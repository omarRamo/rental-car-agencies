using System.Text;
using CarRental.Application.Accounting.Reconciliation;
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
/// The reconciliations of a month (ADR-0064): cash sessions against the cash payments, online payments against the
/// gateway flags and the merchant statement (matched, missing, extra, different), channel payouts against what the
/// channel should pay; differences justified with a reason; statements refused in a closed month.
/// </summary>
public sealed class ReconciliationServiceTests
{
    private static readonly AccountingMonth March = AccountingHarness.March;

    private readonly AccountingHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid AgencyId => _harness.AgencyId;

    [Fact]
    public async Task A_counted_difference_of_a_closed_session_shows_until_it_is_justified_with_a_reason()
    {
        var sessionId = Guid.CreateVersion7();
        _harness.Cash.Sessions.Add(Session(AgencyId, sessionId, CashSessionStatusInfo.Locked, revenueVariance: -1.25m));
        var accountant = Accountant();
        var reconciliation = _harness.Reconciliation(accountant);

        var cash = (await reconciliation.GetCashAsync(AgencyId, March, Ct)).Value;

        var session = cash.Sessions.ShouldHaveSingleItem();
        session.RevenueVariance.ShouldBe(Tnd(-1.25m));
        session.CashierJustification.ShouldBe("Pièce égarée");
        var difference = cash.Differences.ShouldHaveSingleItem();
        difference.Key.ShouldBe($"cash:variance:{sessionId:N}:revenue");
        difference.Kind.ShouldBe(DifferenceKind.CashVariance);
        difference.Amount.ShouldBe(Tnd(-1.25m));
        difference.IsJustified.ShouldBeFalse();
        (await reconciliation.JustifyAsync(AgencyId, March, difference.Key, "ok", Ct)).Error.ShouldBe(AccountingErrors.JustificationReasonInvalid);
        (await reconciliation.JustifyAsync(AgencyId, March, "cash:variance:unknown", "Pièce de monnaie égarée", Ct)).Error.ShouldBe(AccountingErrors.DifferenceNotFound);

        (await reconciliation.JustifyAsync(AgencyId, March, difference.Key, "Pièce de monnaie égarée", Ct)).IsSuccess.ShouldBeTrue();

        var justified = (await reconciliation.GetCashAsync(AgencyId, March, Ct)).Value.Differences.ShouldHaveSingleItem();
        justified.IsJustified.ShouldBeTrue();
        justified.Current!.Reason.ShouldBe("Pièce de monnaie égarée");
        justified.Current.JustifiedByName.ShouldBe(accountant.UserName);
        justified.Current.JustifiedAtUtc.ShouldBe(AccountingHarness.Now);
    }

    [Fact]
    public async Task A_justification_holds_for_the_amount_it_justified_and_stays_visible_when_the_difference_changes()
    {
        var sessionId = Guid.CreateVersion7();
        _harness.Cash.Sessions.Add(Session(AgencyId, sessionId, CashSessionStatusInfo.Locked, revenueVariance: -1.25m));
        var reconciliation = _harness.Reconciliation(Accountant());
        (await reconciliation.JustifyAsync(AgencyId, March, $"cash:variance:{sessionId:N}:revenue", "Pièce de monnaie égarée", Ct)).IsSuccess.ShouldBeTrue();

        _harness.Cash.Sessions[0] = Session(AgencyId, sessionId, CashSessionStatusInfo.Locked, revenueVariance: -2m);
        var difference = (await reconciliation.GetCashAsync(AgencyId, March, Ct)).Value.Differences.ShouldHaveSingleItem();

        difference.Amount.ShouldBe(Tnd(-2m));
        difference.IsJustified.ShouldBeFalse();
        difference.Justifications.ShouldHaveSingleItem().Amount.ShouldBe(Tnd(-1.25m));
    }

    [Fact]
    public async Task The_cash_payments_recorded_in_a_session_are_checked_against_its_drawer()
    {
        var sessionId = Guid.CreateVersion7();
        var session = Session(AgencyId, sessionId, CashSessionStatusInfo.Locked);
        _harness.Cash.Sessions.Add(session with { Totals = session.Totals with { PaymentsReceived = Tnd(100m) } });
        _harness.Payments.Settled.Add(Payment(Guid.CreateVersion7(), PaymentMethodInfo.Cash, 120m, cashSession: sessionId));

        var cash = (await _harness.Reconciliation(Accountant()).GetCashAsync(AgencyId, March, Ct)).Value;

        cash.Sessions.ShouldHaveSingleItem().ReceivedRecorded.ShouldBe(Tnd(120m));
        var difference = cash.Differences.ShouldHaveSingleItem();
        difference.Kind.ShouldBe(DifferenceKind.CashReceiptsMismatch);
        difference.Amount.ShouldBe(Tnd(20m));
    }

    [Fact]
    public async Task Without_a_statement_only_the_flags_of_the_gateway_are_differences()
    {
        _harness.Payments.Online.Add(Online(300m, new DateOnly(2027, 3, 10)));
        _harness.Payments.Online.Add(Online(80m, new DateOnly(2027, 3, 11), attention: GatewayAttentionInfo.UnknownStatus));

        var online = (await _harness.Reconciliation(Accountant()).GetClicToPayAsync(AgencyId, March, Ct)).Value;

        online.HasStatement.ShouldBeFalse();
        online.Payments.ShouldAllBe(p => p.Match == StatementMatch.NoStatement);
        var flag = online.Differences.ShouldHaveSingleItem();
        flag.Kind.ShouldBe(DifferenceKind.GatewayAttention);
        flag.Amount.ShouldBe(Tnd(80m));
    }

    [Fact]
    public async Task The_merchant_statement_matches_the_payments_and_refunds_and_shows_what_is_missing_extra_or_different()
    {
        var paid = Online(300m, new DateOnly(2027, 3, 10));
        var different = Online(200m, new DateOnly(2027, 3, 12));
        var missing = Online(150m, new DateOnly(2027, 3, 14));
        var refund = Online(50m, new DateOnly(2027, 3, 20), PaymentDirectionInfo.Refund, paid.PaymentId);
        _harness.Payments.Online.AddRange([paid, different, missing, refund]);
        var csv = $"""
            date;reference;type;montant;frais;date_remise
            2027-03-10;{paid.OrderNumber};paiement;300,000;6,000;2027-03-12
            2027-03-12;{different.OrderNumber.ToUpperInvariant()};P;190.000;;
            2027-03-20;{paid.OrderNumber};remboursement;50.000;;
            2027-03-25;INCONNU-42;payment;80.000;;
            """;
        var reconciliation = _harness.Reconciliation(Accountant());

        var imported = (await reconciliation.ImportStatementAsync(AgencyId, GenericCsvStatementImporter.FormatName, "releve-mars.csv", Csv(csv), Ct)).Value;
        var online = (await reconciliation.GetClicToPayAsync(AgencyId, March, Ct)).Value;

        imported.IsImported.ShouldBeTrue();
        imported.LineCount.ShouldBe(4);
        online.HasStatement.ShouldBeTrue();
        online.Payments.Single(p => p.PaymentId == paid.PaymentId).Match.ShouldBe(StatementMatch.Matched);
        online.Payments.Single(p => p.PaymentId == refund.PaymentId).Match.ShouldBe(StatementMatch.Matched);
        online.Payments.Single(p => p.PaymentId == different.PaymentId).StatementAmount.ShouldBe(Tnd(190m));
        (online.MatchedCount, online.MissingCount, online.ExtraCount).ShouldBe((2, 1, 1));
        online.Differences.Select(d => (d.Kind, d.Amount.Amount)).ShouldBe(
            [(DifferenceKind.StatementExtra, 80m), (DifferenceKind.StatementAmountMismatch, -10m), (DifferenceKind.StatementMissing, 150m)],
            ignoreOrder: true);
        var statement = (await reconciliation.ListStatementsAsync(AgencyId, Ct)).Value.ShouldHaveSingleItem();
        statement.FileName.ShouldBe("releve-mars.csv");
        statement.Origin.ShouldBe(StatementOrigin.Import);
        (statement.FirstDate, statement.LastDate).ShouldBe((new DateOnly(2027, 3, 10), new DateOnly(2027, 3, 25)));
        statement.TouchesClosedMonth.ShouldBeFalse();
    }

    [Fact]
    public async Task The_lines_settled_by_the_bank_reach_the_bank_journal_less_their_fees()
    {
        var reconciliation = _harness.Reconciliation(Accountant());
        (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 3, 10), "A1", StatementLineKind.Payment, 300m, 6m, new DateOnly(2027, 3, 12), null), Ct)).IsSuccess.ShouldBeTrue();
        (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 3, 11), "A2", StatementLineKind.Refund, 50m, null, new DateOnly(2027, 3, 12), "Remboursement"), Ct)).IsSuccess.ShouldBeTrue();

        var month = (await _harness.Journals(Accountant()).GetMonthAsync(AgencyId, March, cancellationToken: Ct)).Value;

        var settlement = month.Entries.ShouldHaveSingleItem();
        settlement.SourceKey.ShouldBe("clictopay-settlement:20270312");
        settlement.Piece.ShouldBe("CTP-20270312");
        settlement.Lines.Select(l => (l.AccountNumber, l.Debit.Amount, l.Credit.Amount)).ShouldBe([("532", 244m, 0m), ("6271", 6m, 0m), ("5811", 0m, 250m)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_statement_is_read_whole_first_and_nothing_is_recorded_when_a_line_is_unreadable()
    {
        var reconciliation = _harness.Reconciliation(Accountant());

        var outcome = (await reconciliation.ImportStatementAsync(AgencyId, "generic-csv", "releve.csv", Csv("date;reference;type;amount\n2027-03-10;A1;payment;12.500\n2027-03-11;A2;payment;12.3456\n2027-03-12;;payment;5\n"), Ct)).Value;

        outcome.IsImported.ShouldBeFalse();
        outcome.InvalidLines.ShouldBe([3, 4]);
        outcome.LineCount.ShouldBe(1);
        _harness.Db.Rows<MerchantStatement>().ShouldBeEmpty();
        (await reconciliation.ImportStatementAsync(AgencyId, "generic-csv", null, Csv("colonne;inconnue\n1;2\n"), Ct)).Value.HeaderInvalid.ShouldBeTrue();
        (await reconciliation.ImportStatementAsync(AgencyId, "generic-csv", null, Csv("date;reference;type;amount\n"), Ct)).Error.ShouldBe(AccountingErrors.StatementEmpty);
        (await reconciliation.ImportStatementAsync(AgencyId, "smt-v1", null, Csv("date;reference;type;amount\n"), Ct)).Error.ShouldBe(AccountingErrors.StatementInvalid);
        (await reconciliation.ImportStatementAsync(AgencyId, "generic-csv", null, new MemoryStream(new byte[(2 * 1024 * 1024) + 1]), Ct)).Error.ShouldBe(AccountingErrors.StatementTooLarge);
        (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 3, 10), "A1", StatementLineKind.Payment, 1.2345m, null, null, null), Ct)).Error.ShouldBe(AccountingErrors.StatementLineInvalid);
        (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 3, 10), " ", StatementLineKind.Payment, 10m, null, null, null), Ct)).Error.ShouldBe(AccountingErrors.StatementLineInvalid);
        reconciliation.ImportFormats.ShouldBe([GenericCsvStatementImporter.FormatName]);
    }

    [Fact]
    public async Task No_statement_line_is_added_or_deleted_in_a_closed_month()
    {
        var reconciliation = _harness.Reconciliation(Accountant());
        var statementId = (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 3, 10), "A1", StatementLineKind.Payment, 300m, null, null, null), Ct)).Value;
        _harness.CloseMonth(March);

        (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 3, 11), "A2", StatementLineKind.Payment, 10m, null, null, null), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 2, 27), "A3", StatementLineKind.Payment, 10m, null, new DateOnly(2027, 3, 2), null), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await reconciliation.ImportStatementAsync(AgencyId, "generic-csv", null, Csv("date;reference;type;amount\n2027-03-10;A1;payment;12.500\n"), Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await reconciliation.DeleteStatementAsync(statementId, Ct)).Error.ShouldBe(AccountingErrors.PeriodClosed);
        (await reconciliation.ListStatementsAsync(AgencyId, Ct)).Value.ShouldHaveSingleItem().TouchesClosedMonth.ShouldBeTrue();
        var april = (await reconciliation.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 4, 1), "A4", StatementLineKind.Payment, 10m, null, null, null), Ct)).Value;
        (await reconciliation.DeleteStatementAsync(april, Ct)).IsSuccess.ShouldBeTrue();
        (await reconciliation.DeleteStatementAsync(april, Ct)).Error.ShouldBe(AccountingErrors.StatementNotFound);
        _harness.Db.Rows<MerchantStatement>().ShouldHaveSingleItem().Id.ShouldBe(statementId);
    }

    [Fact]
    public async Task A_payout_that_differs_from_what_the_channel_should_pay_and_a_missing_payout_are_differences()
    {
        var (paidChannel, unpaidChannel) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        _harness.Channels.Settlements.Add(Settlement(815m, TunisTime.FromLocal(new DateOnly(2027, 3, 31), new TimeOnly(15, 0)), channelId: paidChannel));
        _harness.Channels.Settlements.Add(Settlement(null, null, channelId: unpaidChannel));

        var channels = (await _harness.Reconciliation(Accountant()).GetChannelsAsync(AgencyId, March, Ct)).Value;

        channels.Settlements.Count.ShouldBe(2);
        channels.Differences.Select(d => (d.Key, d.Kind, d.Amount.Amount)).ShouldBe(
            [($"channel:payout:{paidChannel:N}", DifferenceKind.PayoutDifference, -5m), ($"channel:missing:{unpaidChannel:N}", DifferenceKind.PayoutMissing, 820m)],
            ignoreOrder: true);
        channels.Differences.ShouldAllBe(d => d.Area == ReconciliationArea.BookingCom);
    }

    [Fact]
    public async Task Only_the_reconciliation_permission_reconciles()
    {
        var agent = _harness.Reconciliation(AccountingHarness.User(Roles.Agent));

        (await agent.GetCashAsync(AgencyId, March, Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.GetClicToPayAsync(AgencyId, March, Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.GetChannelsAsync(AgencyId, March, Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.JustifyAsync(AgencyId, March, "cash:x", "Pièce de monnaie égarée", Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.ListStatementsAsync(AgencyId, Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.ImportStatementAsync(AgencyId, "generic-csv", null, Csv("date;reference;type;amount\n2027-03-10;A1;payment;1\n"), Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.AddManualLineAsync(AgencyId, new StatementLineForm(new DateOnly(2027, 3, 10), "A1", StatementLineKind.Payment, 1m, null, null, null), Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await agent.DeleteStatementAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
    }

    private static ScopedUserContext Accountant() => AccountingHarness.User(Roles.Accountant);

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>An online payment (or refund) captured on <paramref name="settledOn"/> at 14:00 in Tunis; its order number is its id.</summary>
    private static OnlinePaymentStateInfo Online(
        decimal amount,
        DateOnly settledOn,
        PaymentDirectionInfo direction = PaymentDirectionInfo.Incoming,
        Guid? originalPaymentId = null,
        GatewayAttentionInfo? attention = null)
    {
        var id = Guid.CreateVersion7();
        var at = TunisTime.FromLocal(settledOn, new TimeOnly(14, 0));
        return new OnlinePaymentStateInfo(
            id,
            Guid.CreateVersion7(),
            direction,
            PaymentStatusInfo.Captured,
            Tnd(amount),
            Tnd(amount),
            Money.Zero,
            id.ToString("N"),
            $"CTP{id.ToString("N")[..12]}",
            2,
            attention,
            originalPaymentId,
            at.AddMinutes(-5),
            at);
    }
}
