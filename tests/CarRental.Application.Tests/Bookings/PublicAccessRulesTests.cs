using System.Text.Json;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Notifications;
using CarRental.Application.Bookings.Payments;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Bookings;

/// <summary>
/// The rules of the public booking funnel and of "Ma réservation" that need no database (ADR-0049, ADR-0051): when the
/// locked quote of a deep link carries a search, the guaranteed amount, what the customer types, which notification a
/// status change sends, the payment state kept from the online payments, and the access e-mail composed out of the
/// request.
/// </summary>
public sealed class PublicAccessRulesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Agency = Guid.NewGuid();
    private static readonly Guid Category = Guid.NewGuid();
    private static readonly Guid Office = Guid.NewGuid();
    private static readonly Guid Airport = Guid.NewGuid();

    [Fact]
    public void A_locked_quote_carries_the_funnel_of_exactly_its_search()
    {
        var request = Request();

        FunnelService.Matches(Quote(), request, Now).ShouldBeTrue();
        FunnelService.Matches(Quote() with { IsExpired = true }, request, Now).ShouldBeFalse();
        FunnelService.Matches(Quote() with { ExpiresAtUtc = Now }, request, Now).ShouldBeFalse();
        FunnelService.Matches(Quote() with { CategoryId = Guid.NewGuid() }, request, Now).ShouldBeFalse();
        FunnelService.Matches(Quote() with { Channel = SalesChannel.WhatsApp }, request, Now).ShouldBeFalse("a WhatsApp price is not a web price");
        FunnelService.Matches(Quote() with { ReturnLocationId = Airport }, request, Now).ShouldBeFalse();
        FunnelService.Matches(Quote() with { ReturnAt = request.ReturnAt.AddHours(1) }, request, Now).ShouldBeFalse();
        FunnelService.Matches(Quote() with { GuaranteedVehicleId = Guid.NewGuid() }, request, Now).ShouldBeFalse("a guaranteed model is an agent's quote");
    }

    [Theory]
    [InlineData(681.400, 1.0, 681.400)]
    [InlineData(681.400, 0.3, 204.420)]
    [InlineData(100.005, 0.5, 50.003)]
    [InlineData(500, 0, 5.000)]
    [InlineData(500, 2, 500.000)]
    public void The_guaranteed_amount_is_the_ratio_of_the_total_to_the_millime(decimal total, decimal ratio, decimal expected) =>
        FunnelService.GuaranteeAmount(Money.Tnd(total), ratio).ShouldBe(Money.Tnd(expected));

    [Theory]
    [InlineData("LOC-2027-000123", "LOC-2027-000123")]
    [InlineData("loc 2027 000123", "LOC-2027-000123")]
    [InlineData(" LOC2027000123 ", "LOC-2027-000123")]
    [InlineData("LOC-2027-00012", null)]
    [InlineData("ABC-2027-000123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_booking_number_is_read_as_typed(string? typed, string? expected) => MyBookingService.NormalizeNumber(typed).ShouldBe(expected);

    [Theory]
    [InlineData(" Amel@Example.TEST ", "amel@example.test")]
    [InlineData("amel@example", null)]
    [InlineData("not an e-mail", null)]
    [InlineData(null, null)]
    public void An_email_is_read_as_typed(string? typed, string? expected) => MyBookingService.NormalizeEmail(typed).ShouldBe(expected);

    [Theory]
    [InlineData(BookingStatusInfo.Draft, BookingStatusInfo.PendingValidation, BookingNotificationKind.RequestReceived)]
    [InlineData(BookingStatusInfo.PendingValidation, BookingStatusInfo.InformationRequested, BookingNotificationKind.InformationRequested)]
    [InlineData(BookingStatusInfo.PendingValidation, BookingStatusInfo.Confirmed, BookingNotificationKind.Confirmed)]
    [InlineData(BookingStatusInfo.AwaitingPayment, BookingStatusInfo.Confirmed, BookingNotificationKind.Confirmed)]
    [InlineData(BookingStatusInfo.InformationRequested, BookingStatusInfo.Refused, BookingNotificationKind.Refused)]
    [InlineData(BookingStatusInfo.AwaitingPayment, BookingStatusInfo.Expired, BookingNotificationKind.Expired)]
    [InlineData(BookingStatusInfo.PendingValidation, BookingStatusInfo.AwaitingPayment, null)]
    [InlineData(BookingStatusInfo.Confirmed, BookingStatusInfo.InProgress, null)]
    [InlineData(BookingStatusInfo.Confirmed, BookingStatusInfo.Cancelled, null)]
    public void A_status_change_sends_its_notification(BookingStatusInfo previous, BookingStatusInfo status, BookingNotificationKind? expected) =>
        BookingNotificationPublisher.KindOf(new BookingStatusChanged(Guid.NewGuid(), Agency, "LOC-2027-000001", SalesChannel.Web, previous, status, Money.Tnd(100m), null, Now))
            .ShouldBe(expected);

    [Fact]
    public void The_payment_state_of_a_booking_comes_from_all_its_online_payments()
    {
        var booking = Guid.NewGuid();
        var failed = FakeOnlinePayments.Info(booking, OnlinePaymentFlowInfo.PreAuthorization, PaymentStatusInfo.Failed, Money.Tnd(300m));
        var held = FakeOnlinePayments.Info(booking, OnlinePaymentFlowInfo.PreAuthorization, PaymentStatusInfo.PreAuthorized, Money.Tnd(300m));
        var captured = FakeOnlinePayments.Info(booking, OnlinePaymentFlowInfo.OneStep, PaymentStatusInfo.Captured, Money.Tnd(300m), isLink: true);
        var expired = FakeOnlinePayments.Info(booking, OnlinePaymentFlowInfo.OneStep, PaymentStatusInfo.Cancelled, Money.Tnd(300m), isLink: true, expiredAtUtc: Now);
        var reversed = FakeOnlinePayments.Info(booking, OnlinePaymentFlowInfo.PreAuthorization, PaymentStatusInfo.Cancelled, Money.Tnd(300m));

        BookingOnlinePaymentHandler.Summarize([]).State.ShouldBe(RequestPaymentState.None);
        BookingOnlinePaymentHandler.Summarize([failed]).State.ShouldBe(RequestPaymentState.Failed);
        BookingOnlinePaymentHandler.Summarize([failed, held]).ShouldBe((RequestPaymentState.PreAuthorized, held.PaymentId, Money.Tnd(300m)));
        BookingOnlinePaymentHandler.Summarize([held, expired]).State.ShouldBe(RequestPaymentState.PreAuthorized, "a held amount matters more than a later attempt");
        BookingOnlinePaymentHandler.Summarize([expired, captured]).State.ShouldBe(RequestPaymentState.Captured);
        BookingOnlinePaymentHandler.Summarize([expired]).State.ShouldBe(RequestPaymentState.Expired);
        BookingOnlinePaymentHandler.Summarize([reversed]).State.ShouldBe(RequestPaymentState.Released);
    }

    [Fact]
    public async Task The_access_email_of_a_matched_request_is_composed_out_of_the_request_and_queued()
    {
        var requested = new AccessEmailRequested(
            new AccessEmail("LOC-2027-000123", "en", "/en/my-booking/access/TOKEN", "042817", Now.AddMinutes(30), "https://www.agence.tn"),
            new CustomerRecipient("amel@example.test", "Amel Ben Salah"));

        // The message goes through the outbox as JSON (web defaults): it comes back whole.
        var stored = JsonSerializer.Deserialize<AccessEmailRequested>(JsonSerializer.Serialize(requested, JsonOptions), JsonOptions);
        stored.ShouldBe(requested);

        var composer = new RecordingComposer();
        var outbox = new RecordingOutbox();
        await new AccessEmailHandler(composer, outbox).HandleAsync(stored!, new OutboxMessageContext(Guid.CreateVersion7(), Now, 1, null), TestContext.Current.CancellationToken);

        composer.Access.ShouldHaveSingleItem().ShouldBe((requested.Email, requested.Recipient));
        var email = outbox.Messages.ShouldHaveSingleItem().ShouldBeOfType<EmailRequested>().Message;
        email.To.ShouldBe("amel@example.test");
        email.Subject.ShouldBe("Access LOC-2027-000123");
    }

    private sealed class RecordingComposer : ICustomerEmailComposer
    {
        public List<(AccessEmail Email, CustomerRecipient Recipient)> Access { get; } = [];

        public Task<EmailMessage> ComposeNotificationAsync(BookingCustomerNotification notification, CustomerRecipient recipient, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Only the access e-mail is expected.");

        public Task<EmailMessage> ComposeAccessAsync(AccessEmail email, CustomerRecipient recipient, CancellationToken cancellationToken = default)
        {
            Access.Add((email, recipient));
            return Task.FromResult(new EmailMessage(recipient.Email, $"Access {email.Number}", $"<p>{email.Code}</p>"));
        }
    }

    private static FunnelStartRequest Request() => new(
        Agency,
        "fr",
        SalesChannel.Web,
        null,
        Category,
        "ECMR",
        Office,
        Office,
        Now.AddDays(30),
        Now.AddDays(34),
        "?lieu=TUN",
        "A1B2C3");

    private static LockedQuoteInfo Quote() => new(
        Guid.NewGuid(),
        "A1B2C3",
        Agency,
        Category,
        SalesChannel.Web,
        Office,
        Office,
        Now.AddDays(30),
        Now.AddDays(34),
        4,
        Money.Tnd(400m),
        Money.Tnd(600m),
        [],
        null,
        null,
        Now.AddHours(20),
        false);
}
