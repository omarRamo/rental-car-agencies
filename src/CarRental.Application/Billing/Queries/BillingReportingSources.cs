using CarRental.Application.Common.Persistence;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Queries;

/// <summary>
/// The Reporting port of the amounts invoiced (ADR-0046) served by the Billing module: the invoices issued by the agency
/// on the Tunis days of the period minus its credit notes of the same days — HT, VAT and TTC of their lines (the stamp
/// duty, outside the VAT, is not invoiced revenue), and the number of each.
/// </summary>
internal sealed class InvoicedAmountsSource(IAppDbContext db) : IInvoicedAmountsSource
{
    public async Task<InvoicedAmountsInfo> GetInvoicedAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return InvoicedAmountsInfo.None;
        }

        var invoices = await db.Set<Invoice>()
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Where(i => i.AgencyId == agencyId && i.IssueDate >= from && i.IssueDate <= to)
            .Select(i => new { i.TotalExclVat, i.TotalVat, i.TotalInclVat })
            .ToListAsync(cancellationToken);
        var notes = await db.Set<CreditNote>()
            .AsNoTracking()
            .IgnoreAutoIncludes()
            .Where(c => c.AgencyId == agencyId && c.IssueDate >= from && c.IssueDate <= to)
            .Select(c => new { c.TotalExclVat, c.TotalVat, c.TotalInclVat })
            .ToListAsync(cancellationToken);
        return new InvoicedAmountsInfo(
            invoices.Select(i => i.TotalExclVat).Concat(notes.Select(c => c.TotalExclVat)).Sum(),
            invoices.Select(i => i.TotalVat).Concat(notes.Select(c => c.TotalVat)).Sum(),
            invoices.Select(i => i.TotalInclVat).Concat(notes.Select(c => c.TotalInclVat)).Sum(),
            invoices.Count,
            notes.Count);
    }
}

/// <summary>
/// The direct costs of the claims of the vehicles (Reporting source <c>claims</c>, ADR-0046 and ADR-0059): each cost of a
/// claim on its date, each reimbursement as a negative cost on its date — amounts as paid (the claims record no VAT
/// split). A repair recorded on a claim is not recorded again as a cost of the vehicle in the Fleet module, so that no cost
/// counts twice in the margin.
/// </summary>
internal sealed class ClaimsVehicleCostsSource(IAppDbContext db) : IVehicleCostsSource
{
    public const string SourceName = "claims";

    public string Name => SourceName;

    public async Task<IReadOnlyList<VehicleCostInfo>> GetCostsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return [];
        }

        var costs = await db.Set<Claim>()
            .AsNoTracking()
            .Where(c => c.AgencyId == agencyId)
            .SelectMany(c => c.Costs.Where(k => k.Date >= from && k.Date <= to).Select(k => new { c.VehicleId, k.Date, k.Amount, k.Label }))
            .ToListAsync(cancellationToken);
        var reimbursements = await db.Set<Claim>()
            .AsNoTracking()
            .Where(c => c.AgencyId == agencyId)
            .SelectMany(c => c.Reimbursements.Where(r => r.Date >= from && r.Date <= to).Select(r => new { c.VehicleId, r.Date, r.Amount, r.Label }))
            .ToListAsync(cancellationToken);
        return
        [
            .. costs.Select(c => new VehicleCostInfo(c.VehicleId, c.Amount, c.Date, c.Date, c.Label)),
            .. reimbursements.Select(r => new VehicleCostInfo(r.VehicleId, r.Amount.Negate(), r.Date, r.Date, r.Label)),
        ];
    }
}
