using System.Text.RegularExpressions;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Notifications;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>
/// "Ma réservation" (ADR-0051): access by e-mail and booking number (same answer whether a booking matches or not), a
/// single-use link and a six-digit code (limited attempts) that open a short session limited to the booking, and what
/// the customer does there — follow the request, send the missing documents (pending submissions for the agent),
/// say the information is provided, pay the balance online, cancel under the policy (fee shown first, online refund
/// with the fee kept; above the approval threshold it waits for a manager), download the contract and invoices.
/// </summary>
/// <remarks>
/// Runs as a system actor working for the agency of the public site (the caller checked the session of the booking
/// first): a grant, a session and a booking of another agency are unknown here (ADR-0067). Nothing here reads or shows
/// the data stored about a person beyond what was typed for the booking.
/// </remarks>
public sealed partial class MyBookingService(
    IAppDbContext db,
    IBookingsQueries bookings,
    ICustomersQueries customers,
    ICustomerRegistration registration,
    IOnlinePayments payments,
    IPaymentsQueries paymentQueries,
    BookingWorkflowService workflow,
    IBookingRequests requests,
    IEnumerable<ICustomerDocumentProvider> documentProviders,
    IOutbox outbox,
    ISettingsReader settings,
    IAgencyDirectory agencies,
    TimeProvider clock,
    ILogger<MyBookingService> logger)
{
    /// <summary>The statuses of a booking whose customer may still send documents.</summary>
    private static readonly BookingStatus[] DocumentsExpected =
        [BookingStatus.PendingValidation, BookingStatus.InformationRequested, BookingStatus.OptionHeld, BookingStatus.AwaitingPayment, BookingStatus.Confirmed];

    /// <summary>The statuses that can be cancelled online (before the departure).</summary>
    private static readonly BookingStatus[] Cancellable =
        [BookingStatus.PendingValidation, BookingStatus.InformationRequested, BookingStatus.OptionHeld, BookingStatus.AwaitingPayment, BookingStatus.Confirmed];

    /// <summary>Timeline rows shown to the customer (assignments and escalations stay internal).</summary>
    private static readonly BookingEventType[] PublicEvents =
    [
        BookingEventType.Submitted, BookingEventType.InformationRequested, BookingEventType.InformationProvided, BookingEventType.Validated,
        BookingEventType.Refused, BookingEventType.Expired, BookingEventType.OptionConfirmed, BookingEventType.Paid, BookingEventType.Cancelled,
        BookingEventType.NoShow, BookingEventType.RentalStarted, BookingEventType.Returned, BookingEventType.Closed, BookingEventType.Amended,
        BookingEventType.Extended,
    ];

    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// An access request on the public site of <paramref name="agencyId"/>: when the number designates a booking of that
    /// agency whose contact has this e-mail (main or second driver as typed with the request, or the payer), a link and a
    /// code are sent to it (at most a few per hour and per booking). Numbers are unique within an agency only: a booking
    /// of another agency is never found, whatever the number and the e-mail (ADR-0067). The same answer is given in
    /// every case, and for a booking the same queries are made whether the address matches or not, the e-mail being
    /// composed out of the request (<see cref="AccessEmailRequested"/>): nothing tells whether the address is the one of
    /// the booking.
    /// </summary>
    public async Task<AccessRequestOutcome> RequestAccessAsync(
        Guid agencyId,
        string? email,
        string? number,
        string culture,
        string? publicBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var problems = new List<AccessRequestProblem>();
        var typedEmail = NormalizeEmail(email);
        if (typedEmail is null)
        {
            problems.Add(AccessRequestProblem.EmailInvalid);
        }

        var typedNumber = NormalizeNumber(number);
        if (typedNumber is null)
        {
            problems.Add(AccessRequestProblem.NumberInvalid);
        }

        if (problems.Count > 0)
        {
            return new AccessRequestOutcome(null, problems);
        }

        var now = clock.GetUtcNow();
        var booking = await bookings.FindByNumberAsync(agencyId, typedNumber!, cancellationToken);
        CustomerRecipient? recipient = null;
        string? site = null;
        if (booking is not null && booking.Status != BookingStatusInfo.Draft)
        {
            // The link leads to the site of the agency of the booking (read whether the address matches or not).
            site = await agencies.SlugAsync(booking.AgencyId, cancellationToken);
            var matched = await MatchContactAsync(booking, typedEmail!, cancellationToken);
            var limit = await settings.GetAsync(PublicBookingSettings.AccessRequestsPerHour, cancellationToken: cancellationToken);
            var since = now - ThrottleWindow;
            var recent = await db.Set<BookingAccessGrant>()
                .CountAsync(g => g.BookingId == booking.Id && g.Purpose == AccessPurpose.MyBooking && g.CreatedAtUtc > since, cancellationToken);
            if (matched is not null && recent >= limit)
            {
                LogThrottled(booking.Number);
            }
            else
            {
                recipient = matched;
            }
        }

        var token = AccessSecrets.NewToken();
        var code = AccessSecrets.NewCode();
        var salt = AccessSecrets.NewSalt();
        var validity = await settings.GetAsync(PublicBookingSettings.AccessLinkValidity, cancellationToken: cancellationToken);
        var grant = BookingAccessGrant.Issue(
            agencyId,
            recipient is null ? null : booking!.Id,
            AccessPurpose.MyBooking,
            AccessSecrets.HashToken(token),
            AccessSecrets.HashCode(code, salt),
            salt,
            culture,
            validity,
            now);
        db.Set<BookingAccessGrant>().Add(grant);
        if (recipient is not null)
        {
            outbox.Enqueue(new AccessEmailRequested(
                new AccessEmail(booking!.Number, culture, PublicBookingPaths.AccessLink(site!, culture, token), code, grant.ExpiresAtUtc, publicBaseUrl, booking.AgencyId),
                recipient));
        }

        await db.SaveChangesAsync(cancellationToken);
        return new AccessRequestOutcome(grant.Id, []);
    }

    /// <summary>The code typed for an access request: a session of its booking, or the same error whatever went wrong.</summary>
    public async Task<Result<MyBookingTicket>> OpenWithCodeAsync(Guid requestId, string? code, CancellationToken cancellationToken = default)
    {
        var grant = await db.Set<BookingAccessGrant>().FirstOrDefaultAsync(g => g.Id == requestId && g.Purpose == AccessPurpose.MyBooking, cancellationToken);
        if (grant is null)
        {
            return AccessErrors.AccessInvalid;
        }

        var attempts = await settings.GetAsync(PublicBookingSettings.CodeAttempts, cancellationToken: cancellationToken);
        var idle = await settings.GetAsync(PublicBookingSettings.SessionIdleTimeout, cancellationToken: cancellationToken);
        var opened = grant.OpenWithCode(code, attempts, idle, clock.GetUtcNow());

        // A wrong code counts even when the session does not open.
        var saved = await SaveAsync(cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        return saved.IsFailure ? AccessErrors.AccessInvalid : Ticket(grant);
    }

    /// <summary>The link of an e-mail was followed (confirmed by a click on the page): a session of its booking, once.</summary>
    public async Task<Result<MyBookingTicket>> OpenWithLinkAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!AccessSecrets.IsWellFormedToken(token))
        {
            return AccessErrors.AccessInvalid;
        }

        var hash = AccessSecrets.HashToken(token!);
        var grant = await db.Set<BookingAccessGrant>().FirstOrDefaultAsync(g => g.TokenHash == hash, cancellationToken);
        if (grant is null)
        {
            return AccessErrors.AccessInvalid;
        }

        var idle = await settings.GetAsync(PublicBookingSettings.SessionIdleTimeout, cancellationToken: cancellationToken);
        var opened = grant.OpenWithLink(idle, clock.GetUtcNow());
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        var saved = await SaveAsync(cancellationToken);
        return saved.IsFailure ? AccessErrors.AccessInvalid : Ticket(grant);
    }

    /// <summary>Whether the link can still be used (the page asks for a click before using it, so that link scanners do not).</summary>
    public async Task<bool> IsLinkUsableAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!AccessSecrets.IsWellFormedToken(token))
        {
            return false;
        }

        var hash = AccessSecrets.HashToken(token!);
        var now = clock.GetUtcNow();
        return await db.Set<BookingAccessGrant>()
            .AnyAsync(g => g.TokenHash == hash && g.BookingId != null && g.UsedAtUtc == null && g.InvalidatedAtUtc == null && g.ExpiresAtUtc > now, cancellationToken);
    }

    /// <summary>The session of the cookie, still open for its booking; its end slides with the activity.</summary>
    public async Task<Result<MyBookingTicket>> ResumeAsync(Guid grantId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        var grant = await db.Set<BookingAccessGrant>().FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken);
        var now = clock.GetUtcNow();
        if (grant is null || grant.BookingId != bookingId || !grant.IsSessionActiveAt(now))
        {
            return AccessErrors.SessionRequired;
        }

        var idle = await settings.GetAsync(PublicBookingSettings.SessionIdleTimeout, cancellationToken: cancellationToken);
        var before = grant.SessionExpiresAtUtc;
        grant.Touch(idle, now);
        if (grant.SessionExpiresAtUtc - before > TimeSpan.FromMinutes(1))
        {
            await SaveAsync(cancellationToken);
        }

        return Ticket(grant);
    }

    /// <summary>Ends the session of the cookie (sign out).</summary>
    public async Task SignOutAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        var grant = await db.Set<BookingAccessGrant>().FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken);
        if (grant is not null)
        {
            grant.Revoke(clock.GetUtcNow());
            await SaveAsync(cancellationToken);
        }
    }

    /// <summary>The booking of the session as its customer sees it.</summary>
    public async Task<Result<MyBookingView>> GetFileAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await bookings.GetSummaryAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var status = booking.Status.ToDomain();
        var culture = await CultureAsync(bookingId, cancellationToken);
        var paid = (await paymentQueries.GetBookingSummaryAsync(bookingId, cancellationToken)).Payments.NetPaid;
        var balance = booking.TotalInclVat - paid;
        if (balance.IsNegative)
        {
            balance = Money.Zero;
        }

        var events = await db.Set<BookingEvent>()
            .AsNoTracking()
            .Where(e => e.BookingId == bookingId)
            .OrderBy(e => e.Sequence)
            .Select(e => new { e.Type, e.OccurredAtUtc, e.Reason })
            .ToListAsync(cancellationToken);
        var timeline = events.Where(e => PublicEvents.Contains(e.Type)).Select(e => new MyBookingTimelineEntry(e.Type, e.OccurredAtUtc)).ToList();
        var information = status == BookingStatus.InformationRequested
            ? events.LastOrDefault(e => e.Type == BookingEventType.InformationRequested)?.Reason
            : null;
        var track = await db.Set<BookingPaymentTrack>().AsNoTracking().FirstOrDefaultAsync(t => t.BookingId == bookingId, cancellationToken);

        var drivers = new List<MyBookingDriver>();
        foreach (var (role, personId) in new[] { (FunnelFields.Main, booking.MainDriverId), (FunnelFields.Second, booking.SecondaryDriverId) })
        {
            if (personId is { } id)
            {
                drivers.Add(await DriverAsync(role, id, bookingId, cancellationToken));
            }
        }

        MyBookingCancellation? cancellation = null;
        if (Cancellable.Contains(status) && booking.PickupAt > now)
        {
            var preview = await workflow.PreviewCancellationAsync(bookingId, cancellationToken);
            if (preview.IsSuccess)
            {
                var refundable = await RefundableOnlineAsync(bookingId, cancellationToken);
                var estimate = Min(refundable, paid - preview.Value.Fee);
                cancellation = new MyBookingCancellation(
                    preview.Value.Fee,
                    preview.Value.FreeUntil,
                    preview.Value.FeePercent,
                    preview.Value.IsFree,
                    estimate.IsNegative ? Money.Zero : estimate);
            }
        }

        var documents = new List<CustomerDocumentInfo>();
        foreach (var provider in documentProviders)
        {
            documents.AddRange(await provider.GetDocumentsAsync(bookingId, cancellationToken));
        }

        return new MyBookingView(
            booking.Id,
            booking.Number,
            booking.Status,
            booking.Channel,
            booking.AgencyId,
            booking.CategoryId,
            booking.PickupLocationId,
            booking.ReturnLocationId,
            booking.PickupAt,
            booking.ReturnAt,
            booking.TotalInclVat,
            booking.Deposit,
            paid,
            balance,
            booking.HoldExpiresAt,
            booking.PaymentDueAt,
            information,
            track?.State ?? RequestPaymentState.None,
            timeline,
            drivers,
            DocumentsExpected.Contains(status),
            status == BookingStatus.InformationRequested && booking.HoldExpiresAt > now,
            cancellation,
            payments.IsAvailable && status is BookingStatus.Confirmed or BookingStatus.AwaitingPayment && balance.IsPositive,
            [.. documents.OrderBy(d => d.IssuedAtUtc)],
            documentProviders.Any(),
            culture);
    }

    /// <summary>
    /// A photo sent for a driver of the booking: staged, then added to the pending submission of the driver for this
    /// booking — applied to the person only when an agent accepts it.
    /// </summary>
    public async Task<Result<FunnelUploadResult>> SendDocumentAsync(Guid bookingId, string? slotKey, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var booking = await bookings.GetSummaryAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (!DocumentsExpected.Contains(booking.Status.ToDomain()))
        {
            return AccessErrors.DocumentsNotExpected;
        }

        var slot = UploadSlot.Parse(slotKey);
        var personId = slot?.Driver == FunnelDriver.Second ? booking.SecondaryDriverId : booking.MainDriverId;
        if (slot is null || personId is null)
        {
            return FunnelErrors.UploadSlotInvalid;
        }

        var staged = await registration.StageDocumentImageAsync(content, booking.AgencyId, cancellationToken);
        if (staged.IsFailure)
        {
            return staged.Error;
        }

        var type = slot.Document switch
        {
            FunnelDocument.License => IdentityDocumentTypeInfo.DrivingLicense,
            FunnelDocument.Permit => IdentityDocumentTypeInfo.InternationalDrivingPermit,
            _ => await IdentityTypeAsync(personId.Value, bookingId, cancellationToken),
        };
        var submitted = await registration.SubmitDocumentImageAsync(
            new DocumentImageSubmission(
                personId.Value,
                bookingId,
                booking.Channel == SalesChannel.WhatsApp ? ConsentChannelInfo.WhatsApp : ConsentChannelInfo.Web,
                type,
                slot.Side == FunnelSide.Back ? DocumentImageSideInfo.Back : DocumentImageSideInfo.Front,
                staged.Value.Id),
            cancellationToken);
        return submitted.IsFailure
            ? submitted.Error
            : new FunnelUploadResult(staged.Value.Id, slot.Key, staged.Value.ContentType, staged.Value.Length);
    }

    /// <summary>The customer sent what the agent asked for: the request goes back to the agent (<c>ProvideInformation</c>).</summary>
    public Task<Result> ProvideInformationAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        requests.ProvideInformationAsync(bookingId, cancellationToken);

    /// <summary>
    /// Pays the balance online (one-step payment, never a link): a payment page still open for the same amount is
    /// reused rather than starting a second one.
    /// </summary>
    public async Task<Result<OnlinePaymentRedirect>> StartBalancePaymentAsync(
        Guid bookingId,
        string culture,
        string returnPath,
        string failPath,
        CancellationToken cancellationToken = default)
    {
        var booking = await bookings.GetSummaryAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (booking.Status is not (BookingStatusInfo.Confirmed or BookingStatusInfo.AwaitingPayment))
        {
            return AccessErrors.NothingToPay;
        }

        if (!payments.IsAvailable)
        {
            return AccessErrors.PaymentUnavailable;
        }

        var paid = (await paymentQueries.GetBookingSummaryAsync(bookingId, cancellationToken)).Payments.NetPaid;
        var balance = booking.TotalInclVat - paid;
        if (!balance.IsPositive)
        {
            return AccessErrors.NothingToPay;
        }

        var now = clock.GetUtcNow();
        var open = (await payments.GetBookingOnlinePaymentsAsync(bookingId, cancellationToken))
            .LastOrDefault(p => p.Flow == OnlinePaymentFlowInfo.OneStep && p.Status == PaymentStatusInfo.Pending
                                && p.PendingOperation is null && p.PaymentPageUrl is not null && p.ExpiresAtUtc > now && p.Amount == balance);
        if (open is not null)
        {
            return new OnlinePaymentRedirect(open.PaymentId, open.OrderNumber, open.PaymentPageUrl!, open.ExpiresAtUtc!.Value, Reused: true);
        }

        var started = await payments.StartPaymentAsync(
            new OnlinePaymentRequest(bookingId, booking.AgencyId, balance.Amount, culture, returnPath, failPath, booking.Number),
            cancellationToken);
        if (started.IsFailure)
        {
            LogPaymentFailed(booking.Number, started.Error.Code);
            return AccessErrors.PaymentUnavailable;
        }

        return started.Value;
    }

    /// <summary>
    /// Cancels the booking under the policy of its agency (the fee shown first is the one kept), then refunds online what
    /// was paid online minus the fee (newest payment first; above the approval threshold the refund waits for a manager),
    /// and tells the customer. A pre-authorization still held is released by the outbox once the cancellation is committed.
    /// </summary>
    public async Task<Result<MyBookingCancelled>> CancelAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await bookings.GetSummaryAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (!Cancellable.Contains(booking.Status.ToDomain()) || booking.PickupAt <= clock.GetUtcNow())
        {
            return AccessErrors.CancellationNotAllowed;
        }

        var cancelled = await workflow.CancelAsync(bookingId, BookingReasons.CustomerCancelled, waiveFee: false, cancellationToken);
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        var fee = cancelled.Value;
        var paid = (await paymentQueries.GetBookingSummaryAsync(bookingId, cancellationToken)).Payments.NetPaid;
        var owed = paid - fee;
        var refund = CancellationRefundState.None;
        var refunded = Money.Zero;
        if (owed.IsPositive)
        {
            var remaining = owed;
            var online = (await payments.GetBookingOnlinePaymentsAsync(bookingId, cancellationToken))
                .Where(p => p.CanRefund)
                .OrderByDescending(p => p.CapturedAtUtc)
                .ToList();
            foreach (var payment in online)
            {
                var amount = Min(payment.RefundableAmount, remaining);
                if (!amount.IsPositive)
                {
                    break;
                }

                var requested = await payments.RefundAsync(new OnlineRefundRequest(payment.PaymentId, amount.Amount, $"Annulation client {booking.Number}"), cancellationToken);
                if (requested.IsFailure)
                {
                    LogRefundFailed(booking.Number, requested.Error.Code);
                    continue;
                }

                remaining -= amount;
                refunded += amount;
                refund = requested.Value.State == OnlineRefundStateInfo.AwaitingApproval || refund == CancellationRefundState.AwaitingApproval
                    ? CancellationRefundState.AwaitingApproval
                    : CancellationRefundState.Requested;
            }

            if (remaining.IsPositive && refund == CancellationRefundState.None)
            {
                refund = CancellationRefundState.ByTheAgency;
            }
        }

        var profile = await db.Set<BookingCustomerProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.BookingId == bookingId, cancellationToken);
        outbox.Enqueue(new BookingCustomerNotification(
            Guid.CreateVersion7(),
            bookingId,
            booking.AgencyId,
            booking.Number,
            BookingNotificationKind.CancellationConfirmed,
            profile?.Culture ?? "fr",
            clock.GetUtcNow(),
            Amount: owed.IsPositive ? owed : Money.Zero,
            Fee: fee,
            Refund: refund,
            BaseUrl: profile?.PublicBaseUrl));
        await db.SaveChangesAsync(cancellationToken);
        return new MyBookingCancelled(fee, owed.IsPositive ? owed : Money.Zero, refund);
    }

    /// <summary>A document of the booking (contract, invoice) from the providers registered by the packages that produce them.</summary>
    public async Task<CustomerDocumentContent?> OpenDocumentAsync(Guid bookingId, string documentId, CancellationToken cancellationToken = default)
    {
        foreach (var provider in documentProviders)
        {
            if (await provider.OpenAsync(bookingId, documentId, cancellationToken) is { } content)
            {
                return content;
            }
        }

        return null;
    }

    /// <summary>The e-mail address typed, normalized (lower case), or <see langword="null"/> when it cannot be one.</summary>
    internal static string? NormalizeEmail(string? email)
    {
        var text = email?.Trim().ToLowerInvariant();
        return text is { Length: > 3 and <= 254 } && EmailShape().IsMatch(text) ? text : null;
    }

    /// <summary>A booking number as typed (<c>loc 2027 000123</c>), normalized (<c>LOC-2027-000123</c>), or <see langword="null"/>.</summary>
    internal static string? NormalizeNumber(string? number)
    {
        var compact = new string([.. (number ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);
        return compact.Length == 13 && compact.StartsWith("LOC", StringComparison.Ordinal) && compact[3..].All(char.IsAsciiDigit)
            ? $"LOC-{compact[3..7]}-{compact[7..]}"
            : null;
    }

    private static Money Min(Money left, Money right) => left <= right ? left : right;

    private static MyBookingTicket Ticket(BookingAccessGrant grant) =>
        new(grant.Id, grant.BookingId!.Value, grant.SessionExpiresAtUtc!.Value, grant.Purpose, grant.Culture);

    private static bool SameEmail(string stored, string typed) => string.Equals(stored.Trim(), typed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The contact of the booking with this address: main or second driver as typed with the request, else the payer.
    /// Every contact is read whatever the answer, so that a match and a mismatch make the same queries.
    /// </summary>
    private async Task<CustomerRecipient?> MatchContactAsync(BookingSummaryInfo booking, string email, CancellationToken cancellationToken)
    {
        var contacts = new List<CustomerRecipient>(3);
        foreach (var personId in new[] { booking.MainDriverId, booking.SecondaryDriverId }.OfType<Guid>())
        {
            if (await customers.GetBookingContactAsync(personId, booking.Id, cancellationToken) is { Email: { } stored } contact)
            {
                contacts.Add(new CustomerRecipient(stored, contact.FullName));
            }
        }

        if (booking.PayerCustomerId is { } payerId && await customers.GetCustomerAsync(payerId, cancellationToken) is { Email: { } payerEmail } payer)
        {
            contacts.Add(new CustomerRecipient(payerEmail, payer.DisplayName));
        }

        return contacts.Find(c => SameEmail(c.Email, email));
    }

    private async Task<string> CultureAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Set<BookingCustomerProfile>().AsNoTracking().Where(p => p.BookingId == bookingId).Select(p => p.Culture).FirstOrDefaultAsync(cancellationToken) ?? "fr";

    private async Task<Money> RefundableOnlineAsync(Guid bookingId, CancellationToken cancellationToken) =>
        (await payments.GetBookingOnlinePaymentsAsync(bookingId, cancellationToken)).Where(p => p.CanRefund).Select(p => p.RefundableAmount).Sum();

    /// <summary>The identity document of a driver: by what was typed for the booking, else by the nationality on file (CIN for a Tunisian).</summary>
    private async Task<IdentityDocumentTypeInfo> IdentityTypeAsync(Guid personId, Guid bookingId, CancellationToken cancellationToken)
    {
        var submitted = (await customers.GetDriverSubmissionsAsync(personId, bookingId, cancellationToken))
            .SelectMany(s => s.Documents)
            .FirstOrDefault(d => d.Type is IdentityDocumentTypeInfo.NationalId or IdentityDocumentTypeInfo.Passport);
        if (submitted is not null)
        {
            return submitted.Type;
        }

        var person = await customers.GetPersonAsync(personId, cancellationToken);
        return person?.Nationality is { } nationality && !string.Equals(nationality, "TN", StringComparison.Ordinal)
            ? IdentityDocumentTypeInfo.Passport
            : IdentityDocumentTypeInfo.NationalId;
    }

    /// <summary>A driver with the slots sent for this booking only (never what the person on file holds).</summary>
    private async Task<MyBookingDriver> DriverAsync(string role, Guid personId, Guid bookingId, CancellationToken cancellationToken)
    {
        var contact = await customers.GetBookingContactAsync(personId, bookingId, cancellationToken);
        var sent = (await customers.GetDriverSubmissionsAsync(personId, bookingId, cancellationToken))
            .Where(s => s.State != DriverSubmissionStateInfo.Rejected)
            .SelectMany(s => s.Documents)
            .ToList();
        var identity = await IdentityTypeAsync(personId, bookingId, cancellationToken);
        bool Received(IdentityDocumentTypeInfo type, FunnelSide side) =>
            sent.Any(d => d.Type == type && (side == FunnelSide.Front ? d.HasFrontImage : d.HasBackImage));

        var driver = role == FunnelFields.Second ? FunnelDriver.Second : FunnelDriver.Main;
        var slots = new List<MyBookingSlot>();
        foreach (var (document, type) in new[]
                 {
                     (FunnelDocument.Identity, identity),
                     (FunnelDocument.License, IdentityDocumentTypeInfo.DrivingLicense),
                     (FunnelDocument.Permit, IdentityDocumentTypeInfo.InternationalDrivingPermit),
                 })
        {
            foreach (var side in new[] { FunnelSide.Front, FunnelSide.Back })
            {
                if (document == FunnelDocument.Permit && side == FunnelSide.Back)
                {
                    continue;
                }

                var optional = document == FunnelDocument.Permit || (identity == IdentityDocumentTypeInfo.Passport && document == FunnelDocument.Identity && side == FunnelSide.Back);
                slots.Add(new MyBookingSlot(new UploadSlot(driver, document, side).Key, document, side, Received(type, side), optional));
            }
        }

        return new MyBookingDriver(role, contact?.FullName ?? string.Empty, identity, slots);
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingErrors.ConcurrentUpdate;
        }
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Access e-mails of booking {Number} throttled (too many requests in an hour).")]
    private partial void LogThrottled(string number);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The balance payment of booking {Number} could not be started ({ErrorCode}).")]
    private partial void LogPaymentFailed(string number, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An online refund of the cancelled booking {Number} was not requested ({ErrorCode}): left to the agency.")]
    private partial void LogRefundFailed(string number, string errorCode);
}
