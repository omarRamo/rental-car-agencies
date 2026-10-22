using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Accounting;

/// <summary>Justifications of the differences and merchant statements.</summary>
public sealed class ReconciliationTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly AuditActor Accountant = new(Guid.CreateVersion7(), "comptable@demo.local", null, null);
    private static readonly AccountingMonth March = AccountingMonth.Create(2027, 3).Value;
    private static readonly DateTimeOffset At = new(2027, 4, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_justification_keeps_the_difference_its_amount_the_reason_who_and_when()
    {
        var justification = ReconciliationJustification.Justify(AgencyId, March, ReconciliationArea.Cash, "cash:variance:1:revenue", Money.Tnd(-1.25m), "  Pièce égarée ", Accountant, At).Value;

        justification.DifferenceKey.ShouldBe("cash:variance:1:revenue");
        justification.Amount.ShouldBe(Money.Tnd(-1.25m));
        justification.Reason.ShouldBe("Pièce égarée");
        justification.JustifiedAtUtc.ShouldBe(At);
        justification.JustifiedByName.ShouldBe("comptable@demo.local");
        (justification.Year, justification.Month).ShouldBe((2027, 3));
        justification.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ReconciliationDifferenceJustified>();
    }

    [Fact]
    public void A_justification_needs_a_reason_of_five_characters()
    {
        ReconciliationJustification.Justify(AgencyId, March, ReconciliationArea.Cash, "k", Money.Zero, "abc", Accountant, At).Error.ShouldBe(AccountingErrors.JustificationReasonInvalid);
        ReconciliationJustification.Justify(AgencyId, March, ReconciliationArea.Cash, "k", Money.Zero, new string('x', 501), Accountant, At).Error.ShouldBe(AccountingErrors.JustificationReasonInvalid);
        ReconciliationJustification.Justify(Guid.Empty, March, ReconciliationArea.Cash, "k", Money.Zero, "Raison valable", Accountant, At).Error.ShouldBe(AccountingErrors.AgencyNotFound);
        ReconciliationJustification.Justify(AgencyId, March, (ReconciliationArea)9, "k", Money.Zero, "Raison valable", Accountant, At).Error.ShouldBe(AccountingErrors.DifferenceNotFound);
    }

    [Fact]
    public void A_statement_records_valid_lines_in_order_and_names_the_days_it_touches()
    {
        var statement = MerchantStatement.Record(
            AgencyId,
            StatementOrigin.Import,
            @"C:\relevés\mars.csv",
            "generic-csv",
            [
                new StatementLineInput(new DateOnly(2027, 3, 3), " abc ", StatementLineKind.Payment, Money.Tnd(100m), Money.Tnd(1.5m), new DateOnly(2027, 3, 5)),
                new StatementLineInput(new DateOnly(2027, 3, 31), "def", StatementLineKind.Refund, Money.Tnd(20m), SettledOn: new DateOnly(2027, 4, 1)),
            ],
            Accountant,
            At).Value;

        statement.Lines.Select(l => (l.Position, l.Reference, l.SignedAmount.Amount)).ShouldBe([(1, "abc", 100m), (2, "def", -20m)]);
        statement.Lines.ShouldAllBe(l => l.AgencyId == AgencyId, "the lines belong to the agency of their statement");
        statement.Days().ShouldBe([new DateOnly(2027, 3, 3), new DateOnly(2027, 3, 31), new DateOnly(2027, 3, 5), new DateOnly(2027, 4, 1)], ignoreOrder: true);
        statement.Provider.ShouldBe(MerchantStatement.ClicToPay);
    }

    [Fact]
    public void A_statement_refuses_an_invalid_or_empty_list_of_lines()
    {
        StatementLineInput Line(decimal amount, decimal? fee = null, DateOnly? settled = null) =>
            new(new DateOnly(2027, 3, 3), "abc", StatementLineKind.Payment, Money.Tnd(amount), fee is { } f ? Money.Tnd(f) : null, settled);

        MerchantStatement.Record(AgencyId, StatementOrigin.Manual, null, "manual", [], Accountant, At).Error.ShouldBe(AccountingErrors.StatementEmpty);
        MerchantStatement.Record(AgencyId, StatementOrigin.Manual, null, "manual", [Line(0m)], Accountant, At).Error.ShouldBe(AccountingErrors.StatementLineInvalid);
        MerchantStatement.Record(AgencyId, StatementOrigin.Manual, null, "manual", [Line(10m, fee: 11m)], Accountant, At).Error.ShouldBe(AccountingErrors.StatementLineInvalid);
        MerchantStatement.Record(AgencyId, StatementOrigin.Manual, null, "manual", [Line(10m, settled: new DateOnly(2027, 3, 2))], Accountant, At).Error.ShouldBe(AccountingErrors.StatementLineInvalid);
        MerchantStatement.Record(AgencyId, StatementOrigin.Manual, null, "manual", [new StatementLineInput(new DateOnly(2027, 3, 3), " ", StatementLineKind.Payment, Money.Tnd(1m))], Accountant, At)
            .Error.ShouldBe(AccountingErrors.StatementLineInvalid);
        MerchantStatement.Record(AgencyId, StatementOrigin.Manual, null, "manual", [.. Enumerable.Repeat(Line(1m), MerchantStatement.MaxLines + 1)], Accountant, At)
            .Error.ShouldBe(AccountingErrors.StatementTooLarge);
    }
}
