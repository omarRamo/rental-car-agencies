using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;

namespace CarRental.Domain.Tests.Accounting;

/// <summary>Journal entries: always balanced (debits = credits), every line on one side, built from signed amounts.</summary>
public sealed class JournalEntryTests
{
    private static readonly DateOnly Day = new(2027, 3, 15);

    [Fact]
    public void A_balanced_entry_is_created_with_its_lines_in_order()
    {
        var entry = JournalEntry.Create(
            Journal.Sales,
            Day,
            "FAC-2027-000001",
            "Facture FAC-2027-000001 — Sami",
            "sales:1",
            [
                new JournalLineDraft("411", "Client", Money.Tnd(119m), Money.Zero, "LOC-2027-000001"),
                new JournalLineDraft("7061", "Location", Money.Zero, Money.Tnd(100m)),
                new JournalLineDraft("43661", "TVA", Money.Zero, Money.Tnd(19m)),
            ]).Value;

        entry.TotalDebit.ShouldBe(Money.Tnd(119m));
        entry.TotalCredit.ShouldBe(Money.Tnd(119m));
        entry.Lines.Select(l => (l.Position, l.AccountNumber)).ShouldBe([(1, "411"), (2, "7061"), (3, "43661")]);
        entry.Lines[0].LetteringKey.ShouldBe("LOC-2027-000001");
        entry.Lines[0].Balance.ShouldBe(Money.Tnd(119m));
        entry.Lines[1].Balance.ShouldBe(Money.Tnd(-100m));
        entry.ClosingId.ShouldBeNull();
    }

    [Fact]
    public void An_unbalanced_empty_or_invalid_entry_never_exists()
    {
        JournalLineDraft Debit(string account, decimal amount) => new(account, "x", Money.Tnd(amount), Money.Zero);
        JournalLineDraft Credit(string account, decimal amount) => new(account, "x", Money.Zero, Money.Tnd(amount));

        JournalEntry.Create(Journal.Bank, Day, "P", "L", "s", [Debit("532", 10m), Credit("411", 9.999m)]).Error.ShouldBe(AccountingErrors.EntryUnbalanced);
        JournalEntry.Create(Journal.Bank, Day, "P", "L", "s", [Debit("532", 10m)]).Error.ShouldBe(AccountingErrors.EntryEmpty);
        JournalEntry.Create(Journal.Bank, Day, "P", "L", "s", []).Error.ShouldBe(AccountingErrors.EntryEmpty);
        JournalEntry.Create(Journal.Bank, Day, "P", "L", "s", [new JournalLineDraft("532", "x", Money.Tnd(1m), Money.Tnd(1m)), Credit("411", 0m)])
            .Error.ShouldBe(AccountingErrors.EntryInvalid);
        JournalEntry.Create(Journal.Bank, Day, "P", "L", "s", [Debit("0532", 1m), Credit("411", 1m)]).Error.ShouldBe(AccountingErrors.EntryInvalid);
        JournalEntry.Create(Journal.Bank, Day, "P", "L", "s", [Debit("532", -1m), Credit("411", -1m)]).Error.ShouldBe(AccountingErrors.EntryInvalid);
        JournalEntry.Create(Journal.Bank, Day, " ", "L", "s", [Debit("532", 1m), Credit("411", 1m)]).Error.ShouldBe(AccountingErrors.EntryInvalid);
        JournalEntry.Create(Journal.Bank, Day, "P", "L", "", [Debit("532", 1m), Credit("411", 1m)]).Error.ShouldBe(AccountingErrors.EntryInvalid);
        JournalEntry.Create((Journal)99, Day, "P", "L", "s", [Debit("532", 1m), Credit("411", 1m)]).Error.ShouldBe(AccountingErrors.EntryInvalid);
        JournalEntry.Create(Journal.Bank, Day, "P", "L", "s", [new JournalLineDraft("532", "x", Money.Of(1m, Currency.Eur), Money.Zero), Credit("411", 1m)])
            .Error.ShouldBe(AccountingErrors.EntryInvalid);
    }

    [Fact]
    public void The_builder_posts_signed_amounts_on_their_side_and_adds_up_the_same_line()
    {
        var entry = new JournalEntryBuilder(Journal.Cash, Day, "CA-20270315-0001", "Caisse", "cash:1")
            .Debit("541", Money.Tnd(30m))
            .Debit("541", Money.Tnd(20m))
            .Credit("411", Money.Tnd(50m), lettering: "LOC-1")
            .Credit("758", Money.Tnd(-5m))
            .Debit("541", Money.Tnd(-5m))
            .Build()
            .Value!;

        entry.Lines.Select(l => (l.AccountNumber, l.Debit.Amount, l.Credit.Amount, l.LetteringKey))
            .ShouldBe([("541", 45m, 0m, null), ("758", 5m, 0m, null), ("411", 0m, 50m, "LOC-1")], ignoreOrder: true);
        entry.TotalDebit.ShouldBe(entry.TotalCredit);
    }

    [Fact]
    public void A_credit_note_built_like_an_invoice_with_negative_amounts_lands_on_the_opposite_sides()
    {
        var entry = new JournalEntryBuilder(Journal.Sales, Day, "AV-2027-000001", "Avoir", "sales:2")
            .Debit("411", Money.Tnd(-59.5m), lettering: "LOC-1")
            .Credit("7061", Money.Tnd(-50m))
            .Credit("43661", Money.Tnd(-9.5m))
            .Build()
            .Value!;

        entry.Lines.Single(l => l.AccountNumber == "411").Credit.ShouldBe(Money.Tnd(59.5m));
        entry.Lines.Single(l => l.AccountNumber == "7061").Debit.ShouldBe(Money.Tnd(50m));
        entry.Lines.Single(l => l.AccountNumber == "43661").Debit.ShouldBe(Money.Tnd(9.5m));
    }

    [Fact]
    public void Nothing_is_posted_when_every_amount_nets_to_zero_and_an_unbalanced_build_fails()
    {
        new JournalEntryBuilder(Journal.Bank, Day, "P", "L", "s").Debit("532", Money.Tnd(5m)).Credit("532", Money.Tnd(5m)).Build().Value.ShouldBeNull();
        new JournalEntryBuilder(Journal.Bank, Day, "P", "L", "s").Debit("532", Money.Tnd(5m)).Credit("411", Money.Tnd(4m)).Build().Error
            .ShouldBe(AccountingErrors.EntryUnbalanced);
    }

    [Fact]
    public void Long_labels_are_cut_to_the_stored_length()
    {
        var label = new string('é', 300);

        var entry = new JournalEntryBuilder(Journal.Bank, Day, new string('P', 60), label, "s")
            .Debit("532", Money.Tnd(1m))
            .Credit("411", Money.Tnd(1m))
            .Build()
            .Value!;

        entry.Label.Length.ShouldBe(JournalEntry.LabelMaxLength);
        entry.Piece.Length.ShouldBe(JournalEntry.PieceMaxLength);
        entry.Lines.ShouldAllBe(l => l.Label.Length == JournalEntry.LabelMaxLength);
    }

    [FsCheck.Xunit.Property(MaxTest = 300)]
    public Property Signed_amounts_that_add_up_to_zero_always_give_a_balanced_entry()
    {
        var accounts = Gen.Elements("411", "532", "541", "165", "7061", "43661", "401", "6061");
        var amount = Gen.Choose(-2_000_000, 2_000_000).Select(m => m / 1000m);
        var lines = Gen.ListOf(from account in accounts from value in amount select (account, value))
            .Select(l => l.Take(12).ToList())
            .Where(l => l.Count > 0);

        return Prop.ForAll(lines.ToArbitrary(), input =>
        {
            var builder = new JournalEntryBuilder(Journal.Miscellaneous, Day, "P", "L", "s");
            foreach (var (account, value) in input)
            {
                builder.Debit(account, Money.Tnd(value));
            }

            // The last line takes the opposite of the sum, as a generator posts the counterpart of what it debited.
            builder.Credit("471", input.Select(l => Money.Tnd(l.value)).Sum());
            var built = builder.Build();
            return built.IsSuccess && (built.Value is null || (built.Value.TotalDebit == built.Value.TotalCredit
                && built.Value.Lines.All(l => l.Debit.IsPositive ^ l.Credit.IsPositive)));
        });
    }
}
