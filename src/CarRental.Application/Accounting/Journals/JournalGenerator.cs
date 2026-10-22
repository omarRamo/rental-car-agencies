using System.Globalization;
using CarRental.Application.Accounting.Chart;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using Keys = CarRental.Domain.Accounting.AccountingMappingCatalog;

namespace CarRental.Application.Accounting.Journals;

/// <summary>The source facts of the journals of a month of an agency, read through the contracts of the other modules.</summary>
internal sealed record JournalFacts(
    Guid AgencyId,
    AccountingMonth Month,
    IReadOnlyList<SalesDocumentInfo> SalesDocuments,
    IReadOnlyList<ExpenseEntryInfo> Expenses,
    IReadOnlyList<SettledPaymentInfo> Payments,
    IReadOnlyList<DepositMovementInfo> Deposits,
    IReadOnlyList<CashMovementEntryInfo> CashMovements,
    IReadOnlyList<CashSessionTotalsInfo> CashSessions,
    IReadOnlyList<ChannelSettlementInfo> Payouts,
    IReadOnlyList<ClicToPaySettlementFact> Settlements,
    IReadOnlyDictionary<Guid, string> BookingNumbers);

/// <summary>What the bank paid on a day for the lines of the Clic to Pay statements settled that day: gross (refunds deducted) and fees.</summary>
internal sealed record ClicToPaySettlementFact(DateOnly SettledOn, Money Gross, Money Fee);

/// <summary>The entries generated for a month, the operation types without account and the entries that could not balance.</summary>
internal sealed record JournalGeneration(IReadOnlyList<JournalEntry> Entries, IReadOnlyList<string> MissingKeys, IReadOnlyList<string> Errors)
{
    public bool IsComplete => MissingKeys.Count == 0 && Errors.Count == 0;
}

/// <summary>
/// Generates the journal entries of a month from its source facts (ADR-0065), each entry balanced, the piece being the
/// number of the source document and the lettering key the number of the rental file (invoice ↔ payments ↔ deposit
/// retained) or of the expense (expense ↔ its payment):
/// <list type="bullet">
/// <item>sales (VT): an invoice debits the customer with its total due and credits each nature of its lines excluding VAT,
/// the VAT collected per rate and the stamp duty; a credit note the opposite;</item>
/// <item>purchases (AC): an expense debits its category excluding VAT and the deductible VAT, credits the supplier;</item>
/// <item>bank (BQ): payments and refunds by card terminal, cheque, transfer and Clic to Pay (clearing account), expenses
/// paid by bank, Booking.com payouts with the commissions they deduct, Clic to Pay settlements of the merchant statement,
/// deposits retained on a card imprint or a cheque collected;</item>
/// <item>cash (CA): every movement of the cash sessions of the month on the business day of its session, and their counted
/// differences;</item>
/// <item>miscellaneous (OD): deposits held by card imprint or cheque — a liability, never revenue (invariant 8) —, their
/// settlement (a retention only settles the customer, i.e. the invoice of the damages), payments collected by a channel.</item>
/// </list>
/// Cash payments and cash deposits are posted from the cash movements only, never twice.
/// </summary>
internal static class JournalGenerator
{
    public static JournalGeneration Generate(JournalFacts facts, AccountResolver accounts, IJournalTexts texts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(texts);
        var writer = new EntryWriter(facts.Month);
        var context = new Context(facts, accounts, texts, writer);

        foreach (var document in facts.SalesDocuments)
        {
            Sales(context, document);
        }

        foreach (var expense in facts.Expenses)
        {
            Purchase(context, expense);
        }

        foreach (var payment in facts.Payments)
        {
            Payment(context, payment);
        }

        foreach (var deposit in facts.Deposits)
        {
            Deposit(context, deposit);
        }

        foreach (var movement in facts.CashMovements)
        {
            CashMovement(context, movement);
        }

        foreach (var session in facts.CashSessions)
        {
            CashVariance(context, session);
        }

        foreach (var payout in facts.Payouts)
        {
            Payout(context, payout);
        }

        foreach (var settlement in facts.Settlements)
        {
            Settlement(context, settlement);
        }

        return new JournalGeneration([.. JournalOrder.Sort(writer.Entries)], [.. accounts.Missing], writer.Errors);
    }

    /// <summary>The short reference of a record without a number: the random end of its version 7 id.</summary>
    public static string ShortId(Guid id) => id.ToString("N")[^8..].ToUpperInvariant();

    /// <summary>The piece of the movements of a cash session: <c>CA-{day}-{session}</c>.</summary>
    public static string CashPiece(DateOnly businessDate, Guid sessionId) =>
        string.Create(CultureInfo.InvariantCulture, $"CA-{businessDate:yyyyMMdd}-{ShortId(sessionId)[^4..]}");

    private static void Sales(Context context, SalesDocumentInfo document)
    {
        var (accounts, texts) = (context.Accounts, context.Texts);
        var lettering = document.BookingNumber ?? document.Number;
        var label = texts.Text(document.Type == SalesDocumentTypeInfo.Invoice ? JournalText.Invoice : JournalText.CreditNote, document.Number, document.Buyer.Name);
        var entry = new JournalEntryBuilder(Journal.Sales, document.IssueDate, document.Number, label, $"sales:{document.Id:N}");
        entry.Debit(accounts.Resolve(Keys.Customers), document.TotalDue, label, lettering);
        foreach (var line in document.Lines)
        {
            entry.Credit(accounts.Resolve(SalesKeyOf(line.Nature)), line.ExclVat, texts.Text(JournalText.SalesRevenue, texts.NatureName(line.Nature), document.Number));
        }

        foreach (var vat in document.VatTotals.Where(v => !v.Vat.IsZero))
        {
            entry.Credit(accounts.Resolve(Keys.VatCollectedKey(vat.Rate)), vat.Vat, texts.Text(JournalText.SalesVat, Percent(vat.Rate), document.Number));
        }

        if (!document.StampDuty.IsZero)
        {
            entry.Credit(accounts.Resolve(Keys.StampDuty), document.StampDuty, texts.Text(JournalText.SalesStampDuty, document.Number));
        }

        context.Writer.Add(entry);
    }

    private static void Purchase(Context context, ExpenseEntryInfo expense)
    {
        var (accounts, texts) = (context.Accounts, context.Texts);
        var label = texts.Text(JournalText.Expense, expense.Number, expense.Supplier);
        var purchase = new JournalEntryBuilder(Journal.Purchases, expense.Date, expense.Number, label, $"expense:{expense.Id:N}");
        purchase.Debit(accounts.Resolve(ExpenseKeyOf(expense.Category)), expense.ExclVat, texts.Text(JournalText.ExpenseCharge, texts.CategoryName(expense.Category), expense.Supplier));
        purchase.Debit(accounts.Resolve(Keys.VatDeductible), expense.Vat, texts.Text(JournalText.ExpenseVat, expense.Supplier));
        purchase.Credit(accounts.Resolve(Keys.Suppliers), expense.InclVat, label, expense.Number);
        context.Writer.Add(purchase);

        // Paid by bank on its date; paid in cash, the movement of the cash session posts the payment.
        if (expense.PaymentMethod != ExpensePaymentMethodInfo.Cash)
        {
            var paymentLabel = texts.Text(JournalText.ExpensePayment, expense.Number, expense.Supplier, MethodOf(texts, expense.PaymentMethod));
            var payment = new JournalEntryBuilder(Journal.Bank, expense.Date, expense.Number, paymentLabel, $"expense-payment:{expense.Id:N}");
            payment.Debit(accounts.Resolve(Keys.Suppliers), expense.InclVat, paymentLabel, expense.Number);
            payment.Credit(accounts.Resolve(Keys.Bank), expense.InclVat, paymentLabel);
            context.Writer.Add(payment);
        }
    }

    private static void Payment(Context context, SettledPaymentInfo payment)
    {
        if (payment.Method == PaymentMethodInfo.Cash)
        {
            return;
        }

        var (accounts, texts) = (context.Accounts, context.Texts);
        var booking = context.BookingNumber(payment.BookingId);
        var piece = $"REG-{ShortId(payment.PaymentId)}";
        var source = $"payment:{payment.PaymentId:N}";
        if (payment.Method == PaymentMethodInfo.ChannelPrepaid)
        {
            // Collected by the channel, which pays the agency later: the customer is settled, the channel owes the money.
            var prepaidLabel = texts.Text(JournalText.ChannelPrepaid, booking);
            var prepaid = new JournalEntryBuilder(Journal.Miscellaneous, payment.Date, piece, prepaidLabel, source);
            var signed = payment.Direction == PaymentDirectionInfo.Incoming ? payment.Amount : payment.Amount.Negate();
            prepaid.Debit(accounts.Resolve(Keys.BookingComReceivable), signed, prepaidLabel);
            prepaid.Credit(accounts.Resolve(Keys.Customers), signed, prepaidLabel, booking);
            context.Writer.Add(prepaid);
            return;
        }

        var incoming = payment.Direction == PaymentDirectionInfo.Incoming;
        var label = texts.Text(incoming ? JournalText.Payment : JournalText.Refund, texts.MethodName(payment.Method), booking);
        var entry = new JournalEntryBuilder(Journal.Bank, payment.Date, piece, label, source);
        var amount = incoming ? payment.Amount : payment.Amount.Negate();
        entry.Debit(accounts.Resolve(TreasuryKeyOf(payment.Method)), amount, label);
        entry.Credit(accounts.Resolve(Keys.Customers), amount, label, booking);
        context.Writer.Add(entry);
    }

    private static void Deposit(Context context, DepositMovementInfo deposit)
    {
        // Cash deposits are posted from the movements of the cash sessions.
        if (deposit.Mode == DepositModeInfo.Cash)
        {
            return;
        }

        var (accounts, texts) = (context.Accounts, context.Texts);
        var booking = context.BookingNumber(deposit.BookingId);
        var mode = texts.DepositModeName(deposit.Mode);
        var guarantee = accounts.Resolve(deposit.Mode == DepositModeInfo.CardImprint ? Keys.CardImprintDeposits : Keys.ChequeDeposits);
        var piece = $"CAU-{ShortId(deposit.DepositId)}";
        if (deposit.Kind == DepositMovementKindInfo.Taken)
        {
            // A liability to the customer, never revenue (invariant 8).
            var label = texts.Text(JournalText.DepositTaken, mode, booking);
            var taken = new JournalEntryBuilder(Journal.Miscellaneous, deposit.Date, piece, label, $"deposit:{deposit.DepositId:N}:taken");
            taken.Debit(guarantee, deposit.Amount, label);
            taken.Credit(accounts.Resolve(Keys.DepositsReceived), deposit.Amount, label, booking);
            context.Writer.Add(taken);
            return;
        }

        // Settled: the liability ends; the part retained settles the customer (the invoice of the damages), the rest is given back.
        var settledLabel = texts.Text(JournalText.DepositSettled, mode, booking, deposit.Retained.ToString("F", CultureInfo.InvariantCulture), deposit.Returned.ToString("F", CultureInfo.InvariantCulture));
        var settled = new JournalEntryBuilder(Journal.Miscellaneous, deposit.Date, piece, settledLabel, $"deposit:{deposit.DepositId:N}:settled");
        settled.Debit(accounts.Resolve(Keys.DepositsReceived), deposit.Amount, settledLabel, booking);
        settled.Credit(accounts.Resolve(Keys.Customers), deposit.Retained, settledLabel, booking);
        settled.Credit(guarantee, deposit.Returned, settledLabel);
        context.Writer.Add(settled);

        if (deposit.Retained.IsPositive)
        {
            // The retained part of the card imprint (or of the cheque) is collected on the bank account.
            var collectedLabel = texts.Text(JournalText.DepositCollected, mode, booking);
            var collected = new JournalEntryBuilder(Journal.Bank, deposit.Date, piece, collectedLabel, $"deposit:{deposit.DepositId:N}:collected");
            collected.Debit(accounts.Resolve(Keys.Bank), deposit.Retained, collectedLabel);
            collected.Credit(guarantee, deposit.Retained, collectedLabel);
            context.Writer.Add(collected);
        }
    }

    private static void CashMovement(Context context, CashMovementEntryInfo movement)
    {
        var (accounts, texts) = (context.Accounts, context.Texts);
        var cash = accounts.Resolve(Keys.Cash);
        var deposits = accounts.Resolve(Keys.CashDeposits);
        var booking = movement.BookingId is { } bookingId ? context.BookingNumber(bookingId) : null;
        var piece = CashPiece(movement.BusinessDate, movement.SessionId);
        JournalEntryBuilder Entry(JournalText text, params object?[] arguments)
        {
            var label = texts.Text(text, arguments);
            return new JournalEntryBuilder(Journal.Cash, movement.BusinessDate, piece, label, $"cash:{movement.MovementId:N}");
        }

        JournalEntryBuilder? entry = null;
        switch (movement.Kind)
        {
            case CashMovementKindInfo.PaymentReceived:
                entry = Entry(JournalText.CashPayment, booking);
                entry.Debit(cash, movement.Amount).Credit(accounts.Resolve(Keys.Customers), movement.Amount, lettering: booking);
                break;
            case CashMovementKindInfo.RefundPaid:
                entry = Entry(JournalText.CashRefund, booking);
                entry.Debit(accounts.Resolve(Keys.Customers), movement.Amount, lettering: booking).Credit(cash, movement.Amount);
                break;
            case CashMovementKindInfo.DepositReceived:
                entry = Entry(JournalText.CashDepositReceived, booking);
                entry.Debit(deposits, movement.Amount).Credit(accounts.Resolve(Keys.DepositsReceived), movement.Amount, lettering: booking);
                break;
            case CashMovementKindInfo.DepositReturned:
                entry = Entry(JournalText.CashDepositReturned, booking);
                entry.Debit(accounts.Resolve(Keys.DepositsReceived), movement.Amount, lettering: booking).Credit(deposits, movement.Amount);
                break;
            case CashMovementKindInfo.DepositRetained:
                // The liability ends and settles the customer (invariant 8); the cash moves from the deposits to the revenue cash.
                entry = Entry(JournalText.CashDepositRetained, booking);
                entry.Debit(accounts.Resolve(Keys.DepositsReceived), movement.Amount, lettering: booking)
                    .Debit(cash, movement.Amount)
                    .Credit(accounts.Resolve(Keys.Customers), movement.Amount, lettering: booking)
                    .Credit(deposits, movement.Amount);
                break;
            case CashMovementKindInfo.ManualIn:
                entry = Entry(JournalText.CashManualIn, movement.Reason);
                entry.Debit(cash, movement.Amount).Credit(accounts.Resolve(Keys.InternalTransfers), movement.Amount);
                break;
            case CashMovementKindInfo.ManualOut:
                entry = Entry(JournalText.CashManualOut, movement.Reason);
                entry.Debit(accounts.Resolve(Keys.InternalTransfers), movement.Amount).Credit(cash, movement.Amount);
                break;
            case CashMovementKindInfo.Adjustment:
                entry = Entry(JournalText.CashAdjustment, movement.Reason);
                Difference(entry, accounts, cash, movement.RevenueEffect);
                Difference(entry, accounts, deposits, movement.DepositEffect);
                break;
            case CashMovementKindInfo.ExpensePaid:
                entry = Entry(JournalText.CashExpensePaid, movement.ExpenseNumber);
                entry.Debit(accounts.Resolve(Keys.Suppliers), movement.Amount, lettering: movement.ExpenseNumber).Credit(cash, movement.Amount);
                break;
            case CashMovementKindInfo.ExpenseReversed:
                entry = Entry(JournalText.CashExpenseReversed, movement.ExpenseNumber);
                entry.Debit(cash, movement.Amount).Credit(accounts.Resolve(Keys.Suppliers), movement.Amount, lettering: movement.ExpenseNumber);
                break;
            default:
                // Transfers between drawers move the deposits within the same cash: nothing to post.
                break;
        }

        if (entry is not null)
        {
            context.Writer.Add(entry);
        }
    }

    private static void CashVariance(Context context, CashSessionTotalsInfo session)
    {
        if (session.Status == CashSessionStatusInfo.Open || (session.RevenueVariance is not { IsZero: false } && session.DepositVariance is not { IsZero: false }))
        {
            return;
        }

        var (accounts, texts) = (context.Accounts, context.Texts);
        var label = texts.Text(JournalText.CashVariance, session.BusinessDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), session.CashierName);
        var entry = new JournalEntryBuilder(Journal.Cash, session.BusinessDate, CashPiece(session.BusinessDate, session.SessionId), label, $"cash-variance:{session.SessionId:N}");
        Difference(entry, accounts, accounts.Resolve(Keys.Cash), session.RevenueVariance ?? Money.Zero);
        Difference(entry, accounts, accounts.Resolve(Keys.CashDeposits), session.DepositVariance ?? Money.Zero);
        context.Writer.Add(entry);
    }

    private static void Payout(Context context, ChannelSettlementInfo settlement)
    {
        if (settlement is not { PayoutId: { } payoutId, Payout: { } payout, PayoutRecordedAtUtc: { } recordedAt })
        {
            return;
        }

        var (accounts, texts) = (context.Accounts, context.Texts);
        var month = string.Create(CultureInfo.InvariantCulture, $"{settlement.Year:D4}-{settlement.Month:D2}");
        var piece = settlement.PayoutReference ?? $"BKG-{month}";
        var label = texts.Text(JournalText.Payout, settlement.ChannelName, month);
        var entry = new JournalEntryBuilder(Journal.Bank, TunisTime.DateOf(recordedAt), piece, label, $"payout:{payoutId:N}");

        // The channel pays the net of its prepaid bookings minus the commissions of those paid on site: the receivable of the
        // prepaid bookings is settled by the payout and the commissions it deducts; what differs waits on the suspense account.
        entry.Debit(accounts.Resolve(Keys.Bank), payout, label);
        entry.Debit(accounts.Resolve(Keys.ExpenseCommissionBookingCom), settlement.DeductedCommission, texts.Text(JournalText.PayoutCommission, settlement.ChannelName, month));
        entry.Credit(accounts.Resolve(Keys.BookingComReceivable), settlement.PrepaidGross, label);
        entry.Credit(accounts.Resolve(Keys.Suspense), payout - settlement.ExpectedSettlement, texts.Text(JournalText.PayoutDifference, settlement.ChannelName, month));
        context.Writer.Add(entry);
    }

    private static void Settlement(Context context, ClicToPaySettlementFact settlement)
    {
        var (accounts, texts) = (context.Accounts, context.Texts);
        var day = settlement.SettledOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var label = texts.Text(JournalText.ClicToPaySettlement, day);
        var piece = string.Create(CultureInfo.InvariantCulture, $"CTP-{settlement.SettledOn:yyyyMMdd}");
        var entry = new JournalEntryBuilder(Journal.Bank, settlement.SettledOn, piece, label, $"clictopay-settlement:{settlement.SettledOn:yyyyMMdd}");
        entry.Debit(accounts.Resolve(Keys.Bank), settlement.Gross - settlement.Fee, label);
        entry.Debit(accounts.Resolve(Keys.ExpenseCommissionClicToPay), settlement.Fee, texts.Text(JournalText.ClicToPayFee, day));
        entry.Credit(accounts.Resolve(Keys.ClicToPayClearing), settlement.Gross, label);
        context.Writer.Add(entry);
    }

    /// <summary>A signed difference of a cash pocket: a surplus credits the surplus account, a shortage debits the shortage account.</summary>
    private static void Difference(JournalEntryBuilder entry, AccountResolver accounts, string pocket, Money signed)
    {
        if (signed.IsZero)
        {
            return;
        }

        entry.Debit(pocket, signed);
        entry.Credit(accounts.Resolve(signed.IsPositive ? Keys.CashSurplus : Keys.CashShortage), signed);
    }

    private static string Percent(decimal rate) => (rate * 100m).ToString("0.##", CultureInfo.InvariantCulture);

    private static string MethodOf(IJournalTexts texts, ExpensePaymentMethodInfo method) => method switch
    {
        ExpensePaymentMethodInfo.Card => texts.MethodName(PaymentMethodInfo.CardTerminal),
        ExpensePaymentMethodInfo.Cheque => texts.MethodName(PaymentMethodInfo.Cheque),
        ExpensePaymentMethodInfo.BankTransfer => texts.MethodName(PaymentMethodInfo.BankTransfer),
        _ => texts.MethodName(PaymentMethodInfo.Cash),
    };

    /// <summary>The operation type of an accounting nature of an invoice line.</summary>
    internal static string SalesKeyOf(InvoiceLineNatureInfo nature) => nature switch
    {
        InvoiceLineNatureInfo.Rental => Keys.SalesRental,
        InvoiceLineNatureInfo.Discount => Keys.SalesDiscount,
        InvoiceLineNatureInfo.Extras => Keys.SalesExtras,
        InvoiceLineNatureInfo.Protection => Keys.SalesProtection,
        InvoiceLineNatureInfo.Delivery => Keys.SalesDelivery,
        InvoiceLineNatureInfo.DailyContribution => Keys.SalesDailyContribution,
        InvoiceLineNatureInfo.ExtraMileage => Keys.SalesExtraMileage,
        InvoiceLineNatureInfo.Fuel => Keys.SalesFuel,
        InvoiceLineNatureInfo.LateReturn => Keys.SalesLateReturn,
        InvoiceLineNatureInfo.Cleaning => Keys.SalesCleaning,
        InvoiceLineNatureInfo.Damage => Keys.SalesDamage,
        InvoiceLineNatureInfo.CancellationFee => Keys.SalesCancellationFee,
        InvoiceLineNatureInfo.FineReinvoicing => Keys.SalesFineReinvoicing,
        InvoiceLineNatureInfo.ManagementFee => Keys.SalesManagementFee,
        _ => Keys.SalesOtherCharge,
    };

    /// <summary>The operation type of a category of expense.</summary>
    internal static string ExpenseKeyOf(ExpenseCategoryInfo category) => category switch
    {
        ExpenseCategoryInfo.Fuel => Keys.ExpenseFuel,
        ExpenseCategoryInfo.Maintenance => Keys.ExpenseMaintenance,
        ExpenseCategoryInfo.Repairs => Keys.ExpenseRepairs,
        ExpenseCategoryInfo.Insurance => Keys.ExpenseInsurance,
        ExpenseCategoryInfo.RoadTax => Keys.ExpenseRoadTax,
        ExpenseCategoryInfo.Leasing => Keys.ExpenseLeasing,
        ExpenseCategoryInfo.CommissionBookingCom => Keys.ExpenseCommissionBookingCom,
        ExpenseCategoryInfo.CommissionClicToPay => Keys.ExpenseCommissionClicToPay,
        ExpenseCategoryInfo.PremisesRent => Keys.ExpensePremisesRent,
        ExpenseCategoryInfo.Utilities => Keys.ExpenseUtilities,
        _ => Keys.ExpenseOther,
    };

    /// <summary>The treasury account of a means of payment that goes through the bank.</summary>
    internal static string TreasuryKeyOf(PaymentMethodInfo method) => method switch
    {
        PaymentMethodInfo.CardTerminal => Keys.CardTerminal,
        PaymentMethodInfo.Cheque => Keys.Cheques,
        PaymentMethodInfo.BankTransfer => Keys.BankTransfers,
        PaymentMethodInfo.ClicToPay => Keys.ClicToPayClearing,
        PaymentMethodInfo.Cash => Keys.Cash,
        _ => Keys.Bank,
    };

    private sealed record Context(JournalFacts Facts, AccountResolver Accounts, IJournalTexts Texts, EntryWriter Writer)
    {
        public string BookingNumber(Guid bookingId) => Facts.BookingNumbers.GetValueOrDefault(bookingId) ?? $"LOC-{ShortId(bookingId)}";
    }

    /// <summary>Collects the built entries; an entry that cannot balance (a bug in a source) is reported, never posted.</summary>
    private sealed class EntryWriter(AccountingMonth month)
    {
        private readonly List<JournalEntry> _entries = [];
        private readonly List<string> _errors = [];

        public IReadOnlyList<JournalEntry> Entries => _entries;

        public IReadOnlyList<string> Errors => _errors;

        public void Add(JournalEntryBuilder builder)
        {
            if (!month.Contains(builder.Date))
            {
                return;
            }

            var built = builder.Build();
            if (built.IsFailure)
            {
                _errors.Add($"{builder.SourceKey}: {built.Error.Code}");
            }
            else if (built.Value is { } entry)
            {
                _entries.Add(entry);
            }
        }
    }
}
