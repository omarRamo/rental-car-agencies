using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Application.Reporting.Sources;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>
/// The indicators of the reports screen and of its exports (SPEC §8, ADR-0046), permission <c>reports.read</c>: for an
/// agency and a period, the fleet indicators of the snapshots, the vehicles, the channels, the booking pace, the funnel,
/// the handling of the requests, the cancellations and the cash, each compared with the previous period. A day counts once
/// over: the indicators of the period cover its days up to yesterday (Tunis time); the pace looks ahead from today; the
/// deposits are those held now.
/// </summary>
public sealed class ReportQueries
{
    private readonly IUserContext _user;
    private readonly IReferentialQueries _referential;
    private readonly IAppDbContext _db;
    private readonly IBookingsReportingQueries _bookings;
    private readonly IFleetReportingQueries _fleetHistory;
    private readonly IFleetQueries _fleet;
    private readonly IPaymentsQueries _payments;
    private readonly IPaymentsReportingQueries _paymentTotals;
    private readonly IInvoicedAmountsSource _invoices;
    private readonly IBookingChargesSource _charges;
    private readonly IChannelCommissionSource _commissions;
    private readonly IEnumerable<IVehicleCostsSource> _costSources;
    private readonly ISettingsReader _settings;
    private readonly TimeProvider _clock;

    internal ReportQueries(
        IUserContext user,
        IReferentialQueries referential,
        IAppDbContext db,
        IBookingsReportingQueries bookings,
        IFleetReportingQueries fleetHistory,
        IFleetQueries fleet,
        IPaymentsQueries payments,
        IPaymentsReportingQueries paymentTotals,
        IInvoicedAmountsSource invoices,
        IBookingChargesSource charges,
        IChannelCommissionSource commissions,
        IEnumerable<IVehicleCostsSource> costSources,
        ISettingsReader settings,
        TimeProvider clock)
    {
        _user = user;
        _referential = referential;
        _db = db;
        _bookings = bookings;
        _fleetHistory = fleetHistory;
        _fleet = fleet;
        _payments = payments;
        _paymentTotals = paymentTotals;
        _invoices = invoices;
        _charges = charges;
        _commissions = commissions;
        _costSources = costSources;
        _settings = settings;
        _clock = clock;
    }

    public async Task<Result<ReportDashboard>> GetDashboardAsync(ReportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_user.HasPermission(Permissions.ReportsRead))
        {
            return ReportingErrors.Forbidden;
        }

        var requested = ReportPeriod.Create(request.From, request.To);
        if (requested.IsFailure)
        {
            return requested.Error;
        }

        var agency = (await _referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == request.AgencyId);
        if (agency is null)
        {
            return ReportingErrors.AgencyNotFound;
        }

        var now = _clock.GetUtcNow();
        var today = TunisTime.DateOf(now);
        var period = requested.Value;
        var covered = period.Until(today.AddDays(-1));
        var previous = covered is null ? null : period.Previous(covered.Days);
        var hintMonths = await _settings.GetAsync(ReportingSettings.SellHintMonths, agency.Id, cancellationToken);
        var hours = await _settings.GetAsync(PricingSettings.AgencyOpeningHours, agency.Id, cancellationToken);

        var rows = covered is null ? [] : await SnapshotRowsAsync(agency.Id, covered.From, covered.To, cancellationToken);
        var previousRows = previous is null ? null : await SnapshotRowsAsync(agency.Id, previous.From, previous.To, cancellationToken);
        var days = covered is null ? [] : await SnapshotDaysAsync(agency.Id, covered.From, covered.To, cancellationToken);
        var previousDays = previous is null ? null : await SnapshotDaysAsync(agency.Id, previous.From, previous.To, cancellationToken);
        var shares = covered is null ? [] : await RentalSharesAsync(agency.Id, covered.From, covered.To, cancellationToken);
        var hintRows = covered is null
            ? []
            : await SnapshotRowsAsync(agency.Id, covered.To.AddMonths(-hintMonths).AddDays(1), covered.To, cancellationToken);

        var facts = covered is null ? [] : await _bookings.GetBookingsOverlappingAsync(agency.Id, covered.From.AddDays(-31), covered.To, cancellationToken);
        var previousFacts = previous is null ? null : await _bookings.GetBookingsOverlappingAsync(agency.Id, previous.From, previous.To, cancellationToken);
        var requests = covered is null ? [] : await RequestsAsync(agency.Id, covered, cancellationToken);
        var previousRequests = previous is null ? null : await RequestsAsync(agency.Id, previous, cancellationToken);
        var funnel = covered is null ? [] : await FunnelCountsAsync(agency.Id, covered, cancellationToken);
        var previousFunnel = previous is null ? null : await FunnelCountsAsync(agency.Id, previous, cancellationToken);

        return new ReportDashboard(
            agency.Id,
            agency.Name,
            new ReportPeriodInfo(period.From, period.To, covered?.To, previous?.From, previous?.To, period.IsCalendarMonth),
            Coverage(covered?.Days ?? 0, days),
            previous is null ? null : Coverage(previous.Days, previousDays!),
            FleetDayTotals.Of(rows.Select(r => r.Figures)),
            previousRows is null ? null : FleetDayTotals.Of(previousRows.Select(r => r.Figures)),
            covered is null ? [] : Daily(covered, rows),
            Vehicles(rows, hintRows),
            hintMonths,
            ChannelCalculator.Compute(rows, shares, days.Select(d => d.UnattributedRevenue).Sum()),
            await PaceAsync(agency.Id, today, now, cancellationToken),
            FunnelCalculator.Compute(funnel, requests),
            previousFunnel is null ? null : FunnelCalculator.Compute(previousFunnel, previousRequests!),
            RequestQualityCalculator.Compute(requests, hours.OpensAt, hours.ClosesAt),
            previousRequests is null ? null : RequestQualityCalculator.Compute(previousRequests, hours.OpensAt, hours.ClosesAt),
            covered is null ? CancellationCalculator.Compute([], period) : CancellationCalculator.Compute(facts, covered),
            previousFacts is null ? null : CancellationCalculator.Compute(previousFacts, previous!),
            await CashAsync(agency.Id, covered, previous, facts, cancellationToken),
            new ReportSources(
                _invoices is not UnconnectedInvoicedAmounts,
                _commissions is not UnconnectedChannelCommissions,
                _charges is not UnconnectedBookingCharges,
                [.. _costSources.Select(s => s.Name)]),
            now);
    }

    /// <summary>
    /// The reports tile of the daily dashboard for <paramref name="agencyId"/>: the fleet indicators of the default period
    /// (<see cref="ReportPeriod.DefaultFor"/>) up to yesterday, compared with the same days of the previous month. Reads the
    /// snapshots only.
    /// </summary>
    public async Task<Result<ReportHeadline>> GetHeadlineAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.ReportsRead))
        {
            return ReportingErrors.Forbidden;
        }

        var agency = (await _referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == agencyId);
        if (agency is null)
        {
            return ReportingErrors.AgencyNotFound;
        }

        var today = TunisTime.Today(_clock);
        var period = ReportPeriod.DefaultFor(today);
        var covered = period.Until(today.AddDays(-1))!;
        var previous = period.Previous(covered.Days);
        var rows = await SnapshotRowsAsync(agency.Id, covered.From, covered.To, cancellationToken);
        var previousRows = await SnapshotRowsAsync(agency.Id, previous.From, previous.To, cancellationToken);
        var days = await SnapshotDaysAsync(agency.Id, covered.From, covered.To, cancellationToken);
        return new ReportHeadline(
            agency.Id,
            agency.Name,
            new ReportPeriodInfo(period.From, period.To, covered.To, previous.From, previous.To, period.IsCalendarMonth),
            Coverage(covered.Days, days),
            FleetDayTotals.Of(rows.Select(r => r.Figures)),
            FleetDayTotals.Of(previousRows.Select(r => r.Figures)));
    }

    private static ReportCoverage Coverage(int expectedDays, List<SnapshotDay> days) =>
        new(expectedDays, days.Count, days.Count == 0 ? null : days.Max(d => d.ComputedAtUtc));

    private static List<DailyFleetIndicators> Daily(ReportPeriod covered, IReadOnlyList<SnapshotRowFigures> rows)
    {
        var byDay = rows.ToLookup(r => r.Day);
        return [.. covered.EachDay().Select(day => new DailyFleetIndicators(day, FleetDayTotals.Of(byDay[day].Select(r => r.Figures))))];
    }

    private static List<VehicleProfitability> Vehicles(IReadOnlyList<SnapshotRowFigures> rows, IReadOnlyList<SnapshotRowFigures> hintRows)
    {
        var hint = hintRows.ToLookup(r => r.VehicleId);
        return
        [
            .. rows
                .GroupBy(r => r.VehicleId)
                .Select(g =>
                {
                    var latest = g.MaxBy(r => r.Day)!;
                    var window = FleetDayTotals.Of(hint[g.Key].Select(r => r.Figures));
                    return new VehicleProfitability(
                        g.Key,
                        latest.PlateNumber,
                        latest.VehicleName,
                        latest.CategoryCode,
                        FleetDayTotals.Of(g.Select(r => r.Figures)),
                        window.Margin,
                        window.VehicleDays);
                })
                .OrderBy(v => v.PlateNumber, StringComparer.Ordinal),
        ];
    }

    private async Task<List<SnapshotRowFigures>> SnapshotRowsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await _db.Set<VehicleDaySnapshot>()
            .AsNoTracking()
            .Where(s => s.AgencyId == agencyId && s.Day >= from && s.Day <= to)
            .Select(s => new SnapshotRowFigures(
                s.VehicleId,
                s.PlateNumber,
                s.VehicleName,
                s.CategoryCode,
                s.Day,
                s.IsAvailable,
                s.IsRented,
                s.Channel,
                s.RentalRevenue,
                s.Commission,
                s.DirectCosts,
                s.Depreciation))
            .ToListAsync(cancellationToken);

    private async Task<List<SnapshotDay>> SnapshotDaysAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await _db.Set<SnapshotDay>()
            .AsNoTracking()
            .Where(d => d.AgencyId == agencyId && d.Day >= from && d.Day <= to)
            .ToListAsync(cancellationToken);

    private async Task<List<RentalShareFigures>> RentalSharesAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await _db.Set<VehicleDayRental>()
            .AsNoTracking()
            .Where(r => r.AgencyId == agencyId && r.Day >= from && r.Day <= to)
            .Select(r => new RentalShareFigures(r.BookingId, r.Channel, r.Day, r.Revenue, r.Commission))
            .ToListAsync(cancellationToken);

    private Task<IReadOnlyList<BookingFactsInfo>> RequestsAsync(Guid agencyId, ReportPeriod period, CancellationToken cancellationToken) =>
        _bookings.GetRequestsSubmittedAsync(agencyId, TunisTime.StartOfDay(period.From), TunisTime.StartOfDay(period.To.AddDays(1)), cancellationToken);

    /// <summary>
    /// Visits of the search and results steps of the public site of the agency by channel and source: distinct anonymous
    /// visits, plus every event without visit (opted-out browsers, each counted as a visit of its own).
    /// </summary>
    private async Task<List<FunnelCount>> FunnelCountsAsync(Guid agencyId, ReportPeriod period, CancellationToken cancellationToken)
    {
        var from = TunisTime.StartOfDay(period.From);
        var to = TunisTime.StartOfDay(period.To.AddDays(1));
        var events = _db.Set<FunnelEvent>()
            .AsNoTracking()
            .Where(e => e.AgencyId == agencyId
                        && e.OccurredAtUtc >= from
                        && e.OccurredAtUtc < to
                        && (e.Step == FunnelStep.Search || e.Step == FunnelStep.Results));
        var visits = await events
            .Where(e => e.VisitId != null)
            .Select(e => new { e.Channel, e.Source, e.Step, e.VisitId })
            .Distinct()
            .ToListAsync(cancellationToken);
        var anonymous = await events
            .Where(e => e.VisitId == null)
            .GroupBy(e => new { e.Channel, e.Source, e.Step })
            .Select(g => new { g.Key.Channel, g.Key.Source, g.Key.Step, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return
        [
            .. visits.GroupBy(v => (v.Channel, v.Source, v.Step)).Select(g => new FunnelCount(g.Key.Channel, g.Key.Source, g.Key.Step, g.Count())),
            .. anonymous.Select(a => new FunnelCount(a.Channel, a.Source, a.Step, a.Count)),
        ];
    }

    private async Task<PaceReport> PaceAsync(Guid agencyId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var horizons = PaceCalculator.DefaultHorizons;
        var end = today.AddDays(horizons.Max() - 1);
        var lastYearAsOf = today.AddYears(-1);
        var bookings = await _bookings.GetBookingsOverlappingAsync(agencyId, today, end, cancellationToken);
        var fleet = await _fleetHistory.GetVehicleDaysAsync(agencyId, today, end, cancellationToken);
        var lastYear = await _bookings.GetBookingsOverlappingAsync(agencyId, lastYearAsOf, lastYearAsOf.AddDays(horizons.Max() - 1), cancellationToken);

        // The active categories, and an inactive one that still has days sold or vehicles.
        var used = bookings.Select(b => b.CategoryId).Concat(fleet.SelectMany(v => v.Days).Select(d => d.CategoryId)).ToHashSet();
        var categories = (await _fleet.GetCategoriesAsync(agencyId, includeInactive: true, cancellationToken))
            .Where(c => c.IsActive || used.Contains(c.Id))
            .Select(c => new PaceCategory(c.Id, c.AcrissCode))
            .ToList();
        return PaceCalculator.Compute(today, horizons, categories, bookings, fleet, lastYear, lastYearAsOf, now.AddYears(-1));
    }

    private async Task<CashReport> CashAsync(
        Guid agencyId,
        ReportPeriod? covered,
        ReportPeriod? previous,
        IReadOnlyList<BookingFactsInfo> facts,
        CancellationToken cancellationToken)
    {
        var collected = covered is null ? [] : Collected(await _payments.GetDailyTotalsByMethodAsync(covered.From, covered.To, agencyId, cancellationToken));
        var previousCollected = previous is null
            ? Money.Zero
            : Collected(await _payments.GetDailyTotalsByMethodAsync(previous.From, previous.To, agencyId, cancellationToken)).Select(c => c.Net).Sum();
        var invoiced = covered is null ? InvoicedAmountsInfo.None : await _invoices.GetInvoicedAsync(agencyId, covered.From, covered.To, cancellationToken);
        var deposits = await _payments.GetOpenDepositLiabilitiesAsync(agencyId, cancellationToken);

        var returned = covered is null
            ? []
            : facts
                .Where(f => f.Status is BookingStatusInfo.Returned or BookingStatusInfo.Closed
                            && f.ActualReturnAt is { } back
                            && covered.Contains(TunisTime.DateOf(back)))
                .ToList();
        IReadOnlyList<Guid> ids = [.. returned.Select(f => f.BookingId)];
        var paid = ids.Count == 0 ? new Dictionary<Guid, PaymentTotals>() : await _paymentTotals.GetPaymentTotalsByBookingAsync(ids, cancellationToken);
        var charges = ids.Count == 0 ? new Dictionary<Guid, Money>() : await _charges.GetChargesDueAsync(agencyId, ids, cancellationToken);
        var unpaid = returned
            .Select(f => new UnpaidBooking(
                f.BookingId,
                f.Number,
                f.Channel,
                TunisTime.DateOf(f.ActualReturnAt!.Value),
                f.Quote.TotalInclVat + charges.GetValueOrDefault(f.BookingId),
                paid.TryGetValue(f.BookingId, out var totals) ? totals.NetPaid : Money.Zero))
            .Where(u => u.Balance.IsPositive)
            .OrderByDescending(u => u.Balance)
            .ThenBy(u => u.Number, StringComparer.Ordinal)
            .ToList();
        return new CashReport(collected, previousCollected, invoiced, deposits.Total, deposits.Count, deposits.ByMode, unpaid);
    }

    private static List<CollectedByMethod> Collected(IEnumerable<DailyPaymentTotal> totals) =>
        [
            .. totals
                .GroupBy(t => t.Method)
                .OrderBy(g => g.Key)
                .Select(g => new CollectedByMethod(
                    g.Key,
                    g.Select(t => t.Received).Sum(),
                    g.Select(t => t.Refunded).Sum(),
                    g.Sum(t => t.PaymentCount),
                    g.Sum(t => t.RefundCount))),
        ];
}
