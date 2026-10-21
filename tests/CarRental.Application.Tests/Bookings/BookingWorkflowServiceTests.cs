using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings;

/// <summary>Decisions on a file: validation (eligibility, silent customer check, payment mode), information, refusal, options, cancellation, no-show.</summary>
public sealed class BookingWorkflowServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Validation_confirms_an_eligible_request_without_checking_the_capacity_again()
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();

        var validated = await harness.Workflow.ValidateAsync(booking.Id, Ct);

        validated.Value.ShouldBe(ValidationOutcome.Confirmed);
        booking.Status.ShouldBe(BookingStatus.Confirmed);
        harness.Guard.AvailabilityChecks.ShouldBeEmpty("a validated request already holds its capacity");
        harness.Customers.BlacklistChecks.ShouldBe([harness.MainDriverId]);
        harness.Outbox.Messages.OfType<BookingStatusChanged>().ShouldHaveSingleItem().PreviousStatus.ShouldBe(BookingStatusInfo.PendingValidation);
        harness.Journal.Entries.Skip(harness.JournalStart).ShouldBe(["begin", "commit", $"notify {harness.Ecmr.Id}"]);
    }

    [Fact]
    public async Task In_the_fallback_payment_mode_the_validated_request_waits_for_its_payment()
    {
        var harness = new BookingsHarness();
        harness.Settings.Set(BookingSettings.ValidationPaymentMode, BookingValidationPaymentMode.PaymentLinkAfterValidation);
        harness.Settings.Set(BookingSettings.PaymentLinkValidity, TimeSpan.FromHours(12));
        var booking = await harness.PendingAsync();

        var validated = await harness.Workflow.ValidateAsync(booking.Id, Ct);

        validated.Value.ShouldBe(ValidationOutcome.AwaitingPayment);
        booking.Status.ShouldBe(BookingStatus.AwaitingPayment);
        booking.PaymentDueAt.ShouldBe(BookingsHarness.Start.AddHours(12));
        harness.Outbox.Messages.OfType<BookingStatusChanged>().Single().PaymentDueAt.ShouldBe(BookingsHarness.Start.AddHours(12));

        (await harness.Workflow.MarkPaidAsync(booking.Id, "CTP-2027-42", Ct)).IsSuccess.ShouldBeTrue();
        booking.Status.ShouldBe(BookingStatus.Confirmed);
        (await harness.Workflow.MarkPaidAsync(booking.Id, "CTP-2027-42", Ct)).IsSuccess.ShouldBeTrue("idempotent on a confirmed file");
    }

    [Fact]
    public async Task A_driver_who_fails_the_customer_check_gets_the_request_refused_with_a_generic_reason()
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();
        harness.Customers.Blacklisted.Add(harness.MainDriverId);

        var validated = await harness.Workflow.ValidateAsync(booking.Id, Ct);

        validated.Value.ShouldBe(ValidationOutcome.RefusedAfterCustomerCheck);
        booking.Status.ShouldBe(BookingStatus.Refused);
        booking.StatusReason.ShouldBe(BookingReasons.CustomerCheck);
        booking.Events[^1].Reason.ShouldBe(BookingReasons.CustomerCheck);
        harness.Notifier.Changed.ShouldBe([harness.Ecmr.Id], "the refusal releases the capacity");
    }

    [Fact]
    public async Task A_driver_who_is_not_eligible_blocks_the_validation()
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();
        harness.Customers.Issues[harness.MainDriverId] = [new EligibilityIssueInfo(EligibilityIssueKindInfo.LicenseHeldTooShort, null, null, 2, 1)];

        (await harness.Workflow.ValidateAsync(booking.Id, Ct)).Error.ShouldBe(BookingErrors.DriversNotEligible);
        booking.Status.ShouldBe(BookingStatus.PendingValidation);
        harness.UnitOfWork.Executions.ShouldBe(1, "only the arrangement");
    }

    [Theory]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Only_users_who_manage_requests_decide_on_them(string role)
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();
        harness.As(role);

        (await harness.Workflow.ValidateAsync(booking.Id, Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Workflow.RefuseAsync(booking.Id, "Non", Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Workflow.RequestInformationAsync(booking.Id, InformationRequestReason.Other, "?", Ct)).Error.ShouldBe(BookingErrors.Forbidden);
    }

    [Fact]
    public async Task Validating_twice_or_an_unknown_file_is_refused()
    {
        var harness = new BookingsHarness();
        var booking = await harness.PendingAsync();
        (await harness.Workflow.ValidateAsync(booking.Id, Ct)).IsSuccess.ShouldBeTrue();

        (await harness.Workflow.ValidateAsync(booking.Id, Ct)).Error.ShouldBe(BookingErrors.InvalidTransition);
        (await harness.Workflow.ValidateAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(BookingErrors.NotFound);
    }

    [Fact]
    public async Task An_information_request_extends_the_hold_by_the_setting_and_a_refusal_needs_a_reason()
    {
        var harness = new BookingsHarness();
        harness.Settings.Set(BookingSettings.InformationRequestExtension, TimeSpan.FromHours(12));
        var booking = await harness.PendingAsync();

        (await harness.Workflow.RequestInformationAsync(booking.Id, InformationRequestReason.UnreadableDocuments, "Photo floue.", Ct)).IsSuccess.ShouldBeTrue();
        booking.HoldExpiresAt.ShouldBe(BookingsHarness.Start.AddHours(36));
        booking.Status.ShouldBe(BookingStatus.InformationRequested);

        (await harness.Workflow.ProvideInformationAsync(booking.Id, Ct)).IsSuccess.ShouldBeTrue();
        harness.Guard.AvailabilityChecks.ShouldHaveSingleItem("providing information consumes capacity again (§3)");

        (await harness.Workflow.RefuseAsync(booking.Id, " ", Ct)).Error.ShouldBe(BookingErrors.ReasonRequired);
        (await harness.Workflow.RefuseAsync(booking.Id, "Permis non conforme", Ct)).IsSuccess.ShouldBeTrue();
        booking.Status.ShouldBe(BookingStatus.Refused);
    }

    [Fact]
    public async Task Confirming_an_option_is_checked_again_under_the_lock()
    {
        var harness = new BookingsHarness();
        var option = await harness.ConfirmedAsync(harness.Quick(QuickBookingOutcome.Option));
        harness.Guard.NextAvailability = AvailabilityErrors.NotAvailable;

        (await harness.Workflow.ConfirmOptionAsync(option.Id, Ct)).Error.ShouldBe(AvailabilityErrors.NotAvailable);
        option.Status.ShouldBe(BookingStatus.OptionHeld);

        harness.Guard.NextAvailability = Result.Success();
        (await harness.Workflow.ConfirmOptionAsync(option.Id, Ct)).IsSuccess.ShouldBeTrue();
        option.Status.ShouldBe(BookingStatus.Confirmed);
        harness.Guard.AvailabilityChecks.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_late_cancellation_of_a_confirmed_booking_costs_the_policy_fee()
    {
        var harness = new BookingsHarness();
        var booking = await harness.ConfirmedAsync();
        harness.Clock.SetUtcNow(harness.PickupAt.AddDays(-2));

        var preview = await harness.Workflow.PreviewCancellationAsync(booking.Id, Ct);
        preview.Value.Fee.ShouldBe(Money.Tnd(61.428m), "20 % of 307.140");
        preview.Value.FreeUntil.ShouldBe(new DateOnly(2027, 6, 8));

        var fee = await harness.Workflow.CancelAsync(booking.Id, "Le client annule", cancellationToken: Ct);

        fee.Value.ShouldBe(Money.Tnd(61.428m));
        booking.Status.ShouldBe(BookingStatus.Cancelled);
        booking.CancellationFee.ShouldBe(Money.Tnd(61.428m));
        harness.Notifier.Changed.ShouldBe([harness.Ecmr.Id]);
    }

    [Fact]
    public async Task An_early_cancellation_is_free_and_only_a_manager_waives_a_fee()
    {
        var harness = new BookingsHarness();
        var early = await harness.ConfirmedAsync();
        (await harness.Workflow.CancelAsync(early.Id, "Changement de dates", cancellationToken: Ct)).Value.ShouldBe(Money.Zero);

        var late = await harness.ConfirmedAsync();
        harness.Clock.SetUtcNow(harness.PickupAt.AddDays(-1));
        (await harness.Workflow.CancelAsync(late.Id, "Panne du véhicule prévu", waiveFee: true, Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        harness.As(Roles.Manager);
        (await harness.Workflow.CancelAsync(late.Id, "Panne du véhicule prévu", waiveFee: true, Ct)).Value.ShouldBe(Money.Zero);
        late.Events[^1].Facts["feeWaived"].ShouldBe("true");
    }

    [Fact]
    public async Task A_no_show_waits_for_the_delay_of_the_agency()
    {
        var harness = new BookingsHarness();
        harness.Settings.Set(BookingSettings.NoShowDelay, TimeSpan.FromHours(3));
        var booking = await harness.ConfirmedAsync();

        harness.Clock.SetUtcNow(harness.PickupAt.AddHours(2));
        (await harness.Workflow.MarkNoShowAsync(booking.Id, "Absent", Ct)).Error.ShouldBe(BookingErrors.NoShowTooEarly);
        harness.Clock.SetUtcNow(harness.PickupAt.AddHours(3));
        (await harness.Workflow.MarkNoShowAsync(booking.Id, "Absent, injoignable", Ct)).IsSuccess.ShouldBeTrue();
        booking.Status.ShouldBe(BookingStatus.NoShow);
    }

    [Fact]
    public async Task The_payment_contract_marks_a_validated_request_paid()
    {
        var harness = new BookingsHarness();
        harness.Settings.Set(BookingSettings.ValidationPaymentMode, BookingValidationPaymentMode.PaymentLinkAfterValidation);
        var booking = await harness.PendingAsync();
        (await harness.Workflow.ValidateAsync(booking.Id, Ct)).IsSuccess.ShouldBeTrue();
        harness.AsSystem("system:payments");

        var payments = new BookingPaymentStatus(harness.Workflow);
        (await payments.MarkPaidAsync(booking.Id, "ORDER-9", Ct)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(BookingStatus.Confirmed);
        booking.Events[^1].ActorName.ShouldBe("system:payments");
    }
}
