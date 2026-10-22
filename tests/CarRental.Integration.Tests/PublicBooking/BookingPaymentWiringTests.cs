using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;
using CarRental.Testing;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.PublicBooking.FunnelWorld;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The wiring between the requests and the online payments (ADR-0051) on agencies of the tests: the validation
/// captures the pre-authorization, a refused or expired request releases it through the outbox, the payment link mode
/// e-mails a link whose payment confirms the booking, and the checklist of the agent shows the payment state.
/// </summary>
public sealed class BookingPaymentWiringTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_validation_captures_the_guarantee_and_confirms_the_booking()
    {
        var agency = await CreateAsync(host, Ct);
        var brand = await AgencyNameAsync(agency);
        var request = await SubmitAsync(host, agency, InDays(40), InDays(44), Ct);
        request.Guarantee.Mode.ShouldBe(FunnelGuaranteeMode.PreAuthorization);
        var held = await HoldGuaranteeAsync(host, request, Ct);
        held.Status.ShouldBe(PaymentStatusInfo.PreAuthorized);

        var checklist = await EventuallyAsync(
            () => RunAsync<BookingAdministrationQueries, Result<RequestChecklist>>(host, Agent, q => q.GetChecklistAsync(request.BookingId, Ct)),
            c => c.IsSuccess && c.Value.Items.Single(i => i.Kind == ChecklistItemKind.Payment).Payment?.State == RequestPaymentState.PreAuthorized,
            "the payment state of the checklist follows the confirmed pre-authorization",
            Ct);
        checklist.Value.Items.Single(i => i.Kind == ChecklistItemKind.Payment).State.ShouldBe(ChecklistState.Ok);

        var validated = await ValidateAsync(host, request.BookingId, Ct);

        validated.IsSuccess.ShouldBeTrue(validated.IsFailure ? validated.Error.ToString() : null);
        (await StatusAsync(host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.Confirmed);
        var payment = (await PaymentsAsync(host, request.BookingId, Ct)).ShouldHaveSingleItem();
        payment.Status.ShouldBe(PaymentStatusInfo.Captured);
        payment.CapturedAmount.ShouldBe(payment.Amount);
        var confirmed = await TestEmails.WaitForAsync(host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject.Contains("confirmée", StringComparison.Ordinal), Ct);
        confirmed.Subject.ShouldBe($"{brand} — Réservation {request.Number} confirmée");
    }

    [Fact]
    public async Task A_refused_request_releases_its_pre_authorization_through_the_outbox()
    {
        var agency = await CreateAsync(host, Ct);
        var request = await SubmitAsync(host, agency, InDays(45), InDays(48), Ct);
        await HoldGuaranteeAsync(host, request, Ct);

        var refused = await RunAsync<BookingWorkflowService, Result>(host, Agent, s => s.RefuseAsync(request.BookingId, "Dossier incomplet", Ct));

        refused.IsSuccess.ShouldBeTrue(refused.IsFailure ? refused.Error.ToString() : null);
        var released = await EventuallyAsync(
            () => PaymentsAsync(host, request.BookingId, Ct),
            p => p.Count == 1 && p[0].Status == PaymentStatusInfo.Cancelled,
            "the pre-authorization is reversed once the refusal is committed",
            Ct);
        released[0].CapturedAmount.ShouldBe(Money.Zero);
        var email = await TestEmails.WaitForAsync(host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject.Contains(request.Number, StringComparison.Ordinal) && e.Text.Contains("pas pu être acceptée", StringComparison.Ordinal), Ct);
        email.Text.ShouldNotContain("Dossier incomplet", customMessage: "a refusal never tells its reason");
        email.Text.ShouldContain("libéré");
    }

    [Fact]
    public async Task An_expired_request_releases_its_pre_authorization()
    {
        var agency = await CreateAsync(host, Ct);
        var brand = await AgencyNameAsync(agency);
        await SetAsync(host, agency.AgencyId, "bookings.hold_duration", "00:00:05", Ct);
        var request = await SubmitAsync(host, agency, InDays(50), InDays(53), Ct);
        await HoldGuaranteeAsync(host, request, Ct);
        var hold = await RunAsync<IBookingsQueries, BookingSummaryInfo?>(host, Agent, q => q.GetSummaryAsync(request.BookingId, Ct));

        await EventuallyAsync(() => Task.FromResult(DateTimeOffset.UtcNow), now => now > hold!.HoldExpiresAt!.Value, "the hold passes", Ct);
        await RunJobAsync("bookings.expire");

        (await StatusAsync(host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.Expired);
        await EventuallyAsync(
            () => PaymentsAsync(host, request.BookingId, Ct),
            p => p.Count == 1 && p[0].Status == PaymentStatusInfo.Cancelled,
            "the expiry releases the held amount",
            Ct);
        await TestEmails.WaitForAsync(host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject == $"{brand} — Votre demande {request.Number} a expiré", Ct);
    }

    [Fact]
    public async Task In_the_payment_link_mode_the_link_is_emailed_and_its_payment_confirms_the_booking()
    {
        var agency = await CreateAsync(host, Ct);
        var brand = await AgencyNameAsync(agency);
        await SetAsync(host, agency.AgencyId, BookingSettings.ValidationPaymentMode.Key, nameof(BookingValidationPaymentMode.PaymentLinkAfterValidation), Ct);
        var request = await SubmitAsync(host, agency, InDays(55), InDays(58), Ct);
        request.Guarantee.Mode.ShouldBe(FunnelGuaranteeMode.PaymentLinkAfterValidation, "no pre-authorization in the payment link mode");
        await using (var scope = host.CreateScope(Funnel))
        {
            var guarantee = await scope.ServiceProvider.GetRequiredService<FunnelService>().StartGuaranteeAsync(request.BookingId, "fr", "/fr", "/fr", Ct);
            guarantee.IsSuccess.ShouldBeTrue();
            guarantee.Value.ShouldBeNull("nothing to pre-authorize");
        }

        var validated = await ValidateAsync(host, request.BookingId, Ct);

        validated.IsSuccess.ShouldBeTrue(validated.IsFailure ? validated.Error.ToString() : null);
        (await StatusAsync(host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.AwaitingPayment);
        var email = await TestEmails.WaitForAsync(host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject == $"{brand} — Réservation {request.Number} : votre lien de paiement", Ct);
        var link = TestEmails.Links(email).Single(l => l.Contains("/dev/clictopay/pay/", StringComparison.Ordinal));
        var payment = await PayAsync(host, new Uri(link).PathAndQuery, Ct);
        payment.Status.ShouldBe(PaymentStatusInfo.Captured);
        payment.IsPaymentLink.ShouldBeTrue();

        await EventuallyAsync(() => StatusAsync(host, request.BookingId, Ct), s => s == BookingStatusInfo.Confirmed, "the captured link confirms the booking", Ct);
        await TestEmails.WaitForAsync(host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject == $"{brand} — Réservation {request.Number} confirmée", Ct);
    }

    [Fact]
    public async Task A_request_whose_guarantee_was_never_paid_is_validated_with_a_payment_link()
    {
        var agency = await CreateAsync(host, Ct);
        var request = await SubmitAsync(host, agency, InDays(60), InDays(63), Ct);

        var validated = await ValidateAsync(host, request.BookingId, Ct);

        validated.IsSuccess.ShouldBeTrue(validated.IsFailure ? validated.Error.ToString() : null);
        (await StatusAsync(host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.AwaitingPayment);
        var links = await EventuallyAsync(
            () => PaymentsAsync(host, request.BookingId, Ct),
            p => p.Any(x => x.IsPaymentLink),
            "the payment link is created through the outbox",
            Ct);
        links.Single(p => p.IsPaymentLink).Flow.ShouldBe(OnlinePaymentFlowInfo.OneStep);
    }

    private async Task RunJobAsync(string jobId)
    {
        await using var scope = host.CreateScope(Application.Common.Security.UserContextSnapshot.System($"system:job:{jobId}"));
        var descriptor = scope.ServiceProvider.GetServices<Application.Common.BackgroundJobs.RecurringJobDescriptor>().Single(d => d.JobId == jobId);
        await ((Application.Common.BackgroundJobs.IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType)).ExecuteAsync(Ct);
    }

    /// <summary>The name of the agency, the brand of the e-mails to its customers (ADR-0067).</summary>
    private async Task<string> AgencyNameAsync(Bookings.BookingAgency agency) =>
        (await host.Factory.Services.GetRequiredService<CarRental.Application.Referential.Contracts.IAgencyDirectory>().FindAsync(agency.AgencyId, Ct)).ShouldNotBeNull().Name;
}
