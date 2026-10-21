using CarRental.Application.Payments.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Application.Reporting.Indicators;

/// <summary>A report: the agency and its period (Tunis days, both included).</summary>
public sealed record ReportRequest(Guid AgencyId, DateOnly From, DateOnly To);

/// <summary>
/// The period of a report (ADR-0046): the requested days, the elapsed part the indicators cover (up to yesterday: a day
/// is complete once over) and the period it is compared with; no elapsed day when the period starts today or later.
/// </summary>
public sealed record ReportPeriodInfo(
    DateOnly From,
    DateOnly To,
    DateOnly? CoveredTo,
    DateOnly? PreviousFrom,
    DateOnly? PreviousTo,
    bool IsCalendarMonth)
{
    public bool HasElapsedDays => CoveredTo is not null;
}

/// <summary>How many days of the covered period have their snapshots, and when they were last computed.</summary>
public sealed record ReportCoverage(int ExpectedDays, int ComputedDays, DateTimeOffset? LastComputedAtUtc)
{
    public bool IsComplete => ComputedDays >= ExpectedDays;
}

/// <summary>The fleet indicators of one day (charts and daily table).</summary>
public sealed record DailyFleetIndicators(DateOnly Day, FleetDayTotals Totals);

/// <summary>
/// The profitability of a vehicle over the period, and its margin over the last months of the "sell or replace" hint
/// (<c>reporting.sell_hint_months</c>, counted on the days computed in that window).
/// </summary>
public sealed record VehicleProfitability(
    Guid VehicleId,
    string PlateNumber,
    string VehicleName,
    string CategoryCode,
    FleetDayTotals Totals,
    Money HintMargin,
    int HintDays)
{
    /// <summary>A negative margin over the hint window: selling or replacing the vehicle is worth considering.</summary>
    public bool SuggestSaleOrReplacement => HintDays > 0 && HintMargin.IsNegative;
}

/// <summary>
/// Revenue and margin of a sales channel (ADR-0046): its rented days (one per rented day of each of its files), revenue
/// and commission of those days, and the direct costs and depreciation of the vehicle-days where it was the main rental.
/// </summary>
public sealed record ChannelPerformance(SalesChannel Channel, int Bookings, int RentedDays, Money Revenue, Money Commission, Money AllocatedCosts)
{
    /// <summary>Revenue after commissions.</summary>
    public Money NetRevenue => Revenue - Commission;

    /// <summary>Margin after commissions, direct costs and depreciation of its days.</summary>
    public Money Margin => Revenue - Commission - AllocatedCosts;

    public Money? AverageRentedDayPrice => KpiMath.PerUnit(Revenue, RentedDays);
}

/// <summary>The channels, the costs of the days without rental (allocated to no channel) and the revenue no vehicle carried.</summary>
public sealed record ChannelReport(IReadOnlyList<ChannelPerformance> Channels, Money IdleCosts, Money UnattributedRevenue)
{
    public Money Revenue => Channels.Select(c => c.Revenue).Sum();

    public Money Commission => Channels.Select(c => c.Commission).Sum();

    /// <summary>Margin of the channels minus the idle costs: the margin of the fleet.</summary>
    public Money Margin => Channels.Select(c => c.Margin).Sum() - IdleCosts;

    /// <summary>Share of the revenue made by <paramref name="channel"/>.</summary>
    public decimal? ShareOf(ChannelPerformance channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return KpiMath.Ratio(channel.Revenue.Amount, Revenue.Amount);
    }
}

/// <summary>
/// Days sold in the next <see cref="Days"/> days (from today): firm files, tentative ones (requests, options, awaiting
/// payment), the rentable vehicle-days of the fleet, and the days sold one year earlier at the same lead time.
/// </summary>
public sealed record PaceHorizon(int Days, int SoldDays, int TentativeDays, int CapacityDays, int? LastYearSoldDays)
{
    /// <summary>Firm days sold ÷ rentable vehicle-days.</summary>
    public decimal? Occupancy => KpiMath.Ratio(SoldDays, CapacityDays);

    /// <summary>Firm and tentative days ÷ rentable vehicle-days.</summary>
    public decimal? OccupancyWithTentative => KpiMath.Ratio(SoldDays + TentativeDays, CapacityDays);

    /// <summary>Change of the firm days sold against the same lead time one year earlier.</summary>
    public decimal? ChangeOnLastYear => LastYearSoldDays is { } lastYear ? KpiMath.Change(SoldDays, lastYear) : null;
}

/// <summary>The booking pace of a category.</summary>
public sealed record CategoryPace(Guid CategoryId, string CategoryCode, IReadOnlyList<PaceHorizon> Horizons);

/// <summary>The booking pace (carnet) as of <see cref="AsOf"/>, by category and in total, for each horizon.</summary>
public sealed record PaceReport(DateOnly AsOf, IReadOnlyList<int> Horizons, IReadOnlyList<CategoryPace> Categories, IReadOnlyList<PaceHorizon> Totals);

/// <summary>
/// The funnel of a channel and source (ADR-0046): visits that searched and that saw results (anonymous funnel events),
/// requests submitted and those confirmed since (files of the agency).
/// </summary>
public sealed record FunnelRow(SalesChannel? Channel, string? Source, int Searches, int Results, int Requests, int Confirmed)
{
    public decimal? SearchToRequest => KpiMath.Ratio(Requests, Searches);

    public decimal? RequestToConfirmed => KpiMath.Ratio(Confirmed, Requests);

    public decimal? SearchToConfirmed => KpiMath.Ratio(Confirmed, Searches);
}

/// <summary>The funnel rows (website and WhatsApp, by source) and their total.</summary>
public sealed record FunnelReport(IReadOnlyList<FunnelRow> Rows, FunnelRow Total)
{
    /// <summary>The channels that have a row, in channel order (the website, then WhatsApp: the bot).</summary>
    public IReadOnlyList<SalesChannel> Channels =>
        [.. Rows.Select(r => r.Channel).OfType<SalesChannel>().Distinct().Order()];

    /// <summary>The funnel of one channel, all sources together (its source is <see langword="null"/>).</summary>
    public FunnelRow TotalOf(SalesChannel channel)
    {
        var rows = Rows.Where(r => r.Channel == channel).ToList();
        return new FunnelRow(channel, null, rows.Sum(r => r.Searches), rows.Sum(r => r.Results), rows.Sum(r => r.Requests), rows.Sum(r => r.Confirmed));
    }
}

/// <summary>
/// The handling of the requests submitted in the period (ADR-0046): outcomes, the median time they waited for an agent
/// (time spent in <c>PendingValidation</c> before the decision, waiting for the customer excluded), in clock hours and in
/// opening hours, and how many were decided within the target (2 opening hours, SPEC §2).
/// </summary>
public sealed record RequestQualityRow(
    SalesChannel? Channel,
    int Submitted,
    int Validated,
    int Refused,
    int Expired,
    int Cancelled,
    int Pending,
    TimeSpan? MedianDelay,
    TimeSpan? MedianBusinessDelay,
    int DecidedWithinTarget)
{
    public int Decided => Validated + Refused;

    /// <summary>Refused ÷ decided (validated or refused).</summary>
    public decimal? RefusalRate => KpiMath.Ratio(Refused, Decided);

    /// <summary>Expired without decision ÷ submitted.</summary>
    public decimal? ExpiryRate => KpiMath.Ratio(Expired, Submitted);

    /// <summary>Decided within the target ÷ decided.</summary>
    public decimal? WithinTargetRate => KpiMath.Ratio(DecidedWithinTarget, Decided);
}

/// <summary>Request handling by channel and in total, with the target delay.</summary>
public sealed record RequestQualityReport(IReadOnlyList<RequestQualityRow> Channels, RequestQualityRow Total, TimeSpan Target);

/// <summary>
/// Cancellations and no-shows of the files due to start in the period (ADR-0046): among the files that were confirmed, how
/// many were cancelled and how many did not show up, the fees recorded, and the requests or options cancelled before any
/// confirmation (counted apart).
/// </summary>
public sealed record CancellationRow(SalesChannel? Channel, int FirmBookings, int Cancelled, int NoShows, int CancelledBeforeConfirmation, Money Fees)
{
    public decimal? CancellationRate => KpiMath.Ratio(Cancelled, FirmBookings);

    public decimal? NoShowRate => KpiMath.Ratio(NoShows, FirmBookings);
}

/// <summary>Cancellations and no-shows by channel and in total.</summary>
public sealed record CancellationReport(IReadOnlyList<CancellationRow> Channels, CancellationRow Total);

/// <summary>Money received and paid back with one means of payment over the period.</summary>
public sealed record CollectedByMethod(PaymentMethodInfo Method, Money Received, Money Refunded, int Payments, int Refunds)
{
    public Money Net => Received - Refunded;
}

/// <summary>A file returned in the period that still owes money: due (current quote TTC and charges beyond it) minus net paid.</summary>
public sealed record UnpaidBooking(Guid BookingId, string Number, SalesChannel Channel, DateOnly ReturnedOn, Money Due, Money NetPaid)
{
    public Money Balance => Due - NetPaid;
}

/// <summary>
/// The cash indicators (ADR-0046): collected by means over the period, invoiced (when the billing is connected), the
/// deposits held now (a liability, never revenue) and the unpaid balances of the files returned in the period.
/// </summary>
public sealed record CashReport(
    IReadOnlyList<CollectedByMethod> Collected,
    Money PreviousCollectedNet,
    InvoicedAmountsInfo Invoiced,
    Money OpenDeposits,
    int OpenDepositCount,
    IReadOnlyList<DepositLiabilityByMode> DepositsByMode,
    IReadOnlyList<UnpaidBooking> Unpaid)
{
    public Money CollectedNet => Collected.Select(c => c.Net).Sum();

    public Money UnpaidTotal => Unpaid.Select(u => u.Balance).Sum();
}

/// <summary>Which ports are connected to a provider (the reports say when a figure is still structurally zero).</summary>
public sealed record ReportSources(bool InvoicingConnected, bool CommissionsConnected, bool ChargesConnected, IReadOnlyList<string> CostSources);

/// <summary>
/// The reports tile of the daily dashboard: the fleet indicators of the default period of the reports (ADR-0046) up to
/// yesterday, compared with the same days of the previous month.
/// </summary>
public sealed record ReportHeadline(
    Guid AgencyId,
    string AgencyName,
    ReportPeriodInfo Period,
    ReportCoverage Coverage,
    FleetDayTotals Fleet,
    FleetDayTotals PreviousFleet);

/// <summary>
/// Everything the reports screen and its exports show for an agency and a period; <see cref="PreviousCoverage"/> says
/// how many days of the comparison period have their snapshots (the fleet comparison is partial otherwise).
/// </summary>
public sealed record ReportDashboard(
    Guid AgencyId,
    string AgencyName,
    ReportPeriodInfo Period,
    ReportCoverage Coverage,
    ReportCoverage? PreviousCoverage,
    FleetDayTotals Fleet,
    FleetDayTotals? PreviousFleet,
    IReadOnlyList<DailyFleetIndicators> Daily,
    IReadOnlyList<VehicleProfitability> Vehicles,
    int SellHintMonths,
    ChannelReport Channels,
    PaceReport Pace,
    FunnelReport Funnel,
    FunnelReport? PreviousFunnel,
    RequestQualityReport Requests,
    RequestQualityReport? PreviousRequests,
    CancellationReport Cancellations,
    CancellationReport? PreviousCancellations,
    CashReport Cash,
    ReportSources Sources,
    DateTimeOffset GeneratedAtUtc);
