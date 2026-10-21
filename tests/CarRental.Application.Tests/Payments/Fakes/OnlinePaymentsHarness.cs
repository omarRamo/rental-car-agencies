using CarRental.Application.Common.Security;
using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Payments.Fakes;

/// <summary>
/// The online payment flows wired on the simulated gateway (<see cref="FakeClicToPay"/>), one in-memory database, a fake
/// clock (28/06/2027 09:00 in Tunis) and an active agency, acting as the users the tests choose.
/// </summary>
internal sealed class OnlinePaymentsHarness
{
    public const string ReturnPath = "/fr/reserver/confirmation/LOC-2027-000123";
    public const string FailPath = "/fr/reserver/paiement?echec=1";

    public OnlinePaymentsHarness(bool configuredStatusCodes = true)
    {
        Time = new FakeTimeProvider(FinanceHarness.Start);
        CallLog = new RecordingGatewayCallLog(Db);
        Referential.Agencies.Add(new AgencyInfo(AgencyId, "Agence Démo Tunis", "Tunis", null, null, IsActive: true));
        Options = new ClicToPayOptions
        {
            Enabled = true,
            UseFake = true,
            ReversedStatusCodes = configuredStatusCodes ? "3" : null,
            RefundedStatusCodes = configuredStatusCodes ? "4" : null,
            DeclinedStatusCodes = configuredStatusCodes ? "6" : null,
        };
        Gateway = new FakeClicToPay(Microsoft.Extensions.Options.Options.Create(Options), CallLog, Time);
    }

    public InMemoryFinanceDb Db { get; } = new();

    public FakeTimeProvider Time { get; }

    public FakeReferentialQueries Referential { get; } = new();

    public FakeSettingsReader Settings { get; } = new();

    public InlineUnitOfWork UnitOfWork { get; } = new();

    public RecordingGatewayCallLog CallLog { get; }

    public ClicToPayOptions Options { get; }

    public FakeClicToPay Gateway { get; }

    public Guid AgencyId { get; } = Guid.CreateVersion7();

    public static ScopedUserContext User(string role) => TestUsers.ContextFor(Guid.CreateVersion7(), $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@demo.local", role);

    public static ScopedUserContext System()
    {
        var context = new ScopedUserContext([]);
        context.Initialize(UserContextSnapshot.System("system:tests"));
        return context;
    }

    /// <summary>An anonymous visitor (the public funnel, the return from the payment page).</summary>
    public static ScopedUserContext Visitor() => new([]);

    public OnlinePayments Online(IUserContext? user = null, IClicToPayGateway? gateway = null) =>
        new(Db, UnitOfWork, user ?? Visitor(), gateway ?? Gateway, Referential, Settings, Time, NullLogger<OnlinePayments>.Instance);

    public OnlinePaymentAdministration Admin(IUserContext user) => new(Db, UnitOfWork, user, Online(user), Settings);

    public OnlinePaymentCatchUpJob CatchUpJob() =>
        new(Db, Online(System()), Gateway, Time, NullLogger<OnlinePaymentCatchUpJob>.Instance);

    public OnlinePaymentReconciliationJob ReconciliationJob() =>
        new(Db, Online(System()), Gateway, Time, NullLogger<OnlinePaymentReconciliationJob>.Instance);

    public OnlinePaymentRequest Request(decimal amount, Guid? bookingId = null, string? description = null, bool isLink = false, TimeSpan? validity = null) =>
        new(bookingId ?? Guid.CreateVersion7(), AgencyId, amount, "fr", ReturnPath, FailPath, description, isLink, validity);

    public Payment Payment(Guid paymentId) => Db.Rows<Payment>().Single(p => p.Id == paymentId);

    public IReadOnlyList<Payment> RefundsOf(Guid paymentId) => [.. Db.Rows<Payment>().Where(p => p.OriginalPaymentId == paymentId)];

    public int CallsOf(GatewayOperation operation, Payment payment) => Gateway.CallsFor(payment.OrderNumber).Count(c => c.Operation == operation);

    /// <summary>A pre-authorization started, paid on the payment page and confirmed by the return.</summary>
    public async Task<Payment> PreAuthorizedAsync(decimal amount, CancellationToken cancellationToken, Guid? bookingId = null)
    {
        var online = Online();
        var started = (await online.StartPreAuthorizationAsync(Request(amount, bookingId), cancellationToken)).Value;
        var payment = Payment(started.PaymentId);
        Gateway.Pay(payment.ExternalReference!).IsSuccess.ShouldBeTrue();
        (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, payment.ExternalReference, false), cancellationToken)).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        return payment;
    }

    /// <summary>A one-step payment started, paid on the payment page and confirmed by the return (captured).</summary>
    public async Task<Payment> PaidAsync(decimal amount, CancellationToken cancellationToken)
    {
        var online = Online();
        var started = (await online.StartPaymentAsync(Request(amount), cancellationToken)).Value;
        var payment = Payment(started.PaymentId);
        Gateway.Pay(payment.ExternalReference!).IsSuccess.ShouldBeTrue();
        (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, false), cancellationToken)).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Captured);
        return payment;
    }
}

/// <summary>Settings with stored values per key (the definition default otherwise).</summary>
internal sealed class FakeSettingsReader : ISettingsReader
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Task.FromResult(Values.TryGetValue(definition.Key, out var value) && definition.Parse(value) is { IsSuccess: true } parsed ? parsed.Value : definition.Default);
    }
}

/// <summary>The call log in memory, also written to the in-memory database (read by the back office).</summary>
internal sealed class RecordingGatewayCallLog(InMemoryFinanceDb db) : IGatewayCallLog
{
    private readonly List<GatewayCallLogEntry> _entries = [];

    public IReadOnlyList<GatewayCallLogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public Task RecordAsync(GatewayCallLogEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_entries)
        {
            _entries.Add(entry);
            db.Set<GatewayCallLog>().Add(GatewayCallLog.Record(entry));
        }

        return Task.CompletedTask;
    }
}

/// <summary>A gateway switched off (<c>ClicToPay:Enabled</c> false).</summary>
internal sealed class OffGateway : IClicToPayGateway
{
    public bool IsEnabled => false;

    public Task<Result<GatewayRegisteredOrder>> RegisterAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<GatewayRegisteredOrder>>(PaymentsErrors.GatewayDisabled);

    public Task<Result<GatewayRegisteredOrder>> RegisterPreAuthAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<GatewayRegisteredOrder>>(PaymentsErrors.GatewayDisabled);

    public Task<Result> DepositAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(PaymentsErrors.GatewayDisabled);

    public Task<Result> ReverseAsync(GatewayOrderReference order, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(PaymentsErrors.GatewayDisabled);

    public Task<Result> RefundAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(PaymentsErrors.GatewayDisabled);

    public Task<Result<GatewayOrderStatus>> GetOrderStatusExtendedAsync(GatewayOrderReference order, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<GatewayOrderStatus>>(PaymentsErrors.GatewayDisabled);
}
