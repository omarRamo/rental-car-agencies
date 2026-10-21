using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Reporting.Sources;

/// <summary>
/// The costs recorded on the vehicles in the Fleet module (document prices over their validity, completed maintenance,
/// other costs), VAT included as recorded: the first <see cref="IVehicleCostsSource"/>, until package 3B adds the expenses.
/// </summary>
internal sealed class FleetVehicleCostsSource(IFleetReportingQueries fleet) : IVehicleCostsSource
{
    public const string SourceName = "fleet";

    public string Name => SourceName;

    public async Task<IReadOnlyList<VehicleCostInfo>> GetCostsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
    [
        .. (await fleet.GetVehicleCostsAsync(agencyId, from, to, cancellationToken))
            .Select(c => new VehicleCostInfo(c.VehicleId, c.Amount, c.From, c.To, c.Label)),
    ];
}

/// <summary>No channel commission until the Channels module (phase 5) provides them.</summary>
internal sealed class UnconnectedChannelCommissions : IChannelCommissionSource
{
    private static readonly IReadOnlyDictionary<Guid, Money> None = new Dictionary<Guid, Money>();

    public Task<IReadOnlyDictionary<Guid, Money>> GetCommissionsAsync(
        Guid agencyId,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default) => Task.FromResult(None);
}

/// <summary>Nothing invoiced until the Billing module (package 3A) provides the invoices.</summary>
internal sealed class UnconnectedInvoicedAmounts : IInvoicedAmountsSource
{
    public Task<InvoicedAmountsInfo> GetInvoicedAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult(InvoicedAmountsInfo.None);
}

/// <summary>No charge beyond the quote until the departure and return package (1F-b) provides the return charges.</summary>
internal sealed class UnconnectedBookingCharges : IBookingChargesSource
{
    private static readonly IReadOnlyDictionary<Guid, Money> None = new Dictionary<Guid, Money>();

    public Task<IReadOnlyDictionary<Guid, Money>> GetChargesDueAsync(
        Guid agencyId,
        IReadOnlyCollection<Guid> bookingIds,
        CancellationToken cancellationToken = default) => Task.FromResult(None);
}
