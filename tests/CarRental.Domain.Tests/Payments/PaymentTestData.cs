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

    public static Payment Online(decimal amount) =>
        Payment.StartOnline(AgencyId, Guid.CreateVersion7(), Tnd(amount), AuditActor.System("system:web"), Now).Value;

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
