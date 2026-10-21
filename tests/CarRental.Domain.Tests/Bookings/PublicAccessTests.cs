using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>
/// The secrets and sessions of the public booking pages (ADR-0049, ADR-0051): tokens of 256 random bits stored hashed,
/// six-digit codes stored salted, funnel sessions that expire with their locked quote, and access grants that open once,
/// count wrong codes the same way whether a booking matched or not, and keep a sliding session capped at 24 hours.
/// </summary>
public sealed class PublicAccessTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_token_carries_256_random_bits_in_base64url_and_only_its_hash_is_kept()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => AccessSecrets.NewToken()).ToList();

        tokens.Distinct(StringComparer.Ordinal).Count().ShouldBe(200);
        tokens.ShouldAllBe(t => AccessSecrets.IsWellFormedToken(t));
        tokens.ShouldAllBe(t => t.Length == 43 && !t.Contains('=') && !t.Contains('+') && !t.Contains('/'));
        AccessSecrets.HashToken(tokens[0]).Length.ShouldBe(32);
        AccessSecrets.HashToken(tokens[0]).ShouldBe(AccessSecrets.HashToken(tokens[0]));
        AccessSecrets.HashToken(tokens[0]).ShouldNotBe(AccessSecrets.HashToken(tokens[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void A_malformed_token_is_refused_before_any_lookup(string? token) => AccessSecrets.IsWellFormedToken(token).ShouldBeFalse();

    [Fact]
    public void A_code_has_six_digits_and_is_stored_salted()
    {
        var code = AccessSecrets.NewCode();
        var salt = AccessSecrets.NewSalt();
        var otherSalt = AccessSecrets.NewSalt();

        code.Length.ShouldBe(6);
        code.ShouldAllBe(c => char.IsAsciiDigit(c));
        salt.Length.ShouldBe(16);
        AccessSecrets.HashCode(code, salt).ShouldBe(AccessSecrets.HashCode(code, salt));
        AccessSecrets.HashCode(code, salt).ShouldNotBe(AccessSecrets.HashCode(code, otherSalt));
        AccessSecrets.NormalizeCode($" {code[..3]} {code[3..]} ").ShouldBe(code);
        AccessSecrets.NormalizeCode("12345").ShouldBeNull();
        AccessSecrets.NormalizeCode("12a456").ShouldBeNull();
    }

    [Fact]
    public void A_funnel_session_expires_with_its_locked_quote_and_a_new_quote_restarts_the_guarantee()
    {
        var session = Session(expiresAt: Now.AddHours(24));

        session.IsExpiredAt(Now.AddHours(23)).ShouldBeFalse();
        session.IsExpiredAt(Now.AddHours(24)).ShouldBeTrue();
        session.Relock(Guid.NewGuid(), "B2C3D4", Now.AddHours(30), [ExtraChoice.Create("CHILD_SEAT", 1).Value], "price_updated", Now.AddHours(6)).IsSuccess.ShouldBeTrue();
        session.IsExpiredAt(Now.AddHours(29)).ShouldBeFalse();
        session.QuoteCode.ShouldBe("B2C3D4");
        session.Extras.ShouldHaveSingleItem().Code.ShouldBe("CHILD_SEAT");
        session.TakeNotice().ShouldBe("price_updated");
        session.TakeNotice().ShouldBeNull("a notice is shown once");
        session.Relock(Guid.NewGuid(), "C3D4E5", Now.AddHours(40), [], null, Now.AddHours(31)).Error.ShouldBe(FunnelErrors.SessionExpired);
    }

    [Fact]
    public void A_funnel_session_needs_the_consent_to_the_documents_and_leads_only_to_its_request_once_submitted()
    {
        var session = Session();

        session.SaveDrivers("{}", documentProcessing: false, false, false, Now).Error.ShouldBe(FunnelErrors.DocumentConsentRequired);
        session.SaveDraft("{\"draft\":true}", Now).IsSuccess.ShouldBeTrue();
        session.SaveDrivers("{}", documentProcessing: true, whatsAppNotifications: true, marketing: false, Now).IsSuccess.ShouldBeTrue();
        session.DraftJson.ShouldBeNull("valid drivers clear the draft");
        session.WhatsAppNotificationsConsent.ShouldBeTrue();
        session.MarkSubmitted(Guid.NewGuid(), "LOC-2027-000123", Now).IsSuccess.ShouldBeTrue();

        session.IsSubmitted.ShouldBeTrue();
        session.SaveDrivers("{}", true, false, false, Now).Error.ShouldBe(FunnelErrors.AlreadySubmitted);
        session.MarkSubmitted(Guid.NewGuid(), "LOC-2027-000124", Now).Error.ShouldBe(FunnelErrors.AlreadySubmitted);
    }

    [Fact]
    public void Each_step_of_a_funnel_session_is_measured_once()
    {
        var session = Session();

        session.MarkTracked(FunnelSteps.Options).ShouldBeTrue();
        session.MarkTracked(FunnelSteps.Options).ShouldBeFalse();
        session.MarkTracked(FunnelSteps.Documents).ShouldBeTrue();
        session.TrackedSteps.ShouldBe(FunnelSteps.Options | FunnelSteps.Documents);
    }

    [Fact]
    public void A_funnel_session_cannot_start_on_an_expired_quote()
    {
        var start = Start(expiresAt: Now);

        FunnelSession.Start(AccessSecrets.HashToken(AccessSecrets.NewToken()), start, Now).Error.ShouldBe(BookingErrors.QuoteExpired);
    }

    [Theory]
    [InlineData("main.license.back", FunnelDriver.Main, FunnelDocument.License, FunnelSide.Back)]
    [InlineData("SECOND.IDENTITY.FRONT", FunnelDriver.Second, FunnelDocument.Identity, FunnelSide.Front)]
    [InlineData(" main.permit.front ", FunnelDriver.Main, FunnelDocument.Permit, FunnelSide.Front)]
    public void Photo_slots_are_read_from_their_keys(string key, FunnelDriver driver, FunnelDocument document, FunnelSide side) =>
        UploadSlot.Parse(key).ShouldBe(new UploadSlot(driver, document, side));

    [Theory]
    [InlineData(null)]
    [InlineData("main.license")]
    [InlineData("third.license.front")]
    [InlineData("main.selfie.front")]
    public void Unknown_photo_slots_are_refused(string? key) => UploadSlot.Parse(key).ShouldBeNull();

    [Fact]
    public void A_link_opens_one_session_once_before_its_expiry()
    {
        var (grant, _) = Grant();

        grant.OpenWithLink(TimeSpan.FromHours(2), Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        grant.IsSessionActiveAt(Now.AddMinutes(11)).ShouldBeTrue();
        grant.OpenWithLink(TimeSpan.FromHours(2), Now.AddMinutes(12)).Error.ShouldBe(AccessErrors.AccessInvalid, "single use");

        var (late, _) = Grant();
        late.OpenWithLink(TimeSpan.FromHours(2), Now.AddMinutes(31)).Error.ShouldBe(AccessErrors.AccessInvalid);
    }

    [Fact]
    public void The_right_code_opens_the_session_and_wrong_codes_lock_the_grant()
    {
        var (grant, code) = Grant();
        var wrong = code == "000000" ? "111111" : "000000";

        for (var attempt = 1; attempt < 5; attempt++)
        {
            grant.OpenWithCode(wrong, 5, TimeSpan.FromHours(2), Now).Error.ShouldBe(AccessErrors.AccessInvalid);
        }

        grant.OpenWithCode(wrong, 5, TimeSpan.FromHours(2), Now).Error.ShouldBe(AccessErrors.AccessLocked);
        grant.OpenWithCode(code, 5, TimeSpan.FromHours(2), Now).Error.ShouldBe(AccessErrors.AccessLocked, "the right code no longer opens it");
        grant.OpenWithLink(TimeSpan.FromHours(2), Now).Error.ShouldBe(AccessErrors.AccessLocked, "nor the link");

        var (fresh, freshCode) = Grant();
        fresh.OpenWithCode($"{freshCode[..3]} {freshCode[3..]}", 5, TimeSpan.FromHours(2), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_request_that_matched_no_booking_answers_and_locks_exactly_like_one_that_did()
    {
        var code = AccessSecrets.NewCode();
        var salt = AccessSecrets.NewSalt();
        var grant = BookingAccessGrant.Issue(null, AccessPurpose.MyBooking, AccessSecrets.HashToken(AccessSecrets.NewToken()), AccessSecrets.HashCode(code, salt), salt, "fr", TimeSpan.FromMinutes(30), Now);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            grant.OpenWithCode(code, 5, TimeSpan.FromHours(2), Now).Error.ShouldBe(AccessErrors.AccessInvalid);
        }

        grant.OpenWithCode(code, 5, TimeSpan.FromHours(2), Now).Error.ShouldBe(AccessErrors.AccessLocked);
        grant.FailedCodeAttempts.ShouldBe(5);
    }

    [Fact]
    public void A_session_slides_with_the_activity_but_never_beyond_24_hours()
    {
        var (grant, _) = Grant();
        grant.OpenWithLink(TimeSpan.FromHours(2), Now).IsSuccess.ShouldBeTrue();

        grant.IsSessionActiveAt(Now.AddHours(2)).ShouldBeFalse("idle for two hours");
        for (var hour = 1; hour <= 23; hour++)
        {
            grant.Touch(TimeSpan.FromHours(2), Now.AddHours(hour));
        }

        grant.SessionExpiresAtUtc.ShouldBe(Now.AddHours(24));
        grant.IsSessionActiveAt(Now.AddHours(23.9)).ShouldBeTrue();
        grant.IsSessionActiveAt(Now.AddHours(24)).ShouldBeFalse();
        grant.Revoke(Now.AddHours(1));
        grant.IsSessionActiveAt(Now.AddHours(1.5)).ShouldBeFalse("signed out");
    }

    [Theory]
    [InlineData("https://www.agence.tn/fr/reserver/x?y=1", "https://www.agence.tn/")]
    [InlineData("http://localhost:5000/", "http://localhost:5000/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://agence.tn/", null)]
    [InlineData(null, null)]
    public void A_profile_keeps_only_the_origin_of_the_site_the_customer_used(string? baseUrl, string? expected) =>
        BookingCustomerProfile.Create(Guid.NewGuid(), "fr", false, Now, baseUrl).PublicBaseUrl.ShouldBe(expected);

    [Fact]
    public void A_payment_track_keeps_a_late_payment_flag_once_raised()
    {
        var track = BookingPaymentTrack.Create(Guid.NewGuid(), Now);

        track.Apply(RequestPaymentState.Expired, Guid.NewGuid(), Money.Tnd(100m), paidLate: false, Now).ShouldBeTrue();
        track.Apply(RequestPaymentState.Captured, track.PaymentId, Money.Tnd(100m), paidLate: true, Now).ShouldBeTrue();
        track.Apply(RequestPaymentState.Captured, track.PaymentId, Money.Tnd(100m), paidLate: false, Now).ShouldBeFalse();
        track.PaidLate.ShouldBeTrue();
    }

    private static (BookingAccessGrant Grant, string Code) Grant()
    {
        var code = AccessSecrets.NewCode();
        var salt = AccessSecrets.NewSalt();
        var grant = BookingAccessGrant.Issue(Guid.NewGuid(), AccessPurpose.MyBooking, AccessSecrets.HashToken(AccessSecrets.NewToken()), AccessSecrets.HashCode(code, salt), salt, "fr", TimeSpan.FromMinutes(30), Now);
        return (grant, code);
    }

    private static FunnelSession Session(DateTimeOffset? expiresAt = null) =>
        FunnelSession.Start(AccessSecrets.HashToken(AccessSecrets.NewToken()), Start(expiresAt ?? Now.AddHours(24)), Now).Value;

    private static FunnelStart Start(DateTimeOffset expiresAt) => new(
        Guid.NewGuid(),
        "fr",
        SalesChannel.Web,
        BookingAttribution.None,
        Guid.NewGuid(),
        "ECMR",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Now.AddDays(30),
        Now.AddDays(34),
        "?lieu=TUN",
        Guid.NewGuid(),
        "A1B2C3",
        expiresAt,
        []);
}
