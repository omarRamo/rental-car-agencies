using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Access;

/// <summary>Why a link to "Ma réservation" was issued.</summary>
public enum AccessPurpose
{
    /// <summary>Asked by the customer (e-mail and booking number): signed link and six-digit code.</summary>
    MyBooking = 1,

    /// <summary>"Compléter mon dossier" (information requested by an agent, or sent by an agent): opens the documents section.</summary>
    CompleteFile = 2,
}

/// <summary>
/// An access to "Ma réservation" (ADR-0051): a single-use link (random token, hash stored) and, for a request of the
/// customer, a six-digit code (salted hash, limited attempts), both valid a short time; once used, the grant is the
/// server side of a short session limited to its booking — sliding, never beyond <see cref="SessionLifetime"/> after
/// it was opened, revocable. A request that matched no booking is recorded too (same work, same answer: no
/// enumeration) and can never be opened.
/// </summary>
public sealed class BookingAccessGrant : AggregateRoot
{
    public const int CultureMaxLength = 5;

    /// <summary>A session never lasts longer than this after its link or code was used.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    private BookingAccessGrant()
    {
    }

    private BookingAccessGrant(Guid? bookingId, AccessPurpose purpose, byte[] tokenHash, byte[]? codeHash, byte[]? codeSalt, string culture, DateTimeOffset now, TimeSpan validity)
        : base(NewId())
    {
        BookingId = bookingId;
        Purpose = purpose;
        TokenHash = tokenHash;
        CodeHash = codeHash;
        CodeSalt = codeSalt;
        Culture = culture;
        CreatedAtUtc = now.ToUniversalTime();
        ExpiresAtUtc = CreatedAtUtc + validity;
    }

    /// <summary>The booking it opens; <see langword="null"/> when the request matched none (it cannot be opened).</summary>
    public Guid? BookingId { get; private set; }

    public AccessPurpose Purpose { get; private set; }

    /// <summary>SHA-256 of the token of the link, unique.</summary>
    public byte[] TokenHash { get; private set; } = null!;

    /// <summary>SHA-256 of the salt and the code; <see langword="null"/> for a link without code.</summary>
    public byte[]? CodeHash { get; private set; }

    public byte[]? CodeSalt { get; private set; }

    /// <summary>Language of the e-mail and of the pages it opens.</summary>
    public string Culture { get; private set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>End of the validity of the link and the code (before use).</summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>When the link or the code was used (single use).</summary>
    public DateTimeOffset? UsedAtUtc { get; private set; }

    public int FailedCodeAttempts { get; private set; }

    /// <summary>Too many wrong codes: neither the link nor the code can be used any more.</summary>
    public DateTimeOffset? InvalidatedAtUtc { get; private set; }

    /// <summary>End of the session opened by the grant (sliding).</summary>
    public DateTimeOffset? SessionExpiresAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public bool IsUsed => UsedAtUtc is not null;

    /// <summary>A link (and code) for <paramref name="bookingId"/>, valid <paramref name="validity"/>; no booking: never openable.</summary>
    public static BookingAccessGrant Issue(
        Guid? bookingId,
        AccessPurpose purpose,
        byte[] tokenHash,
        byte[]? codeHash,
        byte[]? codeSalt,
        string culture,
        TimeSpan validity,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validity, TimeSpan.Zero);
        if (tokenHash.Length != AccessSecrets.HashLength || (codeHash is null) != (codeSalt is null) || codeHash is { Length: not AccessSecrets.HashLength })
        {
            throw new ArgumentException("Hashes are SHA-256; a code comes with its salt.");
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown access purpose.");
        }

        return new BookingAccessGrant(bookingId, purpose, tokenHash, codeHash, codeSalt, culture.Length > CultureMaxLength ? culture[..CultureMaxLength] : culture, now, validity);
    }

    /// <summary>
    /// The link was followed: opens the session once, before the expiry, unless invalidated. Every failure is the same
    /// <see cref="AccessErrors.AccessInvalid"/> (nothing tells an expired link from an unknown one).
    /// </summary>
    public Result OpenWithLink(TimeSpan idleTimeout, DateTimeOffset now) => Open(idleTimeout, now);

    /// <summary>
    /// The code was typed: compared in constant time; a wrong code counts, and <paramref name="maxAttempts"/> wrong codes
    /// invalidate the grant (<see cref="AccessErrors.AccessLocked"/>).
    /// </summary>
    public Result OpenWithCode(string? code, int maxAttempts, TimeSpan idleTimeout, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        if (InvalidatedAtUtc is not null)
        {
            return AccessErrors.AccessLocked;
        }

        if (UsedAtUtc is not null || now >= ExpiresAtUtc || CodeHash is null || CodeSalt is null)
        {
            return AccessErrors.AccessInvalid;
        }

        // A request that matched no booking behaves exactly like one that did: its code is compared, every code is wrong,
        // and the attempts count and lock it the same way (the answers never tell whether a booking exists).
        var normalized = AccessSecrets.NormalizeCode(code);
        var matches = normalized is not null && AccessSecrets.SameHash(CodeHash, AccessSecrets.HashCode(normalized, CodeSalt));
        if (!matches || BookingId is null)
        {
            FailedCodeAttempts++;
            if (FailedCodeAttempts >= maxAttempts)
            {
                InvalidatedAtUtc = now.ToUniversalTime();
                return AccessErrors.AccessLocked;
            }

            return AccessErrors.AccessInvalid;
        }

        return Open(idleTimeout, now);
    }

    /// <summary>The session of the grant is open now (link used, not revoked, not past its sliding or absolute end).</summary>
    public bool IsSessionActiveAt(DateTimeOffset now) =>
        UsedAtUtc is { } used
        && RevokedAtUtc is null
        && SessionExpiresAtUtc is { } expires
        && now < expires
        && now < used + SessionLifetime;

    /// <summary>Activity in the session: its end slides by <paramref name="idleTimeout"/>, never beyond the absolute lifetime.</summary>
    public void Touch(TimeSpan idleTimeout, DateTimeOffset now)
    {
        if (IsSessionActiveAt(now))
        {
            SessionExpiresAtUtc = Cap(now + idleTimeout);
        }
    }

    /// <summary>The customer signed out (or the session must end): it cannot be used again.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAtUtc ??= now.ToUniversalTime();

    private bool IsOpenable(DateTimeOffset now) => BookingId is not null && UsedAtUtc is null && InvalidatedAtUtc is null && now < ExpiresAtUtc;

    private Result Open(TimeSpan idleTimeout, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);
        if (!IsOpenable(now))
        {
            return InvalidatedAtUtc is null ? AccessErrors.AccessInvalid : AccessErrors.AccessLocked;
        }

        UsedAtUtc = now.ToUniversalTime();
        SessionExpiresAtUtc = Cap(UsedAtUtc.Value + idleTimeout);
        return Result.Success();
    }

    private DateTimeOffset Cap(DateTimeOffset end) => UsedAtUtc is { } used && end > used + SessionLifetime ? used + SessionLifetime : end;
}

/// <summary>
/// What the public pages know about a booking made online (no personal data): the language of the customer for the
/// e-mails and pages, and the WhatsApp notifications opt-in (read by the WhatsApp channel, phase 4).
/// </summary>
public sealed class BookingCustomerProfile : Entity
{
    public const int BaseUrlMaxLength = 200;

    private BookingCustomerProfile()
    {
    }

    private BookingCustomerProfile(Guid bookingId, string culture, bool whatsAppNotifications, string? publicBaseUrl, DateTimeOffset now)
        : base(NewId())
    {
        BookingId = bookingId;
        Culture = culture;
        WhatsAppNotifications = whatsAppNotifications;
        PublicBaseUrl = publicBaseUrl;
        CreatedAtUtc = now.ToUniversalTime();
    }

    public Guid BookingId { get; private set; }

    public string Culture { get; private set; } = null!;

    public bool WhatsAppNotifications { get; private set; }

    /// <summary>
    /// The address of the site the customer used (an accepted host), for the links of the e-mails when
    /// <c>PublicSite:BaseUrl</c> is not configured.
    /// </summary>
    public string? PublicBaseUrl { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static BookingCustomerProfile Create(Guid bookingId, string culture, bool whatsAppNotifications, DateTimeOffset now, string? publicBaseUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        if (bookingId == Guid.Empty)
        {
            throw new ArgumentException("A profile belongs to a booking.", nameof(bookingId));
        }

        var baseUrl = Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
                      && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                      && uri.AbsoluteUri.Length <= BaseUrlMaxLength
            ? uri.GetLeftPart(UriPartial.Authority) + "/"
            : null;
        return new BookingCustomerProfile(
            bookingId,
            culture.Length > BookingAccessGrant.CultureMaxLength ? culture[..BookingAccessGrant.CultureMaxLength] : culture,
            whatsAppNotifications,
            baseUrl,
            now);
    }

    /// <summary>Records the site address when none is known yet (a link sent by an agent from the back office).</summary>
    public void KnowBaseUrl(string? publicBaseUrl)
    {
        if (PublicBaseUrl is null && Create(BookingId, Culture, WhatsAppNotifications, CreatedAtUtc, publicBaseUrl).PublicBaseUrl is { } known)
        {
            PublicBaseUrl = known;
        }
    }
}

/// <summary>Where the online payment of a request stands, as told by the Payments module (agent checklist, "Ma réservation").</summary>
public enum RequestPaymentState
{
    /// <summary>No online payment (fallback mode, gateway disabled, counter).</summary>
    None = 0,

    /// <summary>A payment was started; the customer has not paid (or the bank has not confirmed) yet.</summary>
    AwaitingCustomer = 1,

    PreAuthorized = 2,

    /// <summary>Refused by the bank or abandoned.</summary>
    Failed = 3,

    /// <summary>The customer did not pay within the validity of the attempt.</summary>
    Expired = 4,

    Captured = 5,

    /// <summary>The pre-authorization was released (refusal, expiry, cancellation).</summary>
    Released = 6,

    Refunded = 7,
}

/// <summary>
/// The payment state of a booking kept by the Bookings module from the confirmed changes of its online payments
/// (contract message of the Payments module): the most relevant payment, its amount and a flag for a payment made
/// after its expiry (to check).
/// </summary>
public sealed class BookingPaymentTrack : Entity
{
    private BookingPaymentTrack()
    {
    }

    private BookingPaymentTrack(Guid bookingId, DateTimeOffset now)
        : base(NewId())
    {
        BookingId = bookingId;
        UpdatedAtUtc = now.ToUniversalTime();
    }

    public Guid BookingId { get; private set; }

    public RequestPaymentState State { get; private set; }

    public Guid? PaymentId { get; private set; }

    /// <summary>Amount held (pre-authorized) or taken (captured) by the payment.</summary>
    public Money? Amount { get; private set; }

    /// <summary>A payment was made after its attempt had been closed as expired: the agent checks the booking.</summary>
    public bool PaidLate { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static BookingPaymentTrack Create(Guid bookingId, DateTimeOffset now)
    {
        if (bookingId == Guid.Empty)
        {
            throw new ArgumentException("A payment track belongs to a booking.", nameof(bookingId));
        }

        return new BookingPaymentTrack(bookingId, now);
    }

    /// <summary>Sets the current state (recomputed from every online payment of the booking); returns whether it changed.</summary>
    public bool Apply(RequestPaymentState state, Guid? paymentId, Money? amount, bool paidLate, DateTimeOffset now)
    {
        var flag = PaidLate || paidLate;
        if (State == state && PaymentId == paymentId && Amount == amount && PaidLate == flag)
        {
            return false;
        }

        State = state;
        PaymentId = paymentId;
        Amount = amount;
        PaidLate = flag;
        UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }
}
