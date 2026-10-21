using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Periods;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Accounting.Fakes;

/// <summary>The labels of the entries in the tests: the name of the text and its arguments, easy to assert.</summary>
internal sealed class FakeJournalTexts : IJournalTexts
{
    public string Text(JournalText text, params object?[] arguments) => arguments.Length == 0 ? text.ToString() : $"{text}:{string.Join('|', arguments)}";

    public string NatureName(InvoiceLineNatureInfo nature) => nature.ToString();

    public string CategoryName(ExpenseCategoryInfo category) => category.ToString();

    public string MethodName(PaymentMethodInfo method) => method.ToString();

    public string DepositModeName(DepositModeInfo mode) => mode.ToString();
}

/// <summary>The lock of the months: always granted, each acquisition recorded with its mode.</summary>
internal sealed class RecordingPeriodLock : IAccountingPeriodLock
{
    public List<(string Resource, bool Exclusive)> Acquired { get; } = [];

    public Task<Result> AcquireSharedAsync(string resource, CancellationToken cancellationToken = default)
    {
        Acquired.Add((resource, false));
        return Task.FromResult(Result.Success());
    }

    public Task<Result> AcquireExclusiveAsync(string resource, CancellationToken cancellationToken = default)
    {
        Acquired.Add((resource, true));
        return Task.FromResult(Result.Success());
    }
}

internal sealed class FakeSalesDocuments : IBillingQueries
{
    public List<SalesDocumentInfo> Documents { get; } = [];

    public Task<IReadOnlyList<SalesDocumentInfo>> GetSalesDocumentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SalesDocumentInfo>>([.. Documents.Where(d => d.AgencyId == agencyId && d.IssueDate >= from && d.IssueDate <= to)]);

    public Task<SalesDocumentInfo?> GetDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Documents.FirstOrDefault(d => d.Id == documentId));

    public Task<IReadOnlyList<InvoiceSummaryInfo>> GetInvoicesOfBookingAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InvoiceSummaryInfo>>([]);

    public Task<IReadOnlyList<CustomerOpenAmountInfo>> GetOpenAmountsAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CustomerOpenAmountInfo>>([]);
}

internal sealed class FakeBillingAccounting : IBillingAccountingQueries
{
    public List<ClaimSummaryInfo> Claims { get; } = [];

    public List<PendingInvoicingInfo> FilesToInvoice { get; } = [];

    public Task<IReadOnlyList<ClaimSummaryInfo>> GetClaimsAsync(Guid agencyId, Guid? vehicleId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClaimSummaryInfo>>([.. Claims.Where(c => c.AgencyId == agencyId && (vehicleId is null || c.VehicleId == vehicleId))]);

    public Task<IReadOnlyList<PendingInvoicingInfo>> GetFilesToInvoiceAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PendingInvoicingInfo>>([.. FilesToInvoice]);
}

/// <summary>The cash facts of the journals and reconciliations, given by the tests (sessions, movements, expenses).</summary>
internal sealed class FakeCashFacts : ICashQueries, ICashAccountingQueries
{
    public List<CashSessionTotalsInfo> Sessions { get; } = [];

    public List<CashMovementEntryInfo> Movements { get; } = [];

    public List<ExpenseEntryInfo> Expenses { get; } = [];

    public Task<IReadOnlyList<CashSessionTotalsInfo>> GetSessionTotalsAsync(DateOnly from, DateOnly to, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CashSessionTotalsInfo>>([.. Sessions.Where(s => s.BusinessDate >= from && s.BusinessDate <= to && (agencyId is null || s.AgencyId == agencyId))]);

    public Task<IReadOnlyList<ExpenseEntryInfo>> GetExpensesAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExpenseEntryInfo>>([.. Expenses.Where(e => e.Date >= from && e.Date <= to)]);

    public Task<IReadOnlyList<CashMovementEntryInfo>> GetMovementsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CashMovementEntryInfo>>([.. Movements.Where(m => m.BusinessDate >= from && m.BusinessDate <= to)]);
}

internal sealed class FakePaymentFacts : IPaymentsAccountingQueries
{
    public List<SettledPaymentInfo> Settled { get; } = [];

    public List<DepositMovementInfo> Deposits { get; } = [];

    public List<OnlinePaymentStateInfo> Online { get; } = [];

    public Task<IReadOnlyList<SettledPaymentInfo>> GetSettledPaymentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SettledPaymentInfo>>([.. Settled.Where(p => p.Date >= from && p.Date <= to)]);

    public Task<IReadOnlyList<DepositMovementInfo>> GetDepositMovementsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DepositMovementInfo>>([.. Deposits.Where(d => d.Date >= from && d.Date <= to)]);

    public Task<IReadOnlyList<OnlinePaymentStateInfo>> GetOnlinePaymentsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OnlinePaymentStateInfo>>([.. Online]);

    public Task<IReadOnlyList<SettledPaymentInfo>> GetCashPaymentsOfSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SettledPaymentInfo>>([.. Settled.Where(p => p.CashSessionId is { } id && sessionIds.Contains(id))]);
}

internal sealed class FakeChannelPayouts : IChannelPayouts
{
    public List<ChannelSettlementInfo> Settlements { get; } = [];

    public Task<IReadOnlyList<ChannelSettlementInfo>> GetSettlementsAsync(Guid agencyId, int year, int month, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChannelSettlementInfo>>([.. Settlements.Where(s => s.Year == year && s.Month == month)]);

    public Task<IReadOnlyList<ChannelSettlementInfo>> GetRecordedPayoutsAsync(Guid agencyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChannelSettlementInfo>>(
        [
            .. Settlements.Where(s => s.PayoutRecordedAtUtc is { } at && TunisTime.DateOf(at) >= from && TunisTime.DateOf(at) <= to),
        ]);
}

internal sealed class FakeBookingNumbers : IBookingNumbers
{
    public Dictionary<Guid, string> Numbers { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> GetNumbersAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(Numbers.Where(n => bookingIds.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value));
}

/// <summary>The vehicles and maintenances an expense may name.</summary>
internal sealed class FakeExpenseFleet : IFleetQueries, IFleetCostRecords
{
    public List<VehicleInfo> Vehicles { get; } = [];

    public List<MaintenanceRecordInfo> Maintenances { get; } = [];

    public Guid CategoryId { get; } = Guid.CreateVersion7();

    public VehicleInfo AddVehicle(Guid agencyId, string plate)
    {
        var vehicle = new VehicleInfo(Guid.CreateVersion7(), agencyId, CategoryId, plate, "Renault", "Clio", 2025, TransmissionInfo.Manual, FuelTypeInfo.Petrol, 5, 15_000, VehicleStatusInfo.Active, null, null);
        Vehicles.Add(vehicle);
        return vehicle;
    }

    public Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(Guid? agencyId = null, bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleCategoryInfo>>([]);

    public Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) => Task.FromResult<VehicleCategoryInfo?>(null);

    public Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(Guid agencyId, string acrissCode, CancellationToken cancellationToken = default) => Task.FromResult<VehicleCategoryInfo?>(null);

    public Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default) => Task.FromResult(Vehicles.FirstOrDefault(v => v.Id == vehicleId));

    public Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleInfo>>([.. Vehicles.Where(v => v.CategoryId == categoryId)]);

    public Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(Guid categoryId, Period range, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleAvailabilityInfo>>([]);

    public Task<IReadOnlyList<VehicleDeadlineInfo>> GetDeadlinesAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleDeadlineInfo>>([]);

    public Task<IReadOnlyList<MaintenanceRecordInfo>> GetMaintenancesAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MaintenanceRecordInfo>>([.. Maintenances.Where(m => m.VehicleId == vehicleId)]);
}
