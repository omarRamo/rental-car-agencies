using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Accounting.Fakes;

/// <summary>Source facts of the journals, as the contracts of the other modules give them.</summary>
internal static class AccountingFacts
{
    public static readonly DateOnly Day = new(2027, 3, 15);

    public static Money Tnd(decimal amount) => Money.Tnd(amount);

    /// <summary>
    /// An invoice of a rental file: 100.000 HT of rental and 10.000 HT of daily contribution at 19 %, a discount of 10.000 HT,
    /// a stamp duty of 1.000 — TTC 119.000, due 120.000 (credit note: the same amounts, negative, without stamp duty).
    /// </summary>
    public static SalesDocumentInfo Invoice(Guid agencyId, string number = "FAC-2027-000001", string? booking = "LOC-2027-000001", DateOnly? day = null, bool creditNote = false)
    {
        var sign = creditNote ? -1m : 1m;
        SalesLineInfo Line(int position, InvoiceLineNatureInfo nature, decimal exclVat) =>
            new(Guid.CreateVersion7(), position, nature, nature.ToString(), nature.ToString(), 1, Tnd(sign * exclVat), Tnd(sign * exclVat), 0.19m, Tnd(sign * exclVat * 0.19m), Tnd(sign * exclVat * 1.19m), null);
        return new SalesDocumentInfo(
            Guid.CreateVersion7(),
            creditNote ? SalesDocumentTypeInfo.CreditNote : SalesDocumentTypeInfo.Invoice,
            number,
            day ?? Day,
            TunisTime.StartOfDay(day ?? Day).AddHours(10),
            agencyId,
            creditNote ? null : InvoiceKindInfo.Rental,
            null,
            null,
            creditNote ? "Geste commercial" : null,
            new BuyerInfo(BuyerKindInfo.Individual, Guid.CreateVersion7(), "Sami Ben Salah", null, null),
            Guid.CreateVersion7(),
            booking,
            null,
            [Line(1, InvoiceLineNatureInfo.Rental, 100m), Line(2, InvoiceLineNatureInfo.DailyContribution, 10m), Line(3, InvoiceLineNatureInfo.Discount, -10m)],
            [new VatTotalInfo(0.19m, Tnd(sign * 100m), Tnd(sign * 19m), Tnd(sign * 119m))],
            Tnd(sign * 100m),
            Tnd(sign * 19m),
            Tnd(sign * 119m),
            creditNote ? Money.Zero : Tnd(1m),
            creditNote ? Tnd(-119m) : Tnd(120m),
            null,
            new EInvoiceInfo(EInvoiceModeInfo.PaperTransitional, EInvoiceStatusInfo.NotRequired, null, null),
            HasPdf: false);
    }

    public static ExpenseEntryInfo Expense(
        string number = "DEP-2027-000001",
        ExpenseCategoryInfo category = ExpenseCategoryInfo.Fuel,
        ExpensePaymentMethodInfo method = ExpensePaymentMethodInfo.BankTransfer,
        decimal exclVat = 100m,
        decimal vat = 19m,
        DateOnly? day = null) =>
        new(Guid.CreateVersion7(), number, day ?? Day, category, "Station Agil", null, "T-1", null, Tnd(exclVat), Tnd(vat), Tnd(exclVat + vat), 0.19m, method, null, null, null, null, null, false);

    public static SettledPaymentInfo Payment(Guid bookingId, PaymentMethodInfo method, decimal amount, bool refund = false, DateOnly? day = null, Guid? cashSession = null) =>
        new(
            Guid.CreateVersion7(),
            bookingId,
            refund ? PaymentDirectionInfo.Refund : PaymentDirectionInfo.Incoming,
            method,
            Tnd(amount),
            day ?? Day,
            TunisTime.StartOfDay(day ?? Day).AddHours(11),
            "REF",
            null,
            cashSession);

    public static DepositMovementInfo Deposit(Guid depositId, Guid bookingId, DepositModeInfo mode, DepositMovementKindInfo kind, decimal amount, decimal retained = 0m) =>
        new(depositId, bookingId, mode, kind, Tnd(amount), Tnd(retained), kind == DepositMovementKindInfo.Settled ? Tnd(amount - retained) : Money.Zero, Day, TunisTime.StartOfDay(Day).AddHours(12), "AUT-1", null);

    public static CashMovementEntryInfo Movement(Guid sessionId, CashMovementKindInfo kind, decimal amount, decimal revenue, decimal deposit, Guid? bookingId = null, string? expenseNumber = null, int sequence = 1) =>
        new(
            Guid.CreateVersion7(),
            sessionId,
            Day,
            "Agent Démo",
            sequence,
            kind,
            Tnd(amount),
            Tnd(revenue),
            Tnd(deposit),
            kind is CashMovementKindInfo.ManualIn or CashMovementKindInfo.ManualOut or CashMovementKindInfo.Adjustment ? "Motif" : null,
            bookingId,
            null,
            null,
            expenseNumber is null ? null : Guid.CreateVersion7(),
            expenseNumber,
            null,
            TunisTime.StartOfDay(Day).AddHours(9));

    public static CashSessionTotalsInfo Session(Guid agencyId, Guid sessionId, CashSessionStatusInfo status, decimal? revenueVariance = null, decimal? depositVariance = null, DateOnly? day = null) =>
        new(
            sessionId,
            agencyId,
            Guid.CreateVersion7(),
            "Agent Démo",
            day ?? Day,
            status,
            Tnd(100m),
            new CashMovementTotals(Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero, Money.Zero),
            Tnd(100m),
            Money.Zero,
            status == CashSessionStatusInfo.Open ? null : Tnd(100m + (revenueVariance ?? 0m)),
            status == CashSessionStatusInfo.Open ? null : Tnd(depositVariance ?? 0m),
            revenueVariance is { } r ? Tnd(r) : status == CashSessionStatusInfo.Open ? null : Money.Zero,
            depositVariance is { } d ? Tnd(d) : status == CashSessionStatusInfo.Open ? null : Money.Zero,
            revenueVariance is { } and not 0m ? "Pièce égarée" : null);

    /// <summary>
    /// The settlement of a channel for March: prepaid gross 1 000.000 (net 850.000), commissions of the bookings paid on
    /// site 30.000, expected 820.000, a payout of <paramref name="payout"/> entered on <paramref name="recordedAt"/>.
    /// </summary>
    public static ChannelSettlementInfo Settlement(decimal? payout, DateTimeOffset? recordedAt, Guid? payoutId = null, Guid? channelId = null) =>
        new(
            channelId ?? Guid.CreateVersion7(),
            "booking-com",
            "Booking.com",
            SalesChannel.BookingCom,
            2027,
            3,
            Bookings: 4,
            Cancelled: 0,
            Gross: Tnd(1200m),
            Commission: Tnd(180m),
            Net: Tnd(1020m),
            PrepaidGross: Tnd(1000m),
            PrepaidNet: Tnd(850m),
            PayOnSiteCommission: Tnd(30m),
            ExpectedSettlement: Tnd(820m),
            PayoutId: payout is null ? null : payoutId ?? Guid.CreateVersion7(),
            Payout: payout is { } amount ? Tnd(amount) : null,
            PayoutReference: payout is null ? null : "VIR-BKG-0327",
            PayoutRecordedAtUtc: recordedAt);
}
