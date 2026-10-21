using CarRental.Domain.Common;

namespace CarRental.Application.Reporting.Contracts;

/// <summary>
/// Port of the Reporting module (ADR-0046): the direct costs of the vehicles of an agency over a period, attributed to the
/// snapshots day by day (an amount covering several days is spread evenly over them). Every registered source counts: the
/// costs recorded in the Fleet module come with Reporting; the expenses of package 3B add their own source with
/// <c>services.TryAddEnumerable(ServiceDescriptor.Scoped&lt;IVehicleCostsSource, TSource&gt;())</c> — taking care not to
/// count twice a cost already recorded on the vehicle (a maintenance invoice, an insurance premium).
/// </summary>
public interface IVehicleCostsSource
{
    /// <summary>Stable name of the source, e.g. <c>fleet</c> or <c>expenses</c> (shown in the notes of the reports).</summary>
    string Name { get; }

    /// <summary>Costs of the vehicles of the agency whose period overlaps the Tunis days <paramref name="from"/> to <paramref name="to"/>.</summary>
    Task<IReadOnlyList<VehicleCostInfo>> GetCostsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>
/// A direct cost of a vehicle covering the Tunis days <paramref name="From"/> to <paramref name="To"/> (both included),
/// excluding VAT whenever the source knows it (the fleet records amounts VAT included, without breakdown).
/// </summary>
public sealed record VehicleCostInfo(Guid VehicleId, Money Amount, DateOnly From, DateOnly To, string Label);

/// <summary>
/// Port of the Reporting module (ADR-0046): the commission owed to a sales channel for a booking, excluding VAT, spread
/// over its rented days like its revenue. No commission until the Channels module (phase 5) provides the Booking.com ones:
/// the default implementation returns none. The provider registers its own with
/// <c>services.AddScoped&lt;IChannelCommissionSource, TSource&gt;()</c> (its registration wins over the default).
/// </summary>
public interface IChannelCommissionSource
{
    /// <summary>The commission of each of <paramref name="bookingIds"/> that has one (others are absent).</summary>
    Task<IReadOnlyDictionary<Guid, Money>> GetCommissionsAsync(
        Guid agencyId,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of the Reporting module (ADR-0046): the amounts invoiced by an agency over a period (invoices issued minus credit
/// notes). Nothing until the Billing module (package 3A) provides it: the default implementation returns zero and the
/// reports say that invoicing is not connected yet. The provider registers its own with
/// <c>services.AddScoped&lt;IInvoicedAmountsSource, TSource&gt;()</c>.
/// </summary>
public interface IInvoicedAmountsSource
{
    Task<InvoicedAmountsInfo> GetInvoicedAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Invoiced over a period: totals net of credit notes, and the number of each.</summary>
public sealed record InvoicedAmountsInfo(Money ExclVat, Money Vat, Money InclVat, int InvoiceCount, int CreditNoteCount)
{
    public static InvoicedAmountsInfo None { get; } = new(Money.Zero, Money.Zero, Money.Zero, 0, 0);
}

/// <summary>
/// Port of the Reporting module (ADR-0046): what a customer owes for a booking beyond its current quote (return charges of
/// package 1F-b: fuel, kilometres, late return, cleaning…), VAT included, for the unpaid amounts. None until package 1F-b
/// provides it — charges recorded as quote lines are already in the quote total and need no source.
/// </summary>
public interface IBookingChargesSource
{
    /// <summary>Charges due beyond the quote for each of <paramref name="bookingIds"/> that has some (others are absent).</summary>
    Task<IReadOnlyDictionary<Guid, Money>> GetChargesDueAsync(
        Guid agencyId,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default);
}
