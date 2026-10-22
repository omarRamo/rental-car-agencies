using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Tests.Payments;

/// <summary>
/// The Clic to Pay flows on the simulated gateway (ADR-0039): pre-authorization at the submission, return read server to
/// server (the query proves nothing), decline, capture at the validation (at most the pre-authorized amount, never sent
/// twice), reversal at the refusal, the tab closed before the return, expiry of unpaid orders and payment links, and the
/// recovery of calls whose answer was lost.
/// </summary>
public sealed class OnlinePaymentFlowTests
{
    private readonly OnlinePaymentsHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pre_authorization_is_registered_with_the_payment_id_as_order_number()
    {
        var bookingId = Guid.CreateVersion7();

        var started = await _harness.Online().StartPreAuthorizationAsync(_harness.Request(681.4m, bookingId, "LOC-2027-000123"), Ct);

        var redirect = started.Value;
        var payment = _harness.Payment(redirect.PaymentId);
        redirect.OrderNumber.ShouldBe(payment.Id.ToString("N"));
        redirect.RedirectUrl.ShouldBe(FakeClicToPay.PagePathPrefix + payment.ExternalReference);
        redirect.ExpiresAtUtc.ShouldBe(FinanceHarness.Start.AddHours(1), "an attempt is valid 1 h by default");
        redirect.Reused.ShouldBeFalse();
        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.Method.ShouldBe(PaymentMethod.ClicToPay);
        payment.OnlineFlow.ShouldBe(OnlinePaymentFlow.PreAuthorization);
        payment.BookingId.ShouldBe(bookingId);
        payment.Amount.ShouldBe(Money.Tnd(681.4m));
        payment.GatewayFormUrl.ShouldBe(redirect.RedirectUrl);
        var order = _harness.Gateway.FindOrderByNumber(redirect.OrderNumber).ShouldNotBeNull();
        order.IsPreAuthorization.ShouldBeTrue();
        order.Amount.ShouldBe(Money.Tnd(681.4m));
        order.Language.ShouldBe("fr");
        order.Description.ShouldBe("LOC-2027-000123");
        order.ReturnUrl.ShouldBe($"/payments/clictopay/return?order={redirect.OrderNumber}");
        var call = _harness.CallLog.Entries.ShouldHaveSingleItem();
        call.Operation.ShouldBe(GatewayOperation.RegisterPreAuth);
        call.Outcome.ShouldBe(GatewayCallOutcome.Succeeded);
        call.OrderNumber.ShouldBe(redirect.OrderNumber);
    }

    [Fact]
    public async Task The_return_reads_the_status_server_to_server_and_redirects_to_the_recorded_path()
    {
        var online = _harness.Online();
        var started = (await online.StartPreAuthorizationAsync(_harness.Request(300m), Ct)).Value;
        var payment = _harness.Payment(started.PaymentId);

        // The browser comes back before paying (or with a forged return address): the gateway says unpaid.
        var early = (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, payment.ExternalReference, false), Ct)).Value;
        early.Status.ShouldBe(PaymentStatusInfo.Pending);
        early.RedirectPath.ShouldBe(OnlinePaymentsHarness.ReturnPath);
        (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, true), Ct)).Value.RedirectPath.ShouldBe(OnlinePaymentsHarness.FailPath);

        _harness.Gateway.Pay(payment.ExternalReference!).IsSuccess.ShouldBeTrue();
        var paid = (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, payment.ExternalReference, false), Ct)).Value;

        paid.Status.ShouldBe(PaymentStatusInfo.PreAuthorized);
        paid.StatusConfirmed.ShouldBeTrue();
        paid.RedirectPath.ShouldBe(OnlinePaymentsHarness.ReturnPath);
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.StatusReason.ShouldBe("status 1, action 0 (Request processed successfully)");
        payment.DomainEvents.OfType<PaymentStatusChanged>().ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.PreAuthorized);
        _harness.CallLog.Entries.Count(e => e.Operation == GatewayOperation.GetOrderStatusExtended).ShouldBe(3);

        // A settled payment is not read again on a reload of the return address.
        (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, false), Ct)).Value.Status.ShouldBe(PaymentStatusInfo.PreAuthorized);
        _harness.CallLog.Entries.Count(e => e.Operation == GatewayOperation.GetOrderStatusExtended).ShouldBe(3);
    }

    [Fact]
    public async Task The_catch_up_reads_the_orders_of_every_agency_each_agency_in_a_scope_of_its_own()
    {
        var other = Guid.CreateVersion7();
        _harness.Referential.Agencies.Add(new AgencyInfo(other, "Agence Démo Sousse", "Sousse", null, null, IsActive: true));
        var online = _harness.Online();
        var otherBooking = Guid.CreateVersion7();
        _harness.Bookings.PlaceIn(otherBooking, other);
        var first = (await online.StartPreAuthorizationAsync(_harness.Request(100m), Ct)).Value;
        var second = (await online.StartPreAuthorizationAsync(_harness.Request(200m, otherBooking) with { AgencyId = other }, Ct)).Value;
        _harness.Gateway.Pay(_harness.Payment(second.PaymentId).ExternalReference!);
        _harness.Time.Advance(TimeSpan.FromMinutes(5));

        await _harness.CatchUpJob().ExecuteAsync(Ct);

        _harness.JobAgencies.ShouldBe([_harness.AgencyId, other], ignoreOrder: true);
        _harness.Payment(first.PaymentId).Status.ShouldBe(PaymentStatus.Pending);
        _harness.Payment(second.PaymentId).Status.ShouldBe(PaymentStatus.PreAuthorized, "confirmed for its own agency");
    }

    [Fact]
    public async Task Each_confirmed_change_of_status_is_told_to_the_other_modules_once()
    {
        var online = _harness.Online();
        var started = (await online.StartPreAuthorizationAsync(_harness.Request(300m), Ct)).Value;
        var payment = _harness.Payment(started.PaymentId);
        await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, false), Ct);
        _harness.StatusChangesOf(payment.Id).ShouldBeEmpty("still unpaid: nothing changed");

        _harness.Gateway.Pay(payment.ExternalReference!);
        await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, false), Ct);
        await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, payment.ExternalReference, false), Ct);
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).CaptureAsync(payment.Id, 250m, Ct)).IsSuccess.ShouldBeTrue();

        var changes = _harness.StatusChangesOf(payment.Id);
        changes.Count.ShouldBe(2);
        changes[0].ShouldBe(new OnlinePaymentStatusChanged(
            payment.Id,
            payment.BookingId,
            _harness.AgencyId,
            OnlinePaymentFlowInfo.PreAuthorization,
            IsPaymentLink: false,
            PaymentStatusInfo.Pending,
            PaymentStatusInfo.PreAuthorized,
            Expired: false,
            PaidLate: false));
        changes[1].PreviousStatus.ShouldBe(PaymentStatusInfo.PreAuthorized);
        changes[1].Status.ShouldBe(PaymentStatusInfo.Captured);
    }

    [Fact]
    public async Task The_return_identifies_the_order_by_our_number_first_and_ignores_another_gateway_order_id()
    {
        var online = _harness.Online();
        var mine = _harness.Payment((await online.StartPreAuthorizationAsync(_harness.Request(100m), Ct)).Value.PaymentId);
        var other = _harness.Payment((await online.StartPreAuthorizationAsync(_harness.Request(999m), Ct)).Value.PaymentId);
        _harness.Gateway.Pay(other.ExternalReference!);

        var returned = (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(mine.OrderNumber, other.ExternalReference, false), Ct)).Value;

        returned.PaymentId.ShouldBe(mine.Id);
        returned.Status.ShouldBe(PaymentStatusInfo.Pending, "the status of the recorded order was read, not the one of the query");
        other.Status.ShouldBe(PaymentStatus.Pending);
        (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(null, other.ExternalReference, false), Ct)).Value.PaymentId.ShouldBe(other.Id);
        (await online.HandleReturnAsync(new OnlinePaymentReturnRequest("not-an-order", "unknown", false), Ct)).Error.ShouldBe(PaymentsErrors.OnlinePaymentNotFound);
    }

    [Fact]
    public async Task A_declined_card_fails_the_payment_and_sends_the_browser_to_the_failure_path()
    {
        var online = _harness.Online();
        var started = (await online.StartPreAuthorizationAsync(_harness.Request(250m), Ct)).Value;
        var payment = _harness.Payment(started.PaymentId);
        var next = _harness.Gateway.Decline(payment.ExternalReference!).Value;

        var returned = (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, payment.ExternalReference, true), Ct)).Value;

        next.ShouldStartWith("/payments/clictopay/fail?order=");
        returned.Status.ShouldBe(PaymentStatusInfo.Failed);
        returned.RedirectPath.ShouldBe(OnlinePaymentsHarness.FailPath);
        payment.Status.ShouldBe(PaymentStatus.Failed);
        payment.StatusReason.ShouldBe("status 6, action 116 (Declined (simulated))");
    }

    [Fact]
    public async Task Without_the_status_codes_of_SMT_a_declined_order_is_never_a_success_it_stays_pending_and_flagged()
    {
        var harness = new OnlinePaymentsHarness(configuredStatusCodes: false);
        var online = harness.Online();
        var started = (await online.StartPreAuthorizationAsync(harness.Request(250m), Ct)).Value;
        var payment = harness.Payment(started.PaymentId);
        harness.Gateway.Decline(payment.ExternalReference!);

        var returned = (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, true), Ct)).Value;

        returned.Status.ShouldBe(PaymentStatusInfo.Pending);
        returned.RedirectPath.ShouldBe(OnlinePaymentsHarness.FailPath);
        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.GatewayStatusCode.ShouldBe(FakeClicToPay.DeclinedStatusCode);
        payment.GatewayAttention.ShouldBe(GatewayAttention.UnknownStatus);
    }

    [Fact]
    public async Task The_validation_captures_at_most_the_pre_authorized_amount_and_the_payment_is_captured_once_confirmed()
    {
        var payment = await _harness.PreAuthorizedAsync(681.4m, Ct);
        var online = _harness.Online(OnlinePaymentsHarness.User(Roles.Agent));

        (await online.CaptureAsync(payment.Id, 681.401m, Ct)).Error.ShouldBe(PaymentsErrors.OnlineCaptureExceedsAuthorization);
        (await online.CaptureAsync(payment.Id, 0m, Ct)).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        var captured = await online.CaptureAsync(payment.Id, 650m, Ct);

        captured.Value.Status.ShouldBe(PaymentStatusInfo.Captured);
        captured.Value.CapturedAmount.ShouldBe(Money.Tnd(650m));
        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.CapturedAmount.ShouldBe(Money.Tnd(650m));
        payment.PendingGatewayOperation.ShouldBeNull();
        _harness.Gateway.FindOrderByNumber(payment.OrderNumber)!.DepositedAmount.ShouldBe(Money.Tnd(650m));
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(1);
    }

    [Fact]
    public async Task Capturing_again_returns_the_captured_payment_without_a_second_deposit()
    {
        var payment = await _harness.PreAuthorizedAsync(400m, Ct);
        var online = _harness.Online(OnlinePaymentsHarness.User(Roles.Agent));
        (await online.CaptureBookingAsync(payment.BookingId, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();

        var again = await online.CaptureBookingAsync(payment.BookingId, cancellationToken: Ct);

        again.Value.Status.ShouldBe(PaymentStatusInfo.Captured);
        again.Value.CapturedAmount.ShouldBe(Money.Tnd(400m));
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(1);
    }

    [Fact]
    public async Task A_deposit_whose_answer_was_lost_is_confirmed_by_the_status_read_and_never_sent_twice()
    {
        var payment = await _harness.PreAuthorizedAsync(500m, Ct);
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Deposit, FakeFaultKind.LostResponse, c => c.OrderNumber == payment.OrderNumber));
        var online = _harness.Online(OnlinePaymentsHarness.User(Roles.Agent));

        var captured = await online.CaptureAsync(payment.Id, cancellationToken: Ct);
        var again = await online.CaptureAsync(payment.Id, cancellationToken: Ct);

        captured.Value.Status.ShouldBe(PaymentStatusInfo.Captured);
        again.Value.Status.ShouldBe(PaymentStatusInfo.Captured);
        payment.CapturedAmount.ShouldBe(Money.Tnd(500m));
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(1);
        _harness.CallLog.Entries.ShouldContain(e => e.Operation == GatewayOperation.Deposit && e.Outcome == GatewayCallOutcome.Unavailable);
    }

    [Fact]
    public async Task A_deposit_left_without_answer_is_sent_again_only_after_the_status_shows_it_had_no_effect()
    {
        var payment = await _harness.PreAuthorizedAsync(500m, Ct);
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Deposit, FakeFaultKind.Unavailable, c => c.OrderNumber == payment.OrderNumber));
        var online = _harness.Online(OnlinePaymentsHarness.User(Roles.Agent));

        (await online.CaptureAsync(payment.Id, 450m, Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.PendingGatewayOperation.ShouldBe(PendingGatewayOperation.Capture);
        (await online.CaptureAsync(payment.Id, 450m, Ct)).Error.ShouldBe(PaymentsErrors.OnlineOperationInProgress);

        _harness.Time.Advance(TimeSpan.FromMinutes(3));
        var captured = await online.CaptureAsync(payment.Id, 450m, Ct);

        captured.Value.CapturedAmount.ShouldBe(Money.Tnd(450m));
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(2, "the first request never reached the gateway");
    }

    [Fact]
    public async Task A_capture_left_without_answer_is_not_sent_again_while_the_status_cannot_be_read()
    {
        var payment = await _harness.PreAuthorizedAsync(500m, Ct);
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Deposit, FakeFaultKind.Unavailable, c => c.OrderNumber == payment.OrderNumber));
        var online = _harness.Online(OnlinePaymentsHarness.User(Roles.Agent));
        (await online.CaptureAsync(payment.Id, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        _harness.Time.Advance(TimeSpan.FromMinutes(3));
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.GetOrderStatusExtended, FakeFaultKind.Unavailable, c => c.OrderNumber == payment.OrderNumber));

        var blind = await online.CaptureAsync(payment.Id, cancellationToken: Ct);

        blind.Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(1, "nothing is sent on the faith of a state that may be out of date");
        payment.PendingGatewayOperation.ShouldBe(PendingGatewayOperation.Capture);
        (await online.CaptureAsync(payment.Id, cancellationToken: Ct)).Value.Status.ShouldBe(PaymentStatusInfo.Captured);
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(2);
    }

    [Fact]
    public async Task The_job_flags_a_capture_left_without_answer_and_keeps_it_for_the_next_validation()
    {
        var payment = await _harness.PreAuthorizedAsync(500m, Ct);
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Deposit, FakeFaultKind.Unavailable, c => c.OrderNumber == payment.OrderNumber));
        var online = _harness.Online(OnlinePaymentsHarness.User(Roles.Agent));
        (await online.CaptureAsync(payment.Id, 450m, Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);

        _harness.Time.Advance(TimeSpan.FromMinutes(3));
        await _harness.CatchUpJob().ExecuteAsync(Ct);

        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.PendingGatewayOperation.ShouldBe(PendingGatewayOperation.Capture, "kept with its amount: a late deposit is recorded with it");
        payment.PendingGatewayAmount.ShouldBe(Money.Tnd(450m));
        payment.GatewayAttention.ShouldBe(GatewayAttention.CaptureNotConfirmed);
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(1, "the job never sends a capture");
        var flagged = (await online.FindAsync(payment.Id, Ct)).ShouldNotBeNull();
        flagged.CanCapture.ShouldBeTrue("the back office can send it again");
        flagged.CanReverse.ShouldBeFalse();

        var captured = await online.CaptureAsync(payment.Id, 450m, Ct);

        captured.Value.CapturedAmount.ShouldBe(Money.Tnd(450m));
        payment.GatewayAttention.ShouldBeNull();
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(2);
    }

    [Fact]
    public async Task A_pre_authorization_debited_outside_the_platform_without_its_amount_is_recorded_and_flagged()
    {
        var payment = await _harness.PreAuthorizedAsync(300m, Ct);
        _harness.Gateway.ReportsAmountDetails = false;
        _harness.Gateway.OverrideStatus(payment.OrderNumber, 2).IsSuccess.ShouldBeTrue();

        var refreshed = await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).RefreshStatusAsync(payment.Id, Ct);

        refreshed.Value.Status.ShouldBe(PaymentStatusInfo.Captured);
        payment.CapturedAmount.ShouldBe(Money.Tnd(300m));
        payment.GatewayAttention.ShouldBe(GatewayAttention.AmountMismatch);
        payment.GatewayAttentionDetail.ShouldNotBeNull().ShouldContain("order amount assumed");
    }

    [Fact]
    public async Task A_capture_the_gateway_refuses_leaves_the_pre_authorization_flagged()
    {
        var payment = await _harness.PreAuthorizedAsync(200m, Ct);
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.Deposit, FakeFaultKind.Rejected, c => c.OrderNumber == payment.OrderNumber, "7"));

        var result = await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).CaptureAsync(payment.Id, cancellationToken: Ct);

        result.Error.ShouldBe(PaymentsErrors.GatewayRejected);
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.PendingGatewayOperation.ShouldBeNull();
        payment.GatewayAttention.ShouldBe(GatewayAttention.OperationRejected);
        _harness.CallLog.Entries.ShouldContain(e => e.Operation == GatewayOperation.Deposit && e.GatewayErrorCode == "7");
    }

    [Fact]
    public async Task A_status_read_lagging_behind_the_deposit_leaves_the_capture_unconfirmed_until_the_job_confirms_it()
    {
        var payment = await _harness.PreAuthorizedAsync(300m, Ct);
        _harness.Gateway.HoldStatus(payment.OrderNumber, reads: 1).IsSuccess.ShouldBeTrue();

        var result = await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).CaptureAsync(payment.Id, cancellationToken: Ct);

        result.Error.ShouldBe(PaymentsErrors.OnlineCaptureUnconfirmed);
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.PendingGatewayAcknowledged.ShouldBeTrue();
        (await _harness.Online().FindAsync(payment.Id, Ct)).ShouldNotBeNull().CanCapture.ShouldBeFalse("an acknowledged deposit is never sent again");
        _harness.Time.Advance(TimeSpan.FromMinutes(3));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.CapturedAmount.ShouldBe(Money.Tnd(300m));
        _harness.CallsOf(GatewayOperation.Deposit, payment).ShouldBe(1);
    }

    [Fact]
    public async Task Only_a_confirmed_pre_authorization_is_captured()
    {
        var online = _harness.Online(OnlinePaymentsHarness.User(Roles.Agent));
        var unpaid = (await online.StartPreAuthorizationAsync(_harness.Request(100m), Ct)).Value;
        var oneStep = await _harness.PaidAsync(100m, Ct);

        (await online.CaptureAsync(unpaid.PaymentId, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.OnlineNotPreAuthorized);
        (await online.CaptureAsync(oneStep.Id, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.OnlineCaptureNotApplicable);
        (await online.CaptureAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.OnlinePaymentNotFound);
        (await online.CaptureBookingAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.OnlineNotPreAuthorized);
    }

    [Fact]
    public async Task The_refusal_reverses_the_pre_authorization()
    {
        var payment = await _harness.PreAuthorizedAsync(681.4m, Ct);
        var online = _harness.Online(OnlinePaymentsHarness.System());

        var reversed = await online.ReverseBookingAsync(payment.BookingId, Ct);

        reversed.Value.ShouldHaveSingleItem().Status.ShouldBe(PaymentStatusInfo.Cancelled);
        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.ExpiredAtUtc.ShouldBeNull();
        _harness.Gateway.FindOrderByNumber(payment.OrderNumber)!.StatusCode.ShouldBe(FakeClicToPay.ReversedStatusCode);
        (await online.ReverseAsync(payment.Id, Ct)).Value.Status.ShouldBe(PaymentStatusInfo.Cancelled, "reversing again changes nothing");
        _harness.CallsOf(GatewayOperation.Reverse, payment).ShouldBe(1);
        (await online.CaptureAsync(payment.Id, cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.OnlineNotPreAuthorized);
    }

    [Fact]
    public async Task A_reversal_acknowledged_by_the_gateway_is_inferred_even_when_its_status_code_is_not_configured()
    {
        var harness = new OnlinePaymentsHarness(configuredStatusCodes: false);
        var payment = await harness.PreAuthorizedAsync(150m, Ct);

        var reversed = await harness.Online(OnlinePaymentsHarness.System()).ReverseAsync(payment.Id, Ct);

        reversed.Value.Status.ShouldBe(PaymentStatusInfo.Cancelled);
        payment.StatusReason.ShouldBe("status 3, action 0 (Request processed successfully); reverse.do acknowledged");
    }

    [Fact]
    public async Task A_captured_payment_is_not_reversed()
    {
        var payment = await _harness.PaidAsync(80m, Ct);

        (await _harness.Online(OnlinePaymentsHarness.System()).ReverseAsync(payment.Id, Ct)).Error.ShouldBe(PaymentsErrors.OnlineAlreadyCaptured);
        (await _harness.Online(OnlinePaymentsHarness.System()).ReverseBookingAsync(payment.BookingId, Ct)).Value.ShouldHaveSingleItem().Status.ShouldBe(PaymentStatusInfo.Captured);
    }

    [Fact]
    public async Task A_release_asked_before_the_customer_paid_is_performed_when_the_bank_confirms_the_pre_authorization()
    {
        var online = _harness.Online();
        var started = (await online.StartPreAuthorizationAsync(_harness.Request(500m), Ct)).Value;
        var payment = _harness.Payment(started.PaymentId);

        var released = await _harness.Online(OnlinePaymentsHarness.System()).ReverseBookingAsync(payment.BookingId, Ct);
        released.Value.ShouldHaveSingleItem().PendingOperation.ShouldBe(PendingGatewayOperationInfo.Reverse);
        _harness.Gateway.Pay(payment.ExternalReference!);
        _harness.Time.Advance(TimeSpan.FromMinutes(3));
        await _harness.CatchUpJob().ExecuteAsync(Ct);

        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.PendingGatewayOperation.ShouldBeNull();
        _harness.Gateway.FindOrderByNumber(payment.OrderNumber)!.StatusCode.ShouldBe(FakeClicToPay.ReversedStatusCode);
    }

    [Fact]
    public async Task The_job_catches_up_a_payment_whose_customer_closed_the_tab_before_the_return()
    {
        var started = (await _harness.Online().StartPreAuthorizationAsync(_harness.Request(681.4m), Ct)).Value;
        var payment = _harness.Payment(started.PaymentId);
        _harness.Gateway.Pay(payment.ExternalReference!);

        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Pending, "younger than 2 minutes: left to the return");
        _harness.Time.Advance(TimeSpan.FromMinutes(3));
        await _harness.CatchUpJob().ExecuteAsync(Ct);

        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.StatusChangedByName.ShouldBe("system:tests");
    }

    [Fact]
    public async Task An_attempt_never_paid_expires_at_the_end_of_its_validity()
    {
        var started = (await _harness.Online().StartPreAuthorizationAsync(_harness.Request(681.4m), Ct)).Value;
        var payment = _harness.Payment(started.PaymentId);

        _harness.Time.Advance(TimeSpan.FromMinutes(59));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Pending);
        _harness.Time.Advance(TimeSpan.FromMinutes(2));
        await _harness.CatchUpJob().ExecuteAsync(Ct);

        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.ExpiredAtUtc.ShouldBe(_harness.Time.GetUtcNow());
        payment.DomainEvents.OfType<OnlinePaymentExpired>().ShouldHaveSingleItem().BookingId.ShouldBe(payment.BookingId);
    }

    [Fact]
    public async Task A_payment_made_late_on_a_page_left_open_is_recorded_by_the_return_and_flagged()
    {
        var online = _harness.Online();
        var started = (await online.StartPreAuthorizationAsync(_harness.Request(681.4m), Ct)).Value;
        var payment = _harness.Payment(started.PaymentId);
        _harness.Time.Advance(TimeSpan.FromMinutes(61));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Cancelled);

        _harness.Gateway.Pay(payment.ExternalReference!).IsSuccess.ShouldBeTrue();
        var returned = (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, payment.ExternalReference, false), Ct)).Value;

        returned.Status.ShouldBe(PaymentStatusInfo.PreAuthorized, "an order closed as expired is read again: the money is held");
        returned.StatusConfirmed.ShouldBeTrue();
        returned.RedirectPath.ShouldBe(OnlinePaymentsHarness.ReturnPath);
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.GatewayAttention.ShouldBe(GatewayAttention.LatePayment);
        _harness.StatusChangesOf(payment.Id).Select(c => (c.PreviousStatus, c.Status, c.Expired, c.PaidLate)).ShouldBe(
        [
            (PaymentStatusInfo.Pending, PaymentStatusInfo.Cancelled, true, false),
            (PaymentStatusInfo.Cancelled, PaymentStatusInfo.PreAuthorized, false, true),
        ]);
    }

    [Fact]
    public async Task The_job_records_a_late_payment_of_an_expired_payment_link_for_a_day()
    {
        var link = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).StartPaymentAsync(_harness.Request(350m, isLink: true), Ct)).Value;
        var payment = _harness.Payment(link.PaymentId);
        _harness.Time.Advance(TimeSpan.FromHours(25));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Cancelled);

        _harness.Gateway.Pay(payment.ExternalReference!);
        _harness.Time.Advance(TimeSpan.FromHours(2));
        await _harness.CatchUpJob().ExecuteAsync(Ct);

        payment.Status.ShouldBe(PaymentStatus.Captured);
        payment.CapturedAmount.ShouldBe(Money.Tnd(350m));
        payment.GatewayAttention.ShouldBe(GatewayAttention.LatePayment);
    }

    [Fact]
    public async Task A_payment_link_is_payable_for_its_validity_then_expires()
    {
        var link = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).StartPaymentAsync(_harness.Request(350m, isLink: true), Ct)).Value;
        var payment = _harness.Payment(link.PaymentId);

        link.ExpiresAtUtc.ShouldBe(FinanceHarness.Start.AddHours(24), "payments.payment_link_validity defaults to 24 h");
        payment.IsPaymentLink.ShouldBeTrue();
        payment.OnlineFlow.ShouldBe(OnlinePaymentFlow.OneStep);
        _harness.Gateway.FindOrderByNumber(link.OrderNumber)!.IsPreAuthorization.ShouldBeFalse();
        _harness.Time.Advance(TimeSpan.FromHours(23));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Pending);
        _harness.Time.Advance(TimeSpan.FromHours(2));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.ExpiredAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_payment_link_paid_in_time_is_captured_by_the_return()
    {
        var link = (await _harness.Online(OnlinePaymentsHarness.User(Roles.Agent)).StartPaymentAsync(_harness.Request(350m, isLink: true, validity: TimeSpan.FromHours(48)), Ct)).Value;
        var payment = _harness.Payment(link.PaymentId);
        link.ExpiresAtUtc.ShouldBe(FinanceHarness.Start.AddHours(48));
        _harness.Time.Advance(TimeSpan.FromHours(30));
        _harness.Gateway.Pay(payment.ExternalReference!);

        var returned = (await _harness.Online().HandleReturnAsync(new OnlinePaymentReturnRequest(link.OrderNumber, payment.ExternalReference, false), Ct)).Value;

        returned.Status.ShouldBe(PaymentStatusInfo.Captured);
        payment.CapturedAmount.ShouldBe(Money.Tnd(350m));
        payment.CapturedAtUtc.ShouldBe(_harness.Time.GetUtcNow());
    }

    [Fact]
    public async Task A_double_submission_returns_the_same_payment_page_without_a_second_order()
    {
        var bookingId = Guid.CreateVersion7();
        var online = _harness.Online();

        var first = (await online.StartPreAuthorizationAsync(_harness.Request(681.4m, bookingId), Ct)).Value;
        var second = (await online.StartPreAuthorizationAsync(_harness.Request(681.4m, bookingId), Ct)).Value;
        _harness.Time.Advance(TimeSpan.FromMinutes(11));
        var later = (await online.StartPreAuthorizationAsync(_harness.Request(681.4m, bookingId), Ct)).Value;

        second.PaymentId.ShouldBe(first.PaymentId);
        second.RedirectUrl.ShouldBe(first.RedirectUrl);
        second.Reused.ShouldBeTrue();
        later.PaymentId.ShouldNotBe(first.PaymentId, "past ten minutes the gateway session may be over: a new order");
        _harness.CallLog.Entries.Count(e => e.Operation == GatewayOperation.RegisterPreAuth).ShouldBe(2);
    }

    [Fact]
    public async Task A_registration_refused_by_the_gateway_fails_the_attempt()
    {
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.RegisterPreAuth, FakeFaultKind.Rejected, c => c.Description == "refused-register", "5"));

        var result = await _harness.Online().StartPreAuthorizationAsync(_harness.Request(100m, description: "refused-register"), Ct);

        result.Error.ShouldBe(PaymentsErrors.GatewayRejected);
        var payment = _harness.Db.Rows<Payment>().ShouldHaveSingleItem();
        payment.Status.ShouldBe(PaymentStatus.Failed);
        payment.StatusReason.ShouldBe("registration refused (payments.gateway.rejected)");
        _harness.StatusChangesOf(payment.Id).ShouldHaveSingleItem().Status.ShouldBe(PaymentStatusInfo.Failed);
    }

    [Fact]
    public async Task A_registration_without_answer_leaves_an_unpayable_attempt_that_expires()
    {
        _harness.Gateway.AddFault(new FakeGatewayFault(GatewayOperation.RegisterPreAuth, FakeFaultKind.LostResponse, c => c.Description == "lost-register"));

        var result = await _harness.Online().StartPreAuthorizationAsync(_harness.Request(100m, description: "lost-register"), Ct);

        result.Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        var payment = _harness.Db.Rows<Payment>().ShouldHaveSingleItem();
        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.GatewayFormUrl.ShouldBeNull("no payment page was handed out");
        payment.ExternalReference.ShouldNotBeNull("the status read by order number found the order");
        _harness.Time.Advance(TimeSpan.FromMinutes(61));
        await _harness.CatchUpJob().ExecuteAsync(Ct);
        payment.Status.ShouldBe(PaymentStatus.Cancelled);
    }

    [Fact]
    public async Task Return_paths_are_local_paths_only_and_amounts_are_millimes()
    {
        var online = _harness.Online();

        (await online.StartPreAuthorizationAsync(_harness.Request(100m) with { ReturnPath = "https://evil.example/" }, Ct)).Error.ShouldBe(PaymentsErrors.OnlinePathInvalid);
        (await online.StartPreAuthorizationAsync(_harness.Request(100m) with { FailPath = "//evil.example" }, Ct)).Error.ShouldBe(PaymentsErrors.OnlinePathInvalid);
        (await online.StartPreAuthorizationAsync(_harness.Request(100.0001m), Ct)).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        (await online.StartPreAuthorizationAsync(_harness.Request(100m) with { AgencyId = Guid.CreateVersion7() }, Ct)).Error.ShouldBe(PaymentsErrors.AgencyNotFound);
        (await online.StartPreAuthorizationAsync(_harness.Request(100m, description: new string('d', 100)), Ct)).Error.ShouldBe(PaymentsErrors.OnlineDescriptionTooLong);
        _harness.Gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_payment_id_placeholder_of_a_return_path_is_replaced()
    {
        var online = _harness.Online();
        var started = (await online.StartPaymentAsync(_harness.Request(60m) with { ReturnPath = "/fr/paiement/{paymentId}", FailPath = "/fr/paiement/{paymentId}" }, Ct)).Value;
        _harness.Gateway.Pay(_harness.Payment(started.PaymentId).ExternalReference!);

        var returned = (await online.HandleReturnAsync(new OnlinePaymentReturnRequest(started.OrderNumber, null, false), Ct)).Value;

        returned.RedirectPath.ShouldBe($"/fr/paiement/{started.PaymentId:D}");
    }

    [Fact]
    public async Task When_online_payment_is_off_nothing_is_recorded()
    {
        var online = _harness.Online(gateway: new OffGateway());

        (await online.StartPreAuthorizationAsync(_harness.Request(100m), Ct)).Error.ShouldBe(PaymentsErrors.GatewayDisabled);
        (await online.StartPaymentAsync(_harness.Request(100m), Ct)).Error.ShouldBe(PaymentsErrors.GatewayDisabled);
        (await online.CaptureAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(PaymentsErrors.GatewayDisabled);
        (await online.ReverseBookingAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(PaymentsErrors.GatewayDisabled);
        _harness.Db.Rows<Payment>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Online_operations_refuse_to_run_inside_a_unit_of_work()
    {
        var online = _harness.Online();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _harness.UnitOfWork.ExecuteAsync(async ct =>
            {
                await online.StartPreAuthorizationAsync(_harness.Request(100m), ct);
                return Result.Success();
            }, cancellationToken: Ct));
    }

    [Fact]
    public async Task The_booking_lists_its_online_payments_with_their_refunds()
    {
        var bookingId = Guid.CreateVersion7();
        var payment = await _harness.PreAuthorizedAsync(200m, Ct, bookingId);

        var payments = await _harness.Online().GetBookingOnlinePaymentsAsync(bookingId, Ct);

        var info = payments.ShouldHaveSingleItem();
        info.PaymentId.ShouldBe(payment.Id);
        info.Flow.ShouldBe(OnlinePaymentFlowInfo.PreAuthorization);
        info.CanCapture.ShouldBeTrue();
        info.CanReverse.ShouldBeTrue();
        info.CanRefund.ShouldBeFalse();
        info.GatewayStatusCode.ShouldBe(1);
        info.PaymentPageUrl.ShouldBe(payment.GatewayFormUrl);
        (await _harness.Online().FindAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }
}
