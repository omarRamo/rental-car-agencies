using CarRental.Application.Accounting.Periods;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Accounting.Fakes.AccountingFacts;

namespace CarRental.Application.Tests.Accounting;

/// <summary>
/// The monthly closing (ADR-0065): only once the month is over, after its blocking checks are settled and its confirmable
/// ones confirmed; it freezes the journals, locks the month (the hook then refuses every fact dated in it) under the
/// exclusive lock of the month, is audited; only a manager reopens it, with a reason.
/// </summary>
public sealed class MonthlyClosingServiceTests
{
    private static readonly AccountingMonth March = AccountingHarness.March;

    private readonly AccountingHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid AgencyId => _harness.AgencyId;

    [Fact]
    public async Task A_month_that_is_not_over_is_never_closed()
    {
        var april = March.Next();
        var closing = _harness.Closing(Accountant());

        var preview = (await closing.PreviewAsync(AgencyId, april, Ct)).Value;

        preview.IsOver.ShouldBeFalse();
        preview.CanClose.ShouldBeFalse();
        (await closing.CloseAsync(AgencyId, april, [], Ct)).Error.ShouldBe(AccountingErrors.PeriodNotEnded);
        _harness.Db.Rows<AccountingPeriod>().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unlocked_cash_session_and_an_unjustified_difference_block_the_closing_until_settled()
    {
        var sessionId = Guid.CreateVersion7();
        _harness.Cash.Sessions.Add(Session(AgencyId, sessionId, CashSessionStatusInfo.Closed, revenueVariance: -1.25m));
        var accountant = Accountant();
        var closing = _harness.Closing(accountant);

        var preview = (await closing.PreviewAsync(AgencyId, March, Ct)).Value;

        preview.Checks.Select(c => (c.Kind, c.Severity)).ShouldBe(
            [(ClosingCheckKind.UnlockedCashSession, ClosingCheckSeverity.Blocking), (ClosingCheckKind.UnjustifiedDifference, ClosingCheckSeverity.Blocking)],
            ignoreOrder: true);
        preview.HasBlockingChecks.ShouldBeTrue();
        preview.CanClose.ShouldBeFalse();
        (await closing.CloseAsync(AgencyId, March, [], Ct)).Error.ShouldBe(AccountingErrors.ClosingBlocked);

        _harness.Cash.Sessions[0] = _harness.Cash.Sessions[0] with { Status = CashSessionStatusInfo.Locked };
        (await _harness.Reconciliation(accountant).JustifyAsync(AgencyId, March, $"cash:variance:{sessionId:N}:revenue", "Pièce de monnaie égarée", Ct)).IsSuccess.ShouldBeTrue();
        var settled = (await closing.PreviewAsync(AgencyId, March, Ct)).Value;

        settled.Checks.ShouldBeEmpty();
        settled.CanClose.ShouldBeTrue();
        (await closing.CloseAsync(AgencyId, March, [], Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Files_still_to_invoice_and_e_invoices_not_accepted_are_confirmed_by_the_accountant_when_closing()
    {
        _harness.Billing.FilesToInvoice.Add(new PendingInvoicingInfo(Guid.CreateVersion7(), "LOC-2027-000009", InvoiceKindInfo.Rental, TunisTime.FromLocal(new DateOnly(2027, 3, 30), new TimeOnly(18, 0)), Tnd(300m)));
        var failed = Invoice(AgencyId) with { EInvoice = new EInvoiceInfo(EInvoiceModeInfo.Ttn, EInvoiceStatusInfo.Failed, null, null) };
        _harness.Sales.Documents.Add(failed);
        var closing = _harness.Closing(Accountant());

        var preview = (await closing.PreviewAsync(AgencyId, March, Ct)).Value;

        preview.Checks.Select(c => c.Kind).ShouldBe([ClosingCheckKind.FileToInvoice, ClosingCheckKind.EInvoiceNotTransmitted], ignoreOrder: true);
        preview.Checks.ShouldAllBe(c => c.Severity == ClosingCheckSeverity.Confirmable);
        preview.CanClose.ShouldBeTrue();
        (await closing.CloseAsync(AgencyId, March, [preview.ConfirmableKeys[0]], Ct)).Error.ShouldBe(AccountingErrors.ClosingNotConfirmed);

        (await closing.CloseAsync(AgencyId, March, preview.ConfirmableKeys, Ct)).IsSuccess.ShouldBeTrue();

        var frozen = _harness.Db.Rows<PeriodClosing>().ShouldHaveSingleItem();
        frozen.ConfirmedChecks.ShouldNotBeNull();
        frozen.ConfirmedChecks.ShouldContain("FileToInvoice LOC-2027-000009");
        frozen.ConfirmedChecks.ShouldContain("EInvoiceNotTransmitted FAC-2027-000001");
    }

    [Fact]
    public async Task Closing_freezes_the_balanced_journals_locks_the_month_under_its_exclusive_lock_and_is_audited()
    {
        SeedMarch();

        var closingId = (await _harness.Closing(Accountant()).CloseAsync(AgencyId, March, [], Ct)).Value;

        var closing = _harness.Db.Rows<PeriodClosing>().ShouldHaveSingleItem();
        closing.Id.ShouldBe(closingId);
        closing.EntryCount.ShouldBe(4);
        closing.TotalDebit.ShouldBe(Tnd(488m));
        closing.TotalCredit.ShouldBe(closing.TotalDebit);
        closing.Entries.Select(e => e.Sequence).ShouldBe([1, 2, 3, 4]);
        closing.Entries.ShouldAllBe(e => e.ClosingId == closingId);
        var period = _harness.Db.Rows<AccountingPeriod>().ShouldHaveSingleItem();
        period.IsClosed.ShouldBeTrue();
        period.CurrentClosingId.ShouldBe(closingId);
        _harness.Lock.Acquired.ShouldContain((AccountingLocks.Period(AgencyId, March), true));
        _harness.Audit.Events.ShouldContain(e => e.Action == "accounting.period_closed" && e.EntityId == period.Id.ToString());
        (await _harness.Periods.EnsureOpenAsync(AgencyId, new DateOnly(2027, 3, 31), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await _harness.Periods.EnsureOpenAsync(AgencyId, new DateOnly(2027, 4, 1), Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Periods.EnsureOpenAsync(Guid.CreateVersion7(), new DateOnly(2027, 3, 31), Ct)).IsSuccess.ShouldBeTrue("another agency has its own months");
        (await _harness.Periods.GetClosedMonthsAsync(AgencyId, Ct)).ShouldBe([new DateOnly(2027, 3, 1)]);
        (await _harness.Closing(Accountant()).CloseAsync(AgencyId, March, [], Ct)).Error.ShouldBe(AccountingErrors.PeriodAlreadyClosed);
    }

    [Fact]
    public async Task A_month_whose_journals_miss_an_account_is_not_closed()
    {
        SeedMarch();
        var stamp = _harness.Db.Rows<AccountMapping>().Single(m => m.Key == AccountingMappingCatalog.StampDuty);
        _harness.Db.Rows<AccountMapping>().Remove(stamp);
        var closing = _harness.Closing(Accountant());

        var preview = (await closing.PreviewAsync(AgencyId, March, Ct)).Value;

        preview.MissingKeys.ShouldBe([AccountingMappingCatalog.StampDuty]);
        preview.CanClose.ShouldBeFalse();
        (await closing.CloseAsync(AgencyId, March, [], Ct)).Error.ShouldBe(AccountingErrors.MappingMissing);
        _harness.Db.Rows<PeriodClosing>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_a_manager_reopens_a_closed_month_with_a_reason_and_its_closing_is_kept()
    {
        SeedMarch();
        (await _harness.Closing(Accountant()).CloseAsync(AgencyId, March, [], Ct)).IsSuccess.ShouldBeTrue();
        var manager = AccountingHarness.User(Roles.Manager);

        (await _harness.Closing(Accountant()).ReopenAsync(AgencyId, March, "Facture oubliée à émettre", Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await _harness.Closing(manager).ReopenAsync(AgencyId, March, "court", Ct)).Error.ShouldBe(AccountingErrors.ReopenReasonRequired);
        (await _harness.Closing(manager).ReopenAsync(AgencyId, March, "  Facture oubliée à émettre  ", Ct)).IsSuccess.ShouldBeTrue();

        var period = _harness.Db.Rows<AccountingPeriod>().ShouldHaveSingleItem();
        period.Status.ShouldBe(AccountingPeriodStatus.Open);
        period.CurrentClosingId.ShouldBeNull();
        period.ReopenCount.ShouldBe(1);
        period.ReopenReason.ShouldBe("Facture oubliée à émettre");
        period.ReopenedByName.ShouldBe(manager.UserName);
        _harness.Db.Rows<PeriodClosing>().Count.ShouldBe(1, "the earlier closing stays for the audit");
        _harness.Audit.Events.ShouldContain(e => e.Action == "accounting.period_reopened" && e.Details!.Contains("Facture oubliée"));
        _harness.Lock.Acquired.Where(l => l.Exclusive).Select(l => l.Resource).ShouldBe(Enumerable.Repeat(AccountingLocks.Period(AgencyId, March), 3), "the closing and both reopenings of the manager");
        (await _harness.Periods.EnsureOpenAsync(AgencyId, new DateOnly(2027, 3, 10), Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Closing(manager).ReopenAsync(AgencyId, March, "Facture oubliée à émettre", Ct)).Error.ShouldBe(AccountingErrors.PeriodNotClosed);
        (await _harness.Closing(manager).ReopenAsync(AgencyId, March.Previous(), "Facture oubliée à émettre", Ct)).Error.ShouldBe(AccountingErrors.PeriodNotClosed);
    }

    [Fact]
    public async Task A_month_reopened_then_closed_again_freezes_its_journals_as_they_are_then()
    {
        SeedMarch();
        var accountant = Accountant();
        var first = (await _harness.Closing(accountant).CloseAsync(AgencyId, March, [], Ct)).Value;
        (await _harness.Closing(AccountingHarness.User(Roles.Manager)).ReopenAsync(AgencyId, March, "Facture oubliée à émettre", Ct)).IsSuccess.ShouldBeTrue();
        _harness.Sales.Documents.Add(Invoice(AgencyId, "FAC-2027-000002", "LOC-2027-000002", new DateOnly(2027, 3, 31)));

        var second = (await _harness.Closing(accountant).CloseAsync(AgencyId, March, [], Ct)).Value;
        _harness.StoreFrozenEntries();

        second.ShouldNotBe(first);
        _harness.Db.Rows<AccountingPeriod>().Single().CurrentClosingId.ShouldBe(second);
        var month = (await _harness.Journals(accountant).GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Value;
        month.IsClosed.ShouldBeTrue();
        month.Entries.Count.ShouldBe(5);
        month.Entries.ShouldContain(e => e.Piece == "FAC-2027-000002");
    }

    [Fact]
    public async Task The_list_of_the_months_shows_the_current_one_and_the_closings()
    {
        SeedMarch();
        (await _harness.Closing(Accountant()).CloseAsync(AgencyId, March, [], Ct)).IsSuccess.ShouldBeTrue();

        var periods = (await _harness.Closing(Accountant()).ListPeriodsAsync(AgencyId, 3, Ct)).Value;

        periods.Select(p => p.Month.ToString()).ShouldBe(["2027-04", "2027-03", "2027-02"]);
        periods[0].IsOver.ShouldBeFalse();
        periods[1].Status.ShouldBe(AccountingPeriodStatus.Closed);
        periods[1].EntryCount.ShouldBe(4);
        periods[1].TotalDebit.ShouldBe(Tnd(488m));
        periods[2].Status.ShouldBe(AccountingPeriodStatus.Open);
        periods[2].IsOver.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_the_accounting_permission_previews_lists_and_closes_a_month_of_a_known_agency()
    {
        var agent = _harness.Closing(AccountingHarness.User(Roles.Agent));

        (await agent.PreviewAsync(AgencyId, March, Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.ListPeriodsAsync(AgencyId, cancellationToken: Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.CloseAsync(AgencyId, March, [], Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await _harness.Closing(Accountant()).CloseAsync(Guid.CreateVersion7(), March, [], Ct)).Error.ShouldBe(AccountingErrors.AgencyNotFound);
    }

    private static ScopedUserContext Accountant() => AccountingHarness.User(Roles.Accountant);

    private void SeedMarch()
    {
        var bookingId = Guid.CreateVersion7();
        _harness.Sales.Documents.Add(Invoice(AgencyId));
        _harness.Cash.Expenses.Add(Expense());
        _harness.Payments.Settled.Add(Payment(bookingId, PaymentMethodInfo.CardTerminal, 120m));
        _harness.Bookings.Numbers[bookingId] = "LOC-2027-000001";
    }
}
