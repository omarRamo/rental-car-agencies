using CarRental.Application.Billing.Contracts;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Queries;

/// <summary><see cref="IBillingAccountingQueries"/>: the claims of an agency and the files of a month still to invoice.</summary>
internal sealed class BillingAccountingQueries(IAppDbContext db, IBookingsBillingQueries bookings) : IBillingAccountingQueries
{
    /// <summary>At most this many files are read for a month (an agency of 30 vehicles closes a few hundred).</summary>
    private const int FileLimit = 1000;

    public async Task<IReadOnlyList<ClaimSummaryInfo>> GetClaimsAsync(Guid agencyId, Guid? vehicleId = null, CancellationToken cancellationToken = default)
    {
        var claims = db.Set<Claim>().AsNoTracking().IgnoreAutoIncludes().Where(c => c.AgencyId == agencyId);
        if (vehicleId is { } vehicle)
        {
            claims = claims.Where(c => c.VehicleId == vehicle);
        }

        var rows = await claims
            .Select(c => new { c.Id, c.AgencyId, c.VehicleId, c.BookingNumber, c.AccidentAtUtc, c.Insurer, c.InsurerClaimNumber, c.Status })
            .ToListAsync(cancellationToken);
        return
        [
            .. rows
                .OrderByDescending(c => c.AccidentAtUtc)
                .Select(c => new ClaimSummaryInfo(c.Id, c.AgencyId, c.VehicleId, c.BookingNumber, c.AccidentAtUtc, c.Insurer, c.InsurerClaimNumber, (ClaimStatusInfo)(int)c.Status)),
        ];
    }

    public async Task<IReadOnlyList<PendingInvoicingInfo>> GetFilesToInvoiceAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var start = TunisTime.StartOfDay(from);
        var end = TunisTime.StartOfDay(to.AddDays(1));
        var files = (await bookings.GetFilesToInvoiceAsync(agencyId, start, FileLimit, cancellationToken))
            .Where(f => f.StatusChangedAtUtc < end)
            .ToList();
        if (files.Count == 0)
        {
            return [];
        }

        var ids = files.Select(f => f.BookingId).ToList();
        var invoiced = (await db.Set<Invoice>()
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(i => i.BookingId != null && ids.Contains(i.BookingId.Value) && (i.Kind == InvoiceKind.Rental || i.Kind == InvoiceKind.CancellationFee))
                .Select(i => new { BookingId = i.BookingId!.Value, i.Kind })
                .ToListAsync(cancellationToken))
            .Select(i => (i.BookingId, i.Kind))
            .ToHashSet();
        return
        [
            .. files
                .Select(f => (File: f, Kind: f.Status == BookingStatusInfo.Closed ? InvoiceKind.Rental : InvoiceKind.CancellationFee))
                .Where(x => !invoiced.Contains((x.File.BookingId, x.Kind)))
                .OrderBy(x => x.File.StatusChangedAtUtc)
                .Select(x => new PendingInvoicingInfo(
                    x.File.BookingId,
                    x.File.Number,
                    (InvoiceKindInfo)(int)x.Kind,
                    x.File.StatusChangedAtUtc,
                    x.Kind == InvoiceKind.Rental ? x.File.TotalInclVat : x.File.CancellationFee ?? Money.Zero)),
        ];
    }
}
