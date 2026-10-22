using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Accounting;

/// <summary>Accounting months: closed only once over, reopened only with a reason; their journals frozen balanced.</summary>
public sealed class AccountingPeriodTests
{
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly AuditActor Accountant = new(Guid.CreateVersion7(), "comptable@demo.local", null, null);
    private static readonly AuditActor Manager = new(Guid.CreateVersion7(), "gerant@demo.local", null, null);
    private static readonly AccountingMonth March = AccountingMonth.Create(2027, 3).Value;
    private static readonly DateTimeOffset At = new(2027, 4, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_month_knows_its_days_and_is_written_yyyy_mm()
    {
        March.FirstDay.ShouldBe(new DateOnly(2027, 3, 1));
        March.LastDay.ShouldBe(new DateOnly(2027, 3, 31));
        AccountingMonth.Create(2028, 2).Value.LastDay.ShouldBe(new DateOnly(2028, 2, 29));
        March.ToString().ShouldBe("2027-03");
        AccountingMonth.Parse("2027-03").Value.ShouldBe(March);
        AccountingMonth.Parse(" 2027-03 ").Value.ShouldBe(March);
        AccountingMonth.Of(new DateOnly(2027, 3, 31)).ShouldBe(March);
        March.Next().ShouldBe(AccountingMonth.Create(2027, 4).Value);
        AccountingMonth.Create(2027, 1).Value.Previous().ShouldBe(AccountingMonth.Create(2026, 12).Value);
        March.Contains(new DateOnly(2027, 3, 1)).ShouldBeTrue();
        March.Contains(new DateOnly(2027, 4, 1)).ShouldBeFalse();
        (March < March.Next()).ShouldBeTrue();
        AccountingMonth.Parse("2027-13").Error.ShouldBe(AccountingErrors.MonthInvalid);
        AccountingMonth.Parse("mars").Error.ShouldBe(AccountingErrors.MonthInvalid);
        AccountingMonth.Parse(null).Error.ShouldBe(AccountingErrors.MonthInvalid);
        AccountingMonth.Create(1999, 12).Error.ShouldBe(AccountingErrors.MonthInvalid);
    }

    [Fact]
    public void A_month_is_over_the_day_after_its_last_day()
    {
        March.IsOverOn(new DateOnly(2027, 3, 31)).ShouldBeFalse();
        March.IsOverOn(new DateOnly(2027, 4, 1)).ShouldBeTrue();
    }

    [Fact]
    public void Closing_needs_the_month_to_be_over_and_happens_once()
    {
        var period = AccountingPeriod.Start(AgencyId, March).Value;
        var closingId = Guid.CreateVersion7();

        period.Close(closingId, new DateOnly(2027, 3, 31), Accountant, At).Error.ShouldBe(AccountingErrors.PeriodNotEnded);
        period.IsClosed.ShouldBeFalse();

        period.Close(closingId, new DateOnly(2027, 4, 1), Accountant, At).IsSuccess.ShouldBeTrue();

        period.IsClosed.ShouldBeTrue();
        period.Status.ShouldBe(AccountingPeriodStatus.Closed);
        period.CurrentClosingId.ShouldBe(closingId);
        period.ClosedAtUtc.ShouldBe(At);
        period.ClosedByName.ShouldBe("comptable@demo.local");
        period.CalendarMonth.ShouldBe(March);
        period.DomainEvents.ShouldHaveSingleItem().ShouldBe(new AccountingPeriodClosed(period.Id, AgencyId, 2027, 3, closingId));
        period.Close(Guid.CreateVersion7(), new DateOnly(2027, 4, 2), Accountant, At).Error.ShouldBe(AccountingErrors.PeriodAlreadyClosed);
    }

    [Fact]
    public void Reopening_needs_a_reason_and_keeps_its_trace()
    {
        var period = AccountingPeriod.Start(AgencyId, March).Value;
        var closingId = Guid.CreateVersion7();
        period.Reopen("Erreur de saisie", Manager, At).Error.ShouldBe(AccountingErrors.PeriodNotClosed);
        period.Close(closingId, new DateOnly(2027, 4, 1), Accountant, At).IsSuccess.ShouldBeTrue();

        period.Reopen("court", Manager, At).Error.ShouldBe(AccountingErrors.ReopenReasonRequired);
        period.Reopen(new string('x', 501), Manager, At).Error.ShouldBe(AccountingErrors.ReopenReasonRequired);
        period.Reopen("  Facture oubliée du 31 mars  ", Manager, At.AddDays(1)).IsSuccess.ShouldBeTrue();

        period.IsClosed.ShouldBeFalse();
        period.CurrentClosingId.ShouldBeNull();
        period.ReopenReason.ShouldBe("Facture oubliée du 31 mars");
        period.ReopenedByName.ShouldBe("gerant@demo.local");
        period.ReopenCount.ShouldBe(1);
        period.DomainEvents.OfType<AccountingPeriodReopened>().ShouldHaveSingleItem().PreviousClosingId.ShouldBe(closingId);

        period.Close(Guid.CreateVersion7(), new DateOnly(2027, 4, 5), Accountant, At.AddDays(3)).IsSuccess.ShouldBeTrue("a reopened month closes again");
    }

    [Fact]
    public void A_closing_freezes_balanced_entries_of_its_month_in_journal_order()
    {
        var bank = Entry(Journal.Bank, new DateOnly(2027, 3, 20), "REG-1", 50m);
        var sales = Entry(Journal.Sales, new DateOnly(2027, 3, 25), "FAC-2", 119m);
        var salesEarlier = Entry(Journal.Sales, new DateOnly(2027, 3, 2), "FAC-1", 10m);

        var closing = PeriodClosing.Freeze(AgencyId, March, [bank, sales, salesEarlier], "FileToInvoice LOC-1", Accountant, At).Value;

        closing.EntryCount.ShouldBe(3);
        closing.TotalDebit.ShouldBe(Money.Tnd(179m));
        closing.TotalCredit.ShouldBe(Money.Tnd(179m));
        closing.ConfirmedChecks.ShouldBe("FileToInvoice LOC-1");
        closing.Entries.Select(e => (e.Sequence, e.Piece)).ShouldBe([(1, "FAC-1"), (2, "FAC-2"), (3, "REG-1")]);
        closing.Entries.ShouldAllBe(e => e.ClosingId == closing.Id);
        closing.Entries.Select(e => e.Id).ShouldNotContain(bank.Id, "the frozen entries are copies");
        closing.Entries.SelectMany(e => e.Lines).Count().ShouldBe(6);
        closing.Entries.ShouldAllBe(e => e.AgencyId == AgencyId && e.Lines.All(l => l.AgencyId == AgencyId), "the frozen journals are the agency's");
        bank.AgencyId.ShouldBe(Guid.Empty, "an entry generated from the live facts is never stored");
    }

    [Fact]
    public void A_closing_refuses_an_entry_dated_in_another_month()
    {
        var april = Entry(Journal.Bank, new DateOnly(2027, 4, 1), "REG-1", 50m);

        PeriodClosing.Freeze(AgencyId, March, [april], null, Accountant, At).Error.ShouldBe(AccountingErrors.EntryOutsideMonth);
        PeriodClosing.Freeze(Guid.Empty, March, [], null, Accountant, At).Error.ShouldBe(AccountingErrors.AgencyNotFound);
        PeriodClosing.Freeze(AgencyId, March, [], null, Accountant, At).Value.EntryCount.ShouldBe(0, "a month without facts closes too");
    }

    private static JournalEntry Entry(Journal journal, DateOnly date, string piece, decimal amount) =>
        new JournalEntryBuilder(journal, date, piece, piece, $"test:{piece}")
            .Debit("411", Money.Tnd(amount))
            .Credit("7061", Money.Tnd(amount))
            .Build()
            .Value!;
}
