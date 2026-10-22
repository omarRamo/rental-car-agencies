using CarRental.Application.Accounting.Chart;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Accounting.Fakes.AccountingFacts;

namespace CarRental.Application.Tests.Accounting;

/// <summary>
/// The generation of the journals from the source facts (ADR-0065): every entry balanced, the piece and the lettering of
/// its source, each fact posted once, deposits always on the liability (invariant 8).
/// </summary>
public sealed class JournalGeneratorTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly AccountingMonth March = AccountingMonth.Create(2027, 3).Value;

    [Fact]
    public void An_invoice_debits_the_customer_and_credits_each_nature_the_vat_per_rate_and_the_stamp_duty()
    {
        var invoice = Invoice(AgencyId);

        var entry = Generate(new Facts { Sales = [invoice] }).Entries.ShouldHaveSingleItem();

        entry.Journal.ShouldBe(Journal.Sales);
        entry.Piece.ShouldBe("FAC-2027-000001");
        entry.Date.ShouldBe(Day);
        entry.SourceKey.ShouldBe($"sales:{invoice.Id:N}");
        Balances(entry).ShouldBe(new SortedDictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["411"] = 120m,
            ["7061"] = -100m,
            ["4367"] = -10m,
            ["709"] = 10m,
            ["43661"] = -19m,
            ["4368"] = -1m,
        });
        entry.Lines.Single(l => l.AccountNumber == "411").LetteringKey.ShouldBe("LOC-2027-000001");
        entry.TotalDebit.ShouldBe(entry.TotalCredit);
    }

    [Fact]
    public void A_credit_note_takes_back_the_same_accounts_on_the_opposite_sides()
    {
        var note = Invoice(AgencyId, "AV-2027-000001", creditNote: true);

        var entry = Generate(new Facts { Sales = [note] }).Entries.ShouldHaveSingleItem();

        Balances(entry).ShouldBe(new SortedDictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["411"] = -119m,
            ["7061"] = 100m,
            ["4367"] = 10m,
            ["709"] = -10m,
            ["43661"] = 19m,
        });
        entry.Lines.Single(l => l.AccountNumber == "411").LetteringKey.ShouldBe("LOC-2027-000001", "the credit note letters with its file");
    }

    [Fact]
    public void An_expense_paid_by_bank_posts_its_purchase_and_its_payment_lettered_by_its_number()
    {
        var expense = Expense();

        var entries = Generate(new Facts { Expenses = [expense] }).Entries;

        var purchase = entries.Single(e => e.Journal == Journal.Purchases);
        Balances(purchase).ShouldBe(new SortedDictionary<string, decimal>(StringComparer.Ordinal) { ["6061"] = 100m, ["4365"] = 19m, ["401"] = -119m });
        purchase.Piece.ShouldBe("DEP-2027-000001");
        var payment = entries.Single(e => e.Journal == Journal.Bank);
        Balances(payment).ShouldBe(new SortedDictionary<string, decimal>(StringComparer.Ordinal) { ["401"] = 119m, ["532"] = -119m });
        purchase.Lines.Concat(payment.Lines).Where(l => l.AccountNumber == "401").ShouldAllBe(l => l.LetteringKey == "DEP-2027-000001");
    }

    [Fact]
    public void An_expense_paid_in_cash_is_paid_by_the_movement_of_its_cash_session()
    {
        var session = Guid.CreateVersion7();
        var expense = Expense(method: ExpensePaymentMethodInfo.Cash);

        var entries = Generate(new Facts
        {
            Expenses = [expense],
            Movements = [Movement(session, CashMovementKindInfo.ExpensePaid, 119m, -119m, 0m, expenseNumber: expense.Number)],
        }).Entries;

        entries.Select(e => e.Journal).ShouldBe([Journal.Purchases, Journal.Cash]);
        Balances(entries.Single(e => e.Journal == Journal.Cash)).ShouldBe(new SortedDictionary<string, decimal>(StringComparer.Ordinal) { ["401"] = 119m, ["541"] = -119m });
        entries.Single(e => e.Journal == Journal.Cash).Lines.Single(l => l.AccountNumber == "401").LetteringKey.ShouldBe(expense.Number);
    }

    [Fact]
    public void Payments_go_to_their_treasury_account_and_settle_the_customer_cash_ones_through_the_drawer_only()
    {
        var booking = Guid.CreateVersion7();
        var facts = new Facts
        {
            Payments =
            [
                Payment(booking, PaymentMethodInfo.CardTerminal, 200m),
                Payment(booking, PaymentMethodInfo.CardTerminal, 50m, refund: true),
                Payment(booking, PaymentMethodInfo.ClicToPay, 300m),
                Payment(booking, PaymentMethodInfo.BankTransfer, 10m),
                Payment(booking, PaymentMethodInfo.Cheque, 20m),
                Payment(booking, PaymentMethodInfo.ChannelPrepaid, 400m),
                Payment(booking, PaymentMethodInfo.Cash, 999m),
            ],
            Numbers = { [booking] = "LOC-2027-000007" },
        };

        var entries = Generate(facts).Entries;

        entries.Count.ShouldBe(6, "the cash payment is posted by its cash movement");
        var totals = Totals(entries);
        totals["411"].ShouldBe(-(200m - 50m + 300m + 10m + 20m + 400m));
        totals["532"].ShouldBe(200m - 50m + 10m + 20m, "card terminal, transfer and cheque go to the bank by default");
        totals["5811"].ShouldBe(300m, "Clic to Pay goes to its clearing account");
        totals["4118"].ShouldBe(400m, "a payment collected by the channel is owed by the channel");
        entries.Single(e => e.Lines.Any(l => l.AccountNumber == "4118")).Journal.ShouldBe(Journal.Miscellaneous);
        entries.SelectMany(e => e.Lines).Where(l => l.AccountNumber == "411").ShouldAllBe(l => l.LetteringKey == "LOC-2027-000007");
    }

    [Fact]
    public void A_deposit_is_a_liability_released_or_settling_the_customer_never_revenue()
    {
        var booking = Guid.CreateVersion7();
        var imprint = Guid.CreateVersion7();
        var cheque = Guid.CreateVersion7();
        var facts = new Facts
        {
            Deposits =
            [
                Deposit(imprint, booking, DepositModeInfo.CardImprint, DepositMovementKindInfo.Taken, 500m),
                Deposit(imprint, booking, DepositModeInfo.CardImprint, DepositMovementKindInfo.Settled, 500m, retained: 120m),
                Deposit(cheque, booking, DepositModeInfo.Cheque, DepositMovementKindInfo.Taken, 300m),
                Deposit(cheque, booking, DepositModeInfo.Cheque, DepositMovementKindInfo.Settled, 300m),
                Deposit(Guid.CreateVersion7(), booking, DepositModeInfo.Cash, DepositMovementKindInfo.Taken, 700m),
            ],
        };

        var entries = Generate(facts).Entries;

        var totals = Totals(entries);
        totals["165"].ShouldBe(0m, "every deposit taken was settled");
        totals["5812"].ShouldBe(0m);
        totals["5813"].ShouldBe(0m);
        totals["411"].ShouldBe(-120m, "the retained part only settles the customer — the invoice of the damages");
        totals["532"].ShouldBe(120m, "the retained part of the imprint is collected");
        entries.SelectMany(e => e.Lines).ShouldNotContain(l => l.AccountNumber.StartsWith('7'), "a deposit is never revenue (invariant 8)");
        entries.ShouldNotContain(e => e.Lines.Any(l => l.Debit.Amount == 700m || l.Credit.Amount == 700m), "cash deposits are posted by the cash movements");
        entries.Where(e => e.SourceKey.EndsWith(":taken", StringComparison.Ordinal)).ShouldAllBe(e => e.Lines.Single(l => l.AccountNumber == "165").Credit.IsPositive);
    }

    [Fact]
    public void The_movements_of_the_cash_sessions_post_the_cash_journal_on_their_business_day()
    {
        var session = Guid.CreateVersion7();
        var booking = Guid.CreateVersion7();
        var facts = new Facts
        {
            Movements =
            [
                Movement(session, CashMovementKindInfo.PaymentReceived, 100m, 100m, 0m, booking, sequence: 1),
                Movement(session, CashMovementKindInfo.DepositReceived, 300m, 0m, 300m, booking, sequence: 2),
                Movement(session, CashMovementKindInfo.DepositReturned, 200m, 0m, -200m, booking, sequence: 3),
                Movement(session, CashMovementKindInfo.DepositRetained, 100m, 100m, -100m, booking, sequence: 4),
                Movement(session, CashMovementKindInfo.ManualOut, 50m, -50m, 0m, sequence: 5),
                Movement(session, CashMovementKindInfo.RefundPaid, 20m, -20m, 0m, booking, sequence: 6),
                Movement(session, CashMovementKindInfo.Adjustment, 2m, -2m, 0m, sequence: 7),
                Movement(session, CashMovementKindInfo.DepositTransferIn, 40m, 0m, 40m, booking, sequence: 8),
            ],
            Sessions = [Session(AgencyId, session, CashSessionStatusInfo.Locked, revenueVariance: -1.25m)],
        };

        var entries = Generate(facts).Entries;

        entries.ShouldAllBe(e => e.Journal == Journal.Cash && e.Date == Day);
        entries.Count.ShouldBe(8, "the transfer between drawers posts nothing; the counted difference posts one entry");
        var totals = Totals(entries);
        totals["541"].ShouldBe(100m + 100m - 50m - 20m - 2m - 1.25m, "the revenue cash follows the revenue pocket");
        totals["5412"].ShouldBe(300m - 200m - 100m, "the deposit cash follows the deposit pocket");
        totals["165"].ShouldBe(0m, "the cash deposit was received then settled");
        totals["411"].ShouldBe(-100m + 20m - 100m, "receipts and the retained deposit settle the customer, the refund debits it");
        totals["581"].ShouldBe(50m);
        totals["658"].ShouldBe(2m + 1.25m, "an adjustment and a shortage are charges");
        entries.Select(e => e.Piece).Distinct().ShouldHaveSingleItem().ShouldStartWith("CA-20270315-");
    }

    [Fact]
    public void A_booking_com_payout_settles_the_prepaid_bookings_with_the_commissions_it_deducts_and_parks_the_difference()
    {
        var recordedAt = TunisTime.FromLocal(new DateOnly(2027, 3, 31), new TimeOnly(15, 0));

        var entry = Generate(new Facts { Payouts = [Settlement(815m, recordedAt)] }).Entries.ShouldHaveSingleItem();

        entry.Journal.ShouldBe(Journal.Bank);
        entry.Piece.ShouldBe("VIR-BKG-0327");
        entry.Date.ShouldBe(new DateOnly(2027, 3, 31));
        Balances(entry).ShouldBe(new SortedDictionary<string, decimal>(StringComparer.Ordinal) { ["532"] = 815m, ["6226"] = 180m, ["4118"] = -1000m, ["471"] = 5m });
    }

    [Fact]
    public void A_settlement_of_the_merchant_statement_moves_the_clearing_account_to_the_bank_less_its_fees()
    {
        var entry = Generate(new Facts { Settlements = [new ClicToPaySettlementFact(new DateOnly(2027, 3, 20), Tnd(250m), Tnd(6m))] }).Entries.ShouldHaveSingleItem();

        Balances(entry).ShouldBe(new SortedDictionary<string, decimal>(StringComparer.Ordinal) { ["532"] = 244m, ["6271"] = 6m, ["5811"] = -250m });
    }

    [Fact]
    public void Facts_dated_outside_the_month_are_not_posted_and_every_entry_balances()
    {
        var booking = Guid.CreateVersion7();
        var facts = new Facts
        {
            Sales = [Invoice(AgencyId), Invoice(AgencyId, "AV-2027-000001", creditNote: true)],
            Expenses = [Expense(), Expense("DEP-2027-000002", ExpenseCategoryInfo.Insurance, ExpensePaymentMethodInfo.Cheque, 1000m, 0m)],
            Payments = [Payment(booking, PaymentMethodInfo.CardTerminal, 200m)],
            Payouts = [Settlement(820m, TunisTime.FromLocal(new DateOnly(2027, 4, 2), new TimeOnly(9, 0)))],
        };

        var generation = Generate(facts);

        generation.IsComplete.ShouldBeTrue();
        generation.Entries.ShouldNotContain(e => e.SourceKey.StartsWith("payout:", StringComparison.Ordinal), "a payout entered in April belongs to April");
        generation.Entries.ShouldAllBe(e => e.TotalDebit == e.TotalCredit && March.Contains(e.Date));
        generation.Entries.Select(e => e.TotalDebit).Sum().ShouldBe(generation.Entries.Select(e => e.TotalCredit).Sum());
        generation.Entries.Select(e => e.Journal).ShouldBe(generation.Entries.Select(e => e.Journal).Order(), "in journal order");
    }

    [Fact]
    public void An_operation_without_account_is_listed_and_the_generation_is_incomplete()
    {
        var accounts = new AccountResolver(AccountingMappingCatalog.All.Where(d => d.Key != AccountingMappingCatalog.StampDuty).ToDictionary(d => d.Key, d => d.DefaultAccount, StringComparer.Ordinal));

        var generation = JournalGenerator.Generate(new Facts { Sales = [Invoice(AgencyId)] }.Build(), accounts, new FakeJournalTexts());

        generation.IsComplete.ShouldBeFalse();
        generation.MissingKeys.ShouldBe([AccountingMappingCatalog.StampDuty]);
    }

    private static JournalGeneration Generate(Facts facts) => JournalGenerator.Generate(facts.Build(), AccountingHarness.DefaultAccounts(), new FakeJournalTexts());

    private static SortedDictionary<string, decimal> Balances(JournalEntry entry) =>
        new(entry.Lines.GroupBy(l => l.AccountNumber).ToDictionary(g => g.Key, g => g.Select(l => l.Balance).Sum().Amount), StringComparer.Ordinal);

    private static Dictionary<string, decimal> Totals(IEnumerable<JournalEntry> entries) =>
        entries.SelectMany(e => e.Lines).GroupBy(l => l.AccountNumber).ToDictionary(g => g.Key, g => g.Select(l => l.Balance).Sum().Amount);

    private sealed class Facts
    {
        public List<SalesDocumentInfo> Sales { get; init; } = [];

        public List<ExpenseEntryInfo> Expenses { get; init; } = [];

        public List<SettledPaymentInfo> Payments { get; init; } = [];

        public List<DepositMovementInfo> Deposits { get; init; } = [];

        public List<CashMovementEntryInfo> Movements { get; init; } = [];

        public List<CashSessionTotalsInfo> Sessions { get; init; } = [];

        public List<ChannelSettlementInfo> Payouts { get; init; } = [];

        public List<ClicToPaySettlementFact> Settlements { get; init; } = [];

        public Dictionary<Guid, string> Numbers { get; } = [];

        public JournalFacts Build() => new(AgencyId, March, Sales, Expenses, Payments, Deposits, Movements, Sessions, Payouts, Settlements, Numbers);
    }
}
