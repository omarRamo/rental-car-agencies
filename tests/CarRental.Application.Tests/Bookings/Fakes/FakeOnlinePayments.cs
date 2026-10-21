using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings.Fakes;

/// <summary>
/// The online payments of the Bookings tests: payments set by the test, captures and releases recorded (in the call
/// journal too, to check they happen outside the unit of work), and the outcome of the next capture.
/// </summary>
internal sealed class FakeOnlinePayments(CallJournal journal) : IOnlinePayments
{
    public bool IsAvailable { get; set; } = true;

    public List<OnlinePaymentInfo> Payments { get; } = [];

    public List<(Guid BookingId, decimal? Amount)> Captures { get; } = [];

    public List<Guid> Reversals { get; } = [];

    public List<OnlinePaymentRequest> Started { get; } = [];

    /// <summary>The error of the next capture (success when <see langword="null"/>).</summary>
    public Error? CaptureError { get; set; }

    /// <summary>A pre-authorized payment of <paramref name="amount"/> for the booking.</summary>
    public OnlinePaymentInfo PreAuthorize(Guid bookingId, decimal amount)
    {
        var payment = Info(bookingId, OnlinePaymentFlowInfo.PreAuthorization, PaymentStatusInfo.PreAuthorized, Money.Tnd(amount));
        Payments.Add(payment);
        return payment;
    }

    public static OnlinePaymentInfo Info(
        Guid bookingId,
        OnlinePaymentFlowInfo flow,
        PaymentStatusInfo status,
        Money amount,
        bool isLink = false,
        DateTimeOffset? expiredAtUtc = null)
    {
        var id = Guid.CreateVersion7();
        var captured = status is PaymentStatusInfo.Captured or PaymentStatusInfo.Refunded ? amount : Money.Zero;
        return new OnlinePaymentInfo(
            id,
            bookingId,
            Guid.CreateVersion7(),
            id.ToString("N"),
            "gateway-" + id.ToString("N")[..8],
            flow,
            isLink,
            status,
            amount,
            captured,
            Money.Zero,
            Money.Zero,
            captured,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            "/dev/clictopay/pay/x",
            DateTimeOffset.UnixEpoch.AddYears(80),
            expiredAtUtc,
            DateTimeOffset.UnixEpoch.AddYears(57),
            "system",
            captured.IsPositive ? DateTimeOffset.UnixEpoch.AddYears(57) : null,
            DateTimeOffset.UnixEpoch.AddYears(57),
            null,
            []);
    }

    public Task<Result<OnlinePaymentRedirect>> StartPreAuthorizationAsync(OnlinePaymentRequest request, CancellationToken cancellationToken = default) =>
        StartAsync(request);

    public Task<Result<OnlinePaymentRedirect>> StartPaymentAsync(OnlinePaymentRequest request, CancellationToken cancellationToken = default) =>
        StartAsync(request);

    public Task<Result<OnlinePaymentReturn>> HandleReturnAsync(OnlinePaymentReturnRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<OnlinePaymentInfo>> CaptureAsync(Guid paymentId, decimal? amount = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<OnlinePaymentInfo>> CaptureBookingAsync(Guid bookingId, decimal? amount = null, CancellationToken cancellationToken = default)
    {
        journal.Add("capture");
        Captures.Add((bookingId, amount));
        if (CaptureError is { } error)
        {
            CaptureError = null;
            return Task.FromResult(Result.Failure<OnlinePaymentInfo>(error));
        }

        var held = Payments.Last(p => p.BookingId == bookingId && p.Status == PaymentStatusInfo.PreAuthorized);
        var captured = held with { Status = PaymentStatusInfo.Captured, CapturedAmount = amount is { } value ? Money.Tnd(value) : held.Amount };
        Payments[Payments.IndexOf(held)] = captured;
        return Task.FromResult(Result.Success(captured));
    }

    public Task<Result<OnlinePaymentInfo>> ReverseAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<IReadOnlyList<OnlinePaymentInfo>>> ReverseBookingAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        Reversals.Add(bookingId);
        return Task.FromResult(Result.Success<IReadOnlyList<OnlinePaymentInfo>>([.. Payments.Where(p => p.BookingId == bookingId)]));
    }

    public Task<Result<OnlineRefundOutcome>> RefundAsync(OnlineRefundRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<OnlineRefundOutcome>> ApproveRefundAsync(Guid refundId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> RejectRefundAsync(Guid refundId, string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<OnlinePaymentInfo>> RefreshStatusAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<OnlinePaymentInfo?> FindAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Payments.FirstOrDefault(p => p.PaymentId == paymentId));

    public Task<IReadOnlyList<OnlinePaymentInfo>> GetBookingOnlinePaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OnlinePaymentInfo>>([.. Payments.Where(p => p.BookingId == bookingId)]);

    private Task<Result<OnlinePaymentRedirect>> StartAsync(OnlinePaymentRequest request)
    {
        Started.Add(request);
        var payment = Info(request.BookingId, OnlinePaymentFlowInfo.OneStep, PaymentStatusInfo.Pending, Money.Tnd(request.Amount), request.IsLink);
        Payments.Add(payment);
        return Task.FromResult(Result.Success(new OnlinePaymentRedirect(payment.PaymentId, payment.OrderNumber, payment.PaymentPageUrl!, payment.ExpiresAtUtc!.Value, false)));
    }
}
