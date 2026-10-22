using System.Text;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Accounting.Fakes.AccountingFacts;

namespace CarRental.Application.Tests.Accounting;

/// <summary>
/// The journals of a month (ADR-0065): generated from the live facts while the month is open, frozen by its closing, a
/// fact changed after the closing posted once as its correction in the open month; the generic exports.
/// </summary>
public sealed class JournalServiceTests
{
    private static readonly AccountingMonth March = AccountingHarness.March;
    private static readonly AccountingMonth April = March.Next();

    private readonly AccountingHarness _harness = new();
    private readonly Guid _bookingId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid AgencyId => _harness.AgencyId;

    [Fact]
    public async Task The_journals_of_an_open_month_are_generated_from_the_live_facts_with_their_totals()
    {
        SeedMarch();

        var month = (await _harness.Journals(Accountant()).GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Value;

        month.IsClosed.ShouldBeFalse();
        month.IsComplete.ShouldBeTrue();
        month.IsBalanced.ShouldBeTrue();
        month.Entries.Select(e => e.JournalCode).ShouldBe(["VT", "AC", "BQ", "BQ"]);
        month.Totals.Single(t => t.Journal == Journal.Sales).Debit.ShouldBe(Tnd(130m));
        month.Totals.Single(t => t.Journal == Journal.Bank).EntryCount.ShouldBe(2);
        month.TotalDebit.ShouldBe(Tnd(488m));
        month.TotalCredit.ShouldBe(Tnd(488m));
        var customers = month.Balances.Single(b => b.AccountNumber == "411");
        customers.Balance.ShouldBe(Money.Zero, "the invoice due is settled by the card payment");
        customers.AccountLabel.ShouldNotBeNullOrWhiteSpace();
        month.Entries.SelectMany(e => e.Lines).Where(l => l.AccountNumber == "411").ShouldAllBe(l => l.LetteringKey == "LOC-2027-000001");
    }

    [Fact]
    public async Task One_journal_is_shown_alone_while_the_totals_cover_every_journal()
    {
        SeedMarch();

        var month = (await _harness.Journals(Accountant()).GetMonthAsync(AgencyId, March, Journal.Bank, Ct)).Value;

        month.Entries.Count.ShouldBe(2);
        month.Entries.ShouldAllBe(e => e.Journal == Journal.Bank);
        month.Totals.Sum(t => t.EntryCount).ShouldBe(4);
    }

    [Fact]
    public async Task Only_the_accounting_permission_reads_and_exports_the_journals_of_a_known_agency()
    {
        var agent = AccountingHarness.User(Roles.Agent);

        (await _harness.Journals(agent).GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await _harness.Journals(agent).ExportAsync(AgencyId, March, March, null, "csv", Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await _harness.Journals(Accountant()).GetMonthAsync(Guid.CreateVersion7(), March, cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.AgencyNotFound);
        (await _harness.Journals(Accountant()).ExportAsync(Guid.CreateVersion7(), March, March, null, "csv", Ct)).Error.ShouldBe(AccountingErrors.AgencyNotFound);
    }

    [Fact]
    public async Task The_generic_csv_export_lists_every_line_in_journal_order_with_the_control_totals()
    {
        SeedMarch();
        var journals = _harness.Journals(Accountant());

        var file = (await journals.ExportAsync(AgencyId, March, March, null, "CSV", Ct)).Value;

        file.FileName.ShouldBe("journaux-tous-2027-03.csv");
        file.ContentType.ShouldBe("text/csv; charset=utf-8");
        file.Content[..3].ShouldBe(Encoding.UTF8.GetPreamble());
        var rows = Encoding.UTF8.GetString(file.Content[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[0].ShouldBe("date;journal;compte;piece;libelle;debit;credit;lettrage");
        rows[1].ShouldBe("2027-03-15;VT;411;FAC-2027-000001;Invoice:FAC-2027-000001|Sami Ben Salah;120.000;0.000;LOC-2027-000001");
        var month = (await journals.GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Value;
        rows.Length.ShouldBe(month.Entries.Sum(e => e.Lines.Count) + 2);
        rows[1..^1].Select(r => r.Split(';')[1]).ShouldBe(month.Entries.SelectMany(e => e.Lines.Select(_ => e.JournalCode)));
        rows[^1].ShouldBe("TOTAL;;;;;488.000;488.000;");
    }

    [Fact]
    public async Task An_export_of_one_journal_names_it_and_totals_it_alone()
    {
        SeedMarch();

        var file = (await _harness.Journals(Accountant()).ExportAsync(AgencyId, March, March, Journal.Purchases, "csv", Ct)).Value;

        file.FileName.ShouldBe("journaux-AC-2027-03.csv");
        var rows = Encoding.UTF8.GetString(file.Content[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[1..^1].ShouldAllBe(r => r.Split(';', StringSplitOptions.None)[1] == "AC");
        rows[^1].ShouldBe("TOTAL;;;;;119.000;119.000;");
    }

    [Fact]
    public async Task An_export_refuses_an_unknown_format_reversed_months_and_more_than_twelve_months()
    {
        var journals = _harness.Journals(Accountant());

        (await journals.ExportAsync(AgencyId, March, March, null, "pdf", Ct)).Error.ShouldBe(AccountingErrors.ExportFormatUnknown);
        (await journals.ExportAsync(AgencyId, March, March.Previous(), null, "csv", Ct)).Error.ShouldBe(AccountingErrors.MonthInvalid);
        (await journals.ExportAsync(AgencyId, AccountingMonth.Create(2026, 3).Value, March, null, "csv", Ct)).Error.ShouldBe(AccountingErrors.MonthInvalid);
        var year = (await journals.ExportAsync(AgencyId, AccountingMonth.Create(2026, 4).Value, March, null, "csv", Ct)).Value;
        year.FileName.ShouldBe("journaux-tous-2026-04_2027-03.csv");
        journals.Formats.ShouldBe(["csv"]);
    }

    [Fact]
    public async Task An_operation_type_without_account_leaves_the_month_incomplete_and_refuses_its_export()
    {
        SeedMarch();
        var stamp = _harness.Db.Rows<AccountMapping>().Single(m => m.Key == AccountingMappingCatalog.StampDuty);
        _harness.Db.Rows<AccountMapping>().Remove(stamp);
        var journals = _harness.Journals(Accountant());

        var month = (await journals.GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Value;

        month.IsComplete.ShouldBeFalse();
        month.MissingKeys.ShouldBe([AccountingMappingCatalog.StampDuty]);
        (await journals.ExportAsync(AgencyId, March, March, null, "csv", Ct)).Error.ShouldBe(AccountingErrors.MappingMissing);
    }

    [Fact]
    public async Task A_closed_month_shows_and_exports_its_frozen_lines_whatever_its_facts_become()
    {
        SeedMarch();
        var accountant = Accountant();
        var before = (await _harness.Journals(accountant).ExportAsync(AgencyId, March, March, null, "csv", Ct)).Value.Content;
        (await _harness.Closing(accountant).CloseAsync(AgencyId, March, [], Ct)).IsSuccess.ShouldBeTrue();
        _harness.StoreFrozenEntries();

        // A fact that should never have been dated in the closed month: the frozen journals ignore it.
        _harness.Sales.Documents.Add(Invoice(AgencyId, "FAC-2027-000002"));
        var month = (await _harness.Journals(accountant).GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Value;

        month.IsClosed.ShouldBeTrue();
        month.ClosedAtUtc.ShouldBe(AccountingHarness.Now);
        month.ClosedByName.ShouldBe(accountant.UserName);
        month.Entries.Count.ShouldBe(4);
        month.Entries.ShouldNotContain(e => e.Piece == "FAC-2027-000002");
        var after = (await _harness.Journals(accountant).ExportAsync(AgencyId, March, March, null, "csv", Ct)).Value.Content;
        after.ShouldBe(before, "a closed month always exports the same lines");
    }

    [Fact]
    public async Task A_payout_corrected_after_the_closing_of_its_month_is_posted_once_as_its_correction_in_the_open_month()
    {
        var accountant = Accountant();
        var (payoutId, channelId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        _harness.Channels.Settlements.Add(Settlement(815m, TunisTime.FromLocal(new DateOnly(2027, 3, 31), new TimeOnly(15, 0)), payoutId, channelId));
        (await _harness.Reconciliation(accountant).JustifyAsync(AgencyId, March, $"channel:payout:{channelId:N}", "Frais bancaires Booking.com", Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Closing(accountant).CloseAsync(AgencyId, March, [], Ct)).IsSuccess.ShouldBeTrue();
        _harness.StoreFrozenEntries();

        // Corrected on 5 April: the payout of March is 820.000, as expected.
        _harness.Channels.Settlements.Clear();
        _harness.Channels.Settlements.Add(Settlement(820m, TunisTime.FromLocal(new DateOnly(2027, 4, 5), new TimeOnly(10, 0)), payoutId, channelId));
        var april = (await _harness.Journals(accountant).GetMonthAsync(AgencyId, April, cancellationToken: Ct)).Value;
        var march = (await _harness.Journals(accountant).GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Value;

        var correction = april.Entries.ShouldHaveSingleItem();
        correction.SourceKey.ShouldBe($"payout:{payoutId:N}");
        correction.Date.ShouldBe(new DateOnly(2027, 4, 5));
        correction.Label.ShouldStartWith("Correction:");
        correction.Label.ShouldEndWith("|2027-03");
        correction.Lines.Select(l => (l.AccountNumber, l.Debit.Amount, l.Credit.Amount)).ShouldBe([("532", 5m, 0m), ("471", 0m, 5m)], ignoreOrder: true);
        march.Entries.ShouldHaveSingleItem().Lines.Single(l => l.AccountNumber == "532").Debit.ShouldBe(Tnd(815m), "March stays as frozen");
        march.Balances.Single(b => b.AccountNumber == "4118").Balance.ShouldBe(Tnd(-1000m));
        april.Balances.ShouldNotContain(b => b.AccountNumber == "4118", "the receivable is settled once");
    }

    [Fact]
    public async Task A_fact_entered_again_unchanged_after_the_closing_posts_nothing()
    {
        var accountant = Accountant();
        var (payoutId, channelId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        _harness.Channels.Settlements.Add(Settlement(820m, TunisTime.FromLocal(new DateOnly(2027, 3, 31), new TimeOnly(15, 0)), payoutId, channelId));
        (await _harness.Closing(accountant).CloseAsync(AgencyId, March, [], Ct)).IsSuccess.ShouldBeTrue();
        _harness.StoreFrozenEntries();

        _harness.Channels.Settlements.Clear();
        _harness.Channels.Settlements.Add(Settlement(820m, TunisTime.FromLocal(new DateOnly(2027, 4, 1), new TimeOnly(9, 0)), payoutId, channelId));
        var april = (await _harness.Journals(accountant).GetMonthAsync(AgencyId, April, cancellationToken: Ct)).Value;

        april.Entries.ShouldBeEmpty();
    }

    private static ScopedUserContext Accountant() => AccountingHarness.User(Roles.Accountant);

    private void SeedMarch()
    {
        _harness.Sales.Documents.Add(Invoice(AgencyId));
        _harness.Cash.Expenses.Add(Expense());
        _harness.Payments.Settled.Add(Payment(_bookingId, PaymentMethodInfo.CardTerminal, 120m));
        _harness.Bookings.Numbers[_bookingId] = "LOC-2027-000001";
    }
}
