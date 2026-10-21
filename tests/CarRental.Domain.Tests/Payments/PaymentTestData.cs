using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Domain.Tests.Payments;

internal static class PaymentTestData
{
    /// <summary>28/06/2027 10:00 in Tunis.</summary>
    public static readonly DateTimeOffset Now = new(2027, 6, 28, 9, 0, 0, TimeSpan.Zero);

    public static readonly Guid AgencyId = Guid.CreateVersion7();

    public static readonly Guid SessionId = Guid.CreateVersion7();

    public static readonly AuditActor Agent = new(Guid.CreateVersion7(), "agent@demo.local", "203.0.113.5", "corr-agent");

    public static readonly AuditActor Manager = new(Guid.CreateVersion7(), "gerant@demo.local", "203.0.113.6", "corr-manager");

    public static Money Tnd(decimal amount) => Money.Tnd(amount);

    public static CounterPaymentDetails Counter(PaymentMethod method, decimal amount, string? reference = null, bool awaiting = false) => new(
        AgencyId,
        Guid.CreateVersion7(),
        method,
        Tnd(amount),
        reference ?? (PaymentMethods.RequiresReference(method) ? "REF-0001" : null),
        CashSessionId: method == PaymentMethod.Cash ? SessionId : null,
        AwaitingConfirmation: awaiting);

    public static Payment Captured(PaymentMethod method, decimal amount) =>
        Payment.RecordCounter(Counter(method, amount), Agent, Now).Value;

    public static Payment CapturedCash(decimal amount) => Captured(PaymentMethod.Cash, amount);

    public static Payment PendingTransfer(decimal amount) =>
        Payment.RecordCounter(Counter(PaymentMethod.BankTransfer, amount, "VIR-2027-0042", awaiting: true), Agent, Now).Value;

    public static Payment Online(decimal amount) => Online(amount, OnlinePaymentFlow.PreAuthorization);

    public static Payment Online(decimal amount, OnlinePaymentFlow flow, TimeSpan? validity = null, bool isLink = false) =>
        Payment.StartOnline(OnlineDetails(amount, flow, validity, isLink), AuditActor.System("system:web"), Now).Value;

    public static OnlinePaymentDetails OnlineDetails(decimal amount, OnlinePaymentFlow flow = OnlinePaymentFlow.PreAuthorization, TimeSpan? validity = null, bool isLink = false) => new(
        AgencyId,
        Guid.CreateVersion7(),
        Tnd(amount),
        flow,
        "/fr/reserver/confirmation/LOC-2027-000123",
        "/fr/reserver/paiement?echec=1",
        Now + (validity ?? TimeSpan.FromHours(1)),
        isLink);

    /// <summary>An online payment confirmed by the gateway in <paramref name="state"/> (pre-authorized or deposited in full).</summary>
    public static Payment OnlineConfirmed(decimal amount, GatewayPaymentState state, OnlinePaymentFlow flow = OnlinePaymentFlow.PreAuthorization)
    {
        var payment = Online(amount, flow);
        payment.RecordGatewayRegistration("8f1c2d34-0000-4000-8000-000000000001", "https://test.clictopay.com/payment/merchants/demo/payment.html?mdOrder=8f1c", Now)
            .IsSuccess.ShouldBeTrue();
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(state, Tnd(amount)), AuditActor.System("system:clictopay"), Now).IsSuccess.ShouldBeTrue();
        payment.DequeueDomainEvents();
        return payment;
    }

    public static CounterRefundDetails CardRefund(decimal amount, string reason = "Annulation client J-5") =>
        new(PaymentMethod.CardTerminal, Tnd(amount), reason);

    public static DepositDetails Deposit(DepositMode mode, decimal amount, string? reference = "AUT-778812") => new(
        AgencyId,
        Guid.CreateVersion7(),
        mode,
        Tnd(amount),
        mode == DepositMode.Cash ? null : reference,
        CashSessionId: mode == DepositMode.Cash ? SessionId : null);
}
