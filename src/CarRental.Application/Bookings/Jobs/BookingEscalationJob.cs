using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Email;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.Jobs;

/// <summary>A web request escalated to the managers (one line of the escalation e-mail).</summary>
public sealed record EscalatedRequestNotice(
    Guid BookingId,
    string Number,
    SalesChannel Channel,
    DateTimeOffset PendingSince,
    DateTimeOffset HoldExpiresAt,
    DateTimeOffset PickupAt);

/// <summary>Writes the localized escalation e-mail of a manager (implemented by the web host with its resources).</summary>
public interface IBookingEscalationEmailFactory
{
    EmailMessage Create(IReadOnlyList<EscalatedRequestNotice> requests, UserContactInfo recipient);
}

/// <summary>
/// Every five minutes: the web requests still <c>PendingValidation</c> after <c>bookings.escalation_delay</c> (SPEC §6
/// "une demande qui dépasse son délai remonte au gérant") are flagged — visible in the request queue — and the active
/// managers receive one e-mail listing them. The flags and the e-mails are written in one transaction (outbox), so a
/// request is escalated once per pending period, even when a run is replayed.
/// </summary>
internal sealed partial class BookingEscalationJob(
    BookingWriter writer,
    ISettingsReader settings,
    IIdentityQueries identity,
    IBookingEscalationEmailFactory emails,
    IEmailQueue emailQueue,
    ILogger<BookingEscalationJob> logger) : IRecurringJob
{
    public static string JobId => "bookings.escalate";

    public static string CronExpression => "*/5 * * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = writer.Now();
        var candidates = await writer.Db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.Status == BookingStatus.PendingValidation && b.EscalatedAtUtc == null && b.PendingSinceUtc != null)
            .Select(b => new { b.Id, b.AgencyId, b.PendingSinceUtc })
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return;
        }

        var due = new List<Guid>();
        foreach (var agency in candidates.GroupBy(c => c.AgencyId))
        {
            var delay = await settings.GetAsync(BookingSettings.EscalationDelay, agency.Key, cancellationToken);
            due.AddRange(agency.Where(c => c.PendingSinceUtc!.Value + delay <= now).Select(c => c.Id));
        }

        if (due.Count == 0)
        {
            return;
        }

        // Each agency's managers hear of their own requests only (ADR-0067).
        var managers = new Dictionary<Guid, IReadOnlyList<UserContactInfo>>();
        foreach (var agencyId in candidates.Where(c => due.Contains(c.Id)).Select(c => c.AgencyId).Distinct())
        {
            managers[agencyId] = await identity.GetActiveUsersInRoleAsync(agencyId, Roles.Manager, cancellationToken);
        }

        var escalated = await writer.RunAsync<IReadOnlyList<EscalatedRequestNotice>>(
            async (touched, ct) =>
            {
                var notices = new List<(Guid AgencyId, EscalatedRequestNotice Notice)>();
                var bookings = await writer.Db.Set<Booking>().Where(b => due.Contains(b.Id)).OrderBy(b => b.PendingSinceUtc).ToListAsync(ct);
                foreach (var booking in bookings)
                {
                    var flagged = booking.MarkEscalated(writer.Actor(), now);
                    if (flagged is { IsSuccess: true, Value: true })
                    {
                        notices.Add((booking.AgencyId, new EscalatedRequestNotice(
                            booking.Id,
                            booking.Number,
                            booking.Channel,
                            booking.PendingSinceUtc ?? booking.CreatedAtUtc,
                            booking.HoldExpiresAt ?? now,
                            booking.PickupAt)));
                    }
                }

                foreach (var agency in notices.GroupBy(n => n.AgencyId))
                {
                    var agencyNotices = agency.Select(n => n.Notice).ToList();
                    foreach (var manager in managers.GetValueOrDefault(agency.Key) ?? [])
                    {
                        await emailQueue.EnqueueAsync(emails.Create(agencyNotices, manager), ct);
                    }
                }

                return notices.Select(n => n.Notice).ToList();
            },
            cancellationToken);

        if (escalated.IsFailure)
        {
            // A request changed meanwhile: the next run escalates what is still pending.
            LogFailed(escalated.Error.Code);
            return;
        }

        if (escalated.Value.Count > 0)
        {
            LogEscalated(escalated.Value.Count, managers.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} pending request(s) escalated to {Managers} manager(s).")]
    private partial void LogEscalated(int count, int managers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Escalation of the pending requests postponed: {Code}.")]
    private partial void LogFailed(string code);
}
