using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Jobs;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Domain.Bookings;

namespace CarRental.Application.Tests.Bookings;

/// <summary>The expiry job (PROMPT §6 invariant 11) and the escalation of the pending requests to the managers.</summary>
public sealed class BookingJobsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_jobs_run_every_minute_and_every_five_minutes()
    {
        BookingExpiryJob.JobId.ShouldBe("bookings.expire");
        BookingExpiryJob.CronExpression.ShouldBe("* * * * *");
        BookingEscalationJob.JobId.ShouldBe("bookings.escalate");
        BookingEscalationJob.CronExpression.ShouldBe("*/5 * * * *");
    }

    [Fact]
    public async Task Requests_options_and_unpaid_validations_expire_once_their_deadline_passed()
    {
        var harness = new BookingsHarness();
        var request = await harness.PendingAsync();
        var option = await harness.ConfirmedAsync(harness.Quick(QuickBookingOutcome.Option) with { OptionExpiresAt = BookingsHarness.Start.AddHours(30) });
        harness.Settings.Set(BookingSettings.ValidationPaymentMode, BookingValidationPaymentMode.PaymentLinkAfterValidation);
        var unpaid = await harness.PendingAsync();
        (await harness.Workflow.ValidateAsync(unpaid.Id, Ct)).IsSuccess.ShouldBeTrue();
        var confirmed = await harness.ConfirmedAsync();
        harness.AsSystem("system:job:bookings.expire");
        harness.Reset();

        harness.Clock.Advance(TimeSpan.FromHours(23));
        await harness.ExpiryJob.ExecuteAsync(Ct);
        harness.Notifier.Changed.ShouldBeEmpty("no deadline passed yet");

        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.ExpiryJob.ExecuteAsync(Ct);
        request.Status.ShouldBe(BookingStatus.Expired);
        unpaid.Status.ShouldBe(BookingStatus.Expired, "the payment link was valid 24 hours");
        option.Status.ShouldBe(BookingStatus.OptionHeld);

        harness.Clock.Advance(TimeSpan.FromHours(6));
        await harness.ExpiryJob.ExecuteAsync(Ct);
        option.Status.ShouldBe(BookingStatus.Expired);
        confirmed.Status.ShouldBe(BookingStatus.Confirmed);

        request.Events[^1].ActorName.ShouldBe("system:job:bookings.expire");
        request.Events[^1].ActorUserId.ShouldBeNull();
        harness.Notifier.Changed.Count.ShouldBe(3, "the capacity of each expired file is released, after its commit");
        harness.Outbox.Messages.OfType<BookingStatusChanged>().ShouldAllBe(m => m.Status == BookingStatusInfo.Expired);

        harness.Reset();
        await harness.ExpiryJob.ExecuteAsync(Ct);
        harness.Notifier.Changed.ShouldBeEmpty("idempotent");
    }

    [Fact]
    public async Task A_pending_request_is_escalated_once_to_every_manager()
    {
        var harness = new BookingsHarness();
        harness.Managers.Managers.Add(new UserContactInfo(Guid.CreateVersion7(), "Gérant", "gerant@tests.local"));
        harness.Managers.Managers.Add(new UserContactInfo(Guid.CreateVersion7(), "Gérante", "gerante@tests.local"));
        var old = await harness.PendingAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(90));
        var recent = await harness.PendingAsync();
        harness.AsSystem("system:job:bookings.escalate");

        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await harness.EscalationJob.ExecuteAsync(Ct);

        old.EscalatedAtUtc.ShouldBe(BookingsHarness.Start.AddHours(2));
        recent.EscalatedAtUtc.ShouldBeNull("pending for 30 minutes only");
        harness.Managers.Sent.Count.ShouldBe(2);
        harness.Managers.Sent.ShouldAllBe(mail => mail.HtmlBody == old.Number);
        old.Events[^1].Type.ShouldBe(BookingEventType.Escalated);

        await harness.EscalationJob.ExecuteAsync(Ct);
        harness.Managers.Sent.Count.ShouldBe(2, "a request is escalated once");
    }

    [Fact]
    public async Task The_escalation_delay_comes_from_the_settings()
    {
        var harness = new BookingsHarness();
        harness.Settings.Set(BookingSettings.EscalationDelay, TimeSpan.FromMinutes(30));
        harness.Managers.Managers.Add(new UserContactInfo(Guid.CreateVersion7(), "Gérant", "gerant@tests.local"));
        var request = await harness.PendingAsync();
        harness.AsSystem();

        harness.Clock.Advance(TimeSpan.FromMinutes(31));
        await harness.EscalationJob.ExecuteAsync(Ct);

        request.EscalatedAtUtc.ShouldNotBeNull();
    }
}
