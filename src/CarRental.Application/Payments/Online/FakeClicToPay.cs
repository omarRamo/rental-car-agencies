using System.Collections.Concurrent;
using System.Diagnostics;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Payments.Online;

/// <summary>
/// The simulated Clic to Pay gateway of Development, Testing and the browser tests (ADR-0038), never registered in other
/// environments (<c>ClicToPay:UseFake</c> is refused there at start). Orders live in memory (singleton). Its payment page
/// is an in-app page (<see cref="PagePathPrefix"/>, mapped in Development and Testing only) whose buttons call
/// <see cref="Pay"/>, <see cref="Decline"/> or <see cref="Abandon"/> and send the browser back to the return or failure
/// address of the order, like the real page does. It supports pre-authorization then deposit (at most the pre-authorized
/// amount), reversal, refunds (at most the deposited amount), status reads, and test knobs: faults per call
/// (<see cref="AddFault"/>: no answer, lost answer, error code, invalid answer), status reads lagging behind
/// (<see cref="HoldStatus"/>) and answers without amount details (<see cref="ReportsAmountDetails"/>).
/// </summary>
/// <remarks>
/// Status codes 0, 1 and 2 are those of SPEC §9; the fake uses the BPC values 3 (reversed), 4 (refunded) and 6
/// (declined) for the other states — TODO(spec): the real codes come from SMT; the platform only understands them once
/// configured (<see cref="ClicToPayOptions.ReversedStatusCodes"/>…).
/// </remarks>
public sealed class FakeClicToPay(IOptions<ClicToPayOptions> options, IGatewayCallLog callLog, TimeProvider timeProvider) : IClicToPayGateway
{
    /// <summary>Path of the simulated payment page, followed by the gateway order id.</summary>
    public const string PagePathPrefix = "/dev/clictopay/pay/";

    public const int ReversedStatusCode = 3;
    public const int RefundedStatusCode = 4;
    public const int DeclinedStatusCode = 6;

    public const string DuplicateOrderErrorCode = "1";
    public const string InvalidRequestErrorCode = "4";
    public const string OrderNotFoundErrorCode = "6";
    public const string InvalidStateErrorCode = "7";

    public const string ApprovedActionCode = "0";
    public const string DeclinedActionCode = "116";

    private readonly Lazy<ClicToPayStatusMap> _statusMap = new(() => ClicToPayStatusMap.From(options.Value));
    private readonly ConcurrentDictionary<string, FakeOrder> _orders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FakeOrder> _ordersByNumber = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FakeGatewayCall> _calls = new();
    private readonly Lock _faultsLock = new();
    private readonly List<FaultRule> _faults = [];

    public bool IsEnabled => true;

    /// <summary>Whether status reads report the pre-authorized, deposited and refunded amounts (<c>paymentAmountInfo</c>).</summary>
    public bool ReportsAmountDetails { get; set; } = true;

    /// <summary>Every call received, in order (assertions of the tests).</summary>
    public IReadOnlyList<FakeGatewayCall> Calls => [.. _calls];

    /// <summary>The calls received for one order number.</summary>
    public IReadOnlyList<FakeGatewayCall> CallsFor(string orderNumber) => [.. _calls.Where(c => string.Equals(c.OrderNumber, orderNumber, StringComparison.Ordinal))];

    /// <summary>Makes the next matching calls fail (<see cref="FakeGatewayFault.Times"/> calls, any order when no condition).</summary>
    public void AddFault(FakeGatewayFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        ArgumentOutOfRangeException.ThrowIfLessThan(fault.Times, 1);
        lock (_faultsLock)
        {
            _faults.Add(new FaultRule(fault, fault.Times));
        }
    }

    /// <summary>The next <paramref name="reads"/> status reads of the order return its current state, whatever happens meanwhile.</summary>
    public Result HoldStatus(string orderNumber, int reads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reads, 1);
        if (!_ordersByNumber.TryGetValue(orderNumber, out var order))
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        lock (order.Sync)
        {
            order.HeldView = order.View();
            order.HeldReads = reads;
        }

        return Result.Success();
    }

    /// <summary>
    /// Changes an order as if someone acted outside the platform (merchant portal of Clic to Pay): sets its status code and,
    /// optionally, its debited amount. For the reconciliation tests.
    /// </summary>
    public Result OverrideStatus(string orderNumber, int statusCode, Money? depositedAmount = null)
    {
        if (!_ordersByNumber.TryGetValue(orderNumber, out var order))
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        lock (order.Sync)
        {
            order.Status = statusCode;
            if (depositedAmount is { } deposited)
            {
                order.Approved = order.Approved.IsPositive ? order.Approved : order.Amount;
                order.Deposited = deposited;
            }
        }

        return Result.Success();
    }

    /// <summary>The order behind a payment page, or <see langword="null"/> when unknown.</summary>
    public FakeOrderView? FindOrder(string? gatewayOrderId)
    {
        if (gatewayOrderId is null || !_orders.TryGetValue(gatewayOrderId, out var order))
        {
            return null;
        }

        lock (order.Sync)
        {
            return order.View();
        }
    }

    /// <summary>The order registered with our order number, or <see langword="null"/>.</summary>
    public FakeOrderView? FindOrderByNumber(string orderNumber)
    {
        if (!_ordersByNumber.TryGetValue(orderNumber, out var order))
        {
            return null;
        }

        lock (order.Sync)
        {
            return order.View();
        }
    }

    /// <summary>
    /// The customer pays on the payment page: a pre-authorization is approved (status 1), a one-step payment debited
    /// (status 2). Returns the return address of the order with the gateway order id, where the browser goes next.
    /// </summary>
    public Result<string> Pay(string gatewayOrderId) => Complete(gatewayOrderId, pay: true);

    /// <summary>The card is declined on the payment page (status 6). Returns the failure address of the order.</summary>
    public Result<string> Decline(string gatewayOrderId) => Complete(gatewayOrderId, pay: false);

    /// <summary>The customer leaves the payment page without paying: nothing changes. Returns the failure address.</summary>
    public Result<string> Abandon(string gatewayOrderId)
    {
        if (!_orders.TryGetValue(gatewayOrderId, out var order))
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        return ClicToPayUrls.Append(order.FailUrl, ClicToPayUrls.GatewayOrderIdParameter, order.GatewayOrderId);
    }

    public Task<Result<GatewayRegisteredOrder>> RegisterAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        RegisterCoreAsync(registration, preAuthorization: false, cancellationToken);

    public Task<Result<GatewayRegisteredOrder>> RegisterPreAuthAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        RegisterCoreAsync(registration, preAuthorization: true, cancellationToken);

    public async Task<Result> DepositAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var call = new FakeGatewayCallContext(GatewayOperation.Deposit, operation.OrderNumber, operation.GatewayOrderId, operation.Amount);
        return await OperateAsync(call, operation.RefundId, order =>
        {
            if (!order.PreAuthorization || order.Status != ClicToPayStatusMap.PreAuthorizedCode || !operation.Amount.IsPositive || operation.Amount > order.Approved)
            {
                return false;
            }

            order.Status = ClicToPayStatusMap.DepositedCode;
            order.Deposited = operation.Amount;
            return true;
        }, cancellationToken);
    }

    public async Task<Result> ReverseAsync(GatewayOrderReference order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var call = new FakeGatewayCallContext(GatewayOperation.Reverse, order.OrderNumber, order.GatewayOrderId, null);
        return await OperateAsync(call, refundId: null, fake =>
        {
            if (fake.Status != ClicToPayStatusMap.PreAuthorizedCode)
            {
                return false;
            }

            fake.Status = ReversedStatusCode;
            return true;
        }, cancellationToken);
    }

    public async Task<Result> RefundAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var call = new FakeGatewayCallContext(GatewayOperation.Refund, operation.OrderNumber, operation.GatewayOrderId, operation.Amount);
        return await OperateAsync(call, operation.RefundId, order =>
        {
            var refundable = order.Deposited - order.Refunded;
            if (order.Status is not (ClicToPayStatusMap.DepositedCode or RefundedStatusCode) || !operation.Amount.IsPositive || operation.Amount > refundable)
            {
                return false;
            }

            order.Refunded += operation.Amount;
            order.Status = order.Refunded == order.Deposited ? RefundedStatusCode : ClicToPayStatusMap.DepositedCode;
            return true;
        }, cancellationToken);
    }

    public async Task<Result<GatewayOrderStatus>> GetOrderStatusExtendedAsync(GatewayOrderReference order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var started = timeProvider.GetUtcNow();
        var call = new FakeGatewayCallContext(GatewayOperation.GetOrderStatusExtended, order.OrderNumber, order.GatewayOrderId, null);
        var fault = TakeFault(call);
        if (fault is { Kind: FakeFaultKind.Unavailable or FakeFaultKind.LostResponse })
        {
            await RecordAsync(call, GatewayCallOutcome.Unavailable, started, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayUnavailable;
        }

        if (fault is { Kind: FakeFaultKind.InvalidResponse })
        {
            await RecordAsync(call, GatewayCallOutcome.InvalidResponse, started, httpStatus: 200, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayInvalidResponse;
        }

        if (fault is { Kind: FakeFaultKind.Rejected })
        {
            await RecordAsync(call, GatewayCallOutcome.GatewayError, started, 200, fault.ErrorCode ?? InvalidRequestErrorCode, "Simulated error", cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        var found = order.GatewayOrderId is { } id ? _orders.GetValueOrDefault(id) : _ordersByNumber.GetValueOrDefault(order.OrderNumber);
        if (found is null || !string.Equals(found.OrderNumber, order.OrderNumber, StringComparison.Ordinal))
        {
            await RecordAsync(call, GatewayCallOutcome.GatewayError, started, 200, OrderNotFoundErrorCode, "Order not found", cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        FakeOrderView view;
        lock (found.Sync)
        {
            if (found.HeldReads > 0 && found.HeldView is { } held)
            {
                view = held;
                found.HeldReads--;
            }
            else
            {
                view = found.View();
            }
        }

        var map = _statusMap.Value;
        var details = ReportsAmountDetails;
        var status = new GatewayOrderStatus(
            map.Classify(view.StatusCode),
            view.StatusCode,
            view.OrderNumber,
            view.GatewayOrderId,
            view.Amount,
            details && view.ApprovedAmount.IsPositive ? view.ApprovedAmount : null,
            details && view.DepositedAmount.IsPositive ? view.DepositedAmount : null,
            details ? view.RefundedAmount : null,
            view.ActionCode,
            view.ActionCode switch
            {
                ApprovedActionCode => "Request processed successfully",
                DeclinedActionCode => "Declined (simulated)",
                _ => null,
            });
        await RecordAsync(call with { GatewayOrderId = view.GatewayOrderId }, GatewayCallOutcome.Succeeded, started, 200, orderStatus: view.StatusCode, actionCode: view.ActionCode, cancellationToken: cancellationToken);
        return status;
    }

    private async Task<Result<GatewayRegisteredOrder>> RegisterCoreAsync(GatewayRegistration registration, bool preAuthorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var started = timeProvider.GetUtcNow();
        var operation = preAuthorization ? GatewayOperation.RegisterPreAuth : GatewayOperation.Register;
        var call = new FakeGatewayCallContext(operation, registration.OrderNumber, null, registration.Amount, registration.Description);
        var fault = TakeFault(call);
        var early = await EarlyFaultAsync(call, fault, started, refundId: null, cancellationToken);
        if (early is not null)
        {
            return early.Error;
        }

        if (!registration.Amount.IsPositive || string.IsNullOrWhiteSpace(registration.OrderNumber))
        {
            await RecordAsync(call, GatewayCallOutcome.GatewayError, started, 200, InvalidRequestErrorCode, "Invalid amount or order number", cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        var settings = options.Value;
        var order = new FakeOrder(
            Guid.NewGuid().ToString("D"),
            registration.OrderNumber,
            preAuthorization,
            registration.Amount,
            registration.Language,
            registration.Description,
            ClicToPayUrls.ReturnUrlFor(settings, registration.OrderNumber),
            ClicToPayUrls.FailUrlFor(settings, registration.OrderNumber),
            started);
        if (!_ordersByNumber.TryAdd(registration.OrderNumber, order))
        {
            await RecordAsync(call, GatewayCallOutcome.GatewayError, started, 200, DuplicateOrderErrorCode, "Order number already used", cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        _orders[order.GatewayOrderId] = order;
        var answered = call with { GatewayOrderId = order.GatewayOrderId };
        if (fault is { Kind: FakeFaultKind.LostResponse })
        {
            await RecordAsync(answered, GatewayCallOutcome.Unavailable, started, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayUnavailable;
        }

        await RecordAsync(answered, GatewayCallOutcome.Succeeded, started, 200, cancellationToken: cancellationToken);
        return new GatewayRegisteredOrder(order.GatewayOrderId, PagePathPrefix + order.GatewayOrderId);
    }

    /// <summary>Runs an operation on a registered order: faults first, then the change under the lock of the order.</summary>
    private async Task<Result> OperateAsync(FakeGatewayCallContext call, Guid? refundId, Func<FakeOrder, bool> change, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var fault = TakeFault(call);
        var early = await EarlyFaultAsync(call, fault, started, refundId, cancellationToken);
        if (early is not null)
        {
            return early;
        }

        if (call.GatewayOrderId is not { } id || !_orders.TryGetValue(id, out var order) || !string.Equals(order.OrderNumber, call.OrderNumber, StringComparison.Ordinal))
        {
            await RecordAsync(call, GatewayCallOutcome.GatewayError, started, 200, OrderNotFoundErrorCode, "Order not found", refundId, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        bool changed;
        lock (order.Sync)
        {
            changed = change(order);
        }

        if (!changed)
        {
            await RecordAsync(call, GatewayCallOutcome.GatewayError, started, 200, InvalidStateErrorCode, "Operation not allowed in the current order state", refundId, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        if (fault is { Kind: FakeFaultKind.LostResponse })
        {
            await RecordAsync(call, GatewayCallOutcome.Unavailable, started, refundId: refundId, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayUnavailable;
        }

        await RecordAsync(call, GatewayCallOutcome.Succeeded, started, 200, refundId: refundId, cancellationToken: cancellationToken);
        return Result.Success();
    }

    /// <summary>Faults that stop a call before the gateway processes it; <see langword="null"/> when the call goes on.</summary>
    private async Task<Result?> EarlyFaultAsync(FakeGatewayCallContext call, FakeGatewayFault? fault, DateTimeOffset started, Guid? refundId, CancellationToken cancellationToken)
    {
        switch (fault)
        {
            case { Kind: FakeFaultKind.Unavailable }:
                await RecordAsync(call, GatewayCallOutcome.Unavailable, started, refundId: refundId, cancellationToken: cancellationToken);
                return PaymentsErrors.GatewayUnavailable;
            case { Kind: FakeFaultKind.InvalidResponse }:
                await RecordAsync(call, GatewayCallOutcome.InvalidResponse, started, 200, refundId: refundId, cancellationToken: cancellationToken);
                return PaymentsErrors.GatewayInvalidResponse;
            case { Kind: FakeFaultKind.Rejected, ErrorCode: var code }:
                await RecordAsync(call, GatewayCallOutcome.GatewayError, started, 200, code ?? InvalidRequestErrorCode, "Simulated error", refundId, cancellationToken: cancellationToken);
                return PaymentsErrors.GatewayRejected;
            default:
                return null;
        }
    }

    private Result<string> Complete(string gatewayOrderId, bool pay)
    {
        if (!_orders.TryGetValue(gatewayOrderId, out var order))
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        lock (order.Sync)
        {
            if (order.Status != ClicToPayStatusMap.RegisteredCode)
            {
                return PaymentsErrors.GatewayRejected;
            }

            if (pay)
            {
                order.Approved = order.Amount;
                order.Status = order.PreAuthorization ? ClicToPayStatusMap.PreAuthorizedCode : ClicToPayStatusMap.DepositedCode;
                order.Deposited = order.PreAuthorization ? Money.Zero : order.Amount;
                order.ActionCode = ApprovedActionCode;
            }
            else
            {
                order.Status = DeclinedStatusCode;
                order.ActionCode = DeclinedActionCode;
            }
        }

        return ClicToPayUrls.Append(pay ? order.ReturnUrl : order.FailUrl, ClicToPayUrls.GatewayOrderIdParameter, order.GatewayOrderId);
    }

    private FakeGatewayFault? TakeFault(FakeGatewayCallContext call)
    {
        lock (_faultsLock)
        {
            var rule = _faults.FirstOrDefault(r => r.Remaining > 0 && r.Fault.Operation == call.Operation && (r.Fault.When?.Invoke(call) ?? true));
            if (rule is null)
            {
                return null;
            }

            rule.Remaining--;
            if (rule.Remaining == 0)
            {
                _faults.Remove(rule);
            }

            return rule.Fault;
        }
    }

    private async Task RecordAsync(
        FakeGatewayCallContext call,
        GatewayCallOutcome outcome,
        DateTimeOffset started,
        int? httpStatus = null,
        string? errorCode = null,
        string? errorMessage = null,
        Guid? refundId = null,
        int? orderStatus = null,
        string? actionCode = null,
        CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(new FakeGatewayCall(call.Operation, call.OrderNumber, call.GatewayOrderId, call.Amount, outcome, errorCode));
        await callLog.RecordAsync(
            new GatewayCallLogEntry(
                call.Operation,
                call.OrderNumber,
                outcome,
                started,
                timeProvider.GetUtcNow() - started,
                call.GatewayOrderId,
                call.Amount,
                httpStatus,
                errorCode,
                errorMessage,
                orderStatus,
                actionCode,
                refundId,
                Activity.Current?.TraceId.ToString()),
            cancellationToken);
    }

    private sealed class FaultRule(FakeGatewayFault fault, int remaining)
    {
        public FakeGatewayFault Fault { get; } = fault;

        public int Remaining { get; set; } = remaining;
    }

    private sealed class FakeOrder(
        string gatewayOrderId,
        string orderNumber,
        bool preAuthorization,
        Money amount,
        string? language,
        string? description,
        string returnUrl,
        string failUrl,
        DateTimeOffset createdAtUtc)
    {
        public Lock Sync { get; } = new();

        public string GatewayOrderId { get; } = gatewayOrderId;

        public string OrderNumber { get; } = orderNumber;

        public bool PreAuthorization { get; } = preAuthorization;

        public Money Amount { get; } = amount;

        public string ReturnUrl { get; } = returnUrl;

        public string FailUrl { get; } = failUrl;

        public int Status { get; set; } = ClicToPayStatusMap.RegisteredCode;

        public Money Approved { get; set; }

        public Money Deposited { get; set; }

        public Money Refunded { get; set; }

        public string? ActionCode { get; set; }

        public int HeldReads { get; set; }

        public FakeOrderView? HeldView { get; set; }

        public FakeOrderView View() => new(
            GatewayOrderId,
            OrderNumber,
            PreAuthorization,
            Amount,
            language,
            description,
            Status,
            Approved,
            Deposited,
            Refunded,
            ActionCode,
            ReturnUrl,
            FailUrl,
            createdAtUtc);
    }
}

/// <summary>An order of the simulated gateway, as shown by its payment page.</summary>
public sealed record FakeOrderView(
    string GatewayOrderId,
    string OrderNumber,
    bool IsPreAuthorization,
    Money Amount,
    string? Language,
    string? Description,
    int StatusCode,
    Money ApprovedAmount,
    Money DepositedAmount,
    Money RefundedAmount,
    string? ActionCode,
    string ReturnUrl,
    string FailUrl,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>The customer can still pay or refuse (status 0).</summary>
    public bool IsPayable => StatusCode == ClicToPayStatusMap.RegisteredCode;
}

/// <summary>How a simulated call fails.</summary>
public enum FakeFaultKind
{
    /// <summary>No answer; the gateway did not process the request.</summary>
    Unavailable = 1,

    /// <summary>The gateway processed the request, then the answer was lost (time-out): the case idempotency is for.</summary>
    LostResponse = 2,

    /// <summary>The gateway answers with an error code.</summary>
    Rejected = 3,

    /// <summary>The answer cannot be understood.</summary>
    InvalidResponse = 4,
}

/// <summary>A fault of the simulated gateway for the next matching calls of <see cref="Operation"/>.</summary>
/// <param name="Operation">The operation that fails.</param>
/// <param name="Kind">How it fails.</param>
/// <param name="When">Condition on the call (order number, description…); every call when null.</param>
/// <param name="ErrorCode">Error code of a <see cref="FakeFaultKind.Rejected"/> answer.</param>
/// <param name="Times">Number of calls that fail.</param>
public sealed record FakeGatewayFault(
    GatewayOperation Operation,
    FakeFaultKind Kind,
    Func<FakeGatewayCallContext, bool>? When = null,
    string? ErrorCode = null,
    int Times = 1);

/// <summary>A call received by the simulated gateway (what a fault condition can look at).</summary>
public sealed record FakeGatewayCallContext(GatewayOperation Operation, string OrderNumber, string? GatewayOrderId, Money? Amount, string? Description = null);

/// <summary>A call received by the simulated gateway and how it ended.</summary>
public sealed record FakeGatewayCall(GatewayOperation Operation, string OrderNumber, string? GatewayOrderId, Money? Amount, GatewayCallOutcome Outcome, string? ErrorCode);
