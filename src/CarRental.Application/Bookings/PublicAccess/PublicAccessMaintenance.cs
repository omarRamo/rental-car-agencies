using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Notifications;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.PublicAccess;

/// <summary>
/// Data minimization of the public pages (ADR-0049, ADR-0051): every hour, deletes the funnel sessions (drivers typed by
/// visitors, consents, photo references) a day after they expired or led to a request, and the access requests of
/// "Ma réservation" that never matched a booking or ended more than <see cref="GrantRetention"/> ago.
/// </summary>
internal sealed partial class PublicAccessPurgeJob(IAppDbContext db, TimeProvider clock, ILogger<PublicAccessPurgeJob> logger) : IRecurringJob
{
    /// <summary>A session is kept one more day after its end (a visitor coming back to its confirmation page).</summary>
    public static readonly TimeSpan SessionRetention = TimeSpan.FromHours(24);

    /// <summary>Access grants (hashes and instants only) are kept a month for the investigation of an abuse.</summary>
    public static readonly TimeSpan GrantRetention = TimeSpan.FromDays(30);

    private const int BatchSize = 200;

    public static string JobId => "bookings.purge_public_access";

    public static string CronExpression => "40 * * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var sessionCutoff = now - SessionRetention;
        var sessions = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await db.Set<FunnelSession>()
                .Where(s => s.ExpiresAtUtc < sessionCutoff || (s.SubmittedAtUtc != null && s.SubmittedAtUtc < sessionCutoff))
                .OrderBy(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var ids = batch.Select(s => s.Id).ToList();
            var uploads = await db.Set<FunnelUpload>().Where(u => ids.Contains(u.SessionId)).ToListAsync(cancellationToken);
            db.Set<FunnelUpload>().RemoveRange(uploads);
            db.Set<FunnelSession>().RemoveRange(batch);
            await db.SaveChangesAsync(cancellationToken);
            sessions += batch.Count;
            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        var grantCutoff = now - GrantRetention;
        var unmatchedCutoff = now - SessionRetention;
        var grants = await db.Set<BookingAccessGrant>()
            .Where(g => (g.BookingId == null && g.CreatedAtUtc < unmatchedCutoff) || g.CreatedAtUtc < grantCutoff)
            .OrderBy(g => g.Id)
            .Take(BatchSize * 5)
            .ToListAsync(cancellationToken);
        if (grants.Count > 0)
        {
            db.Set<BookingAccessGrant>().RemoveRange(grants);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (sessions + grants.Count > 0)
        {
            LogPurged(sessions, grants.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Public access purge: {Sessions} funnel session(s) and {Grants} access grant(s) deleted.")]
    private partial void LogPurged(int sessions, int grants);
}

/// <summary>
/// The "compléter mon dossier" link an agent sends to a customer (SPEC §6, booking by phone or at the counter, or a web
/// request whose documents are missing): a link opening the documents section of "Ma réservation", e-mailed to the main
/// driver in the language chosen by the agent. Permission <c>bookings.manage</c>.
/// </summary>
public sealed class CustomerLinkService(
    IAppDbContext db,
    IUserContext user,
    IBookingsQueries bookings,
    ICustomersQueries customers,
    IOutbox outbox,
    ISettingsReader settings,
    TimeProvider clock)
{
    private static readonly BookingStatusInfo[] DocumentsExpected =
        [BookingStatusInfo.PendingValidation, BookingStatusInfo.InformationRequested, BookingStatusInfo.OptionHeld, BookingStatusInfo.AwaitingPayment, BookingStatusInfo.Confirmed];

    /// <summary>Sends the link; <paramref name="publicBaseUrl"/> is the address of the site (links of the e-mail) when not configured.</summary>
    public async Task<Result> SendCompleteFileLinkAsync(Guid bookingId, string culture, string? publicBaseUrl = null, CancellationToken cancellationToken = default)
    {
        if (!user.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await bookings.GetSummaryAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (!DocumentsExpected.Contains(booking.Status))
        {
            return AccessErrors.DocumentsNotExpected;
        }

        if (booking.MainDriverId is not { } mainDriverId
            || await customers.GetBookingContactAsync(mainDriverId, bookingId, cancellationToken) is not { Email.Length: > 0 })
        {
            return AccessErrors.NoContactEmail;
        }

        var now = clock.GetUtcNow();
        var profile = await db.Set<BookingCustomerProfile>().FirstOrDefaultAsync(p => p.BookingId == bookingId, cancellationToken);
        if (profile is null)
        {
            profile = BookingCustomerProfile.Create(booking.AgencyId, bookingId, culture, whatsAppNotifications: false, now, publicBaseUrl);
            db.Set<BookingCustomerProfile>().Add(profile);
        }
        else
        {
            profile.KnowBaseUrl(publicBaseUrl);
        }

        var link = await AccessGrants.IssueCompleteFileLinkAsync(db, settings, booking.AgencyId, bookingId, culture, now, cancellationToken);
        outbox.Enqueue(new BookingCustomerNotification(
            Guid.CreateVersion7(),
            bookingId,
            booking.AgencyId,
            booking.Number,
            BookingNotificationKind.CompleteFileLink,
            culture,
            now,
            ActionPath: PublicBookingPaths.AccessLink(culture, link.Token, documents: true),
            ActionExpiresAtUtc: link.Grant.ExpiresAtUtc,
            BaseUrl: profile.PublicBaseUrl));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
