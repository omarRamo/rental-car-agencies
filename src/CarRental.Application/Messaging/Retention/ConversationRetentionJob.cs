using System.Globalization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Retention;

/// <summary>
/// Retention of the WhatsApp exchanges (SPEC §11 "rétention limitée, 12 mois par défaut"): every night, agency by agency,
/// deletes the messages older than <c>messaging.retention_days</c> (365 by default, one value for the platform), then the
/// conversations without activity since, and the notification records of the same age, in batches; and unlinks the
/// conversations of persons anonymized in the Customers module (ADR-0027: an anonymized person keeps no link to personal
/// data). The counts of each agency go to its audit log.
/// </summary>
/// <remarks>
/// <para>Conversations and messages are plain tables (no temporal history), so that a purged message is really gone. The
/// bulk deletes bypass the audit log by design: the job writes one audit event with the counts.</para>
/// <para>The retention applies to every agency, whether its WhatsApp module is on or off (ADR-0067): a module switched
/// off keeps the data of the agency, never beyond the retention.</para>
/// </remarks>
internal sealed partial class ConversationRetentionJob(
    IAppDbContext db,
    ISettingsReader settings,
    IAgencyDirectory agencies,
    ICustomersQueries customers,
    IAuditTrail auditTrail,
    TimeProvider clock,
    ILogger<ConversationRetentionJob> logger) : IRecurringJob
{
    public const int BatchSize = 1_000;
    private const int MaxBatchesPerRun = 200;

    public static string JobId => "messaging.purge_conversations";

    /// <summary>Every night at 03:15, Tunis time.</summary>
    public static string CronExpression => "15 3 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var days = await settings.GetAsync(MessagingSettings.RetentionDays, cancellationToken: cancellationToken);
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(days);
        var known = (await agencies.ListAsync(cancellationToken)).Select(a => a.Id).ToList();
        foreach (var agencyId in known)
        {
            var messages = await DeleteInBatchesAsync(db.Set<Message>().Where(m => m.AgencyId == agencyId && m.CreatedAtUtc < cutoff), cancellationToken);
            var conversations = await DeleteInBatchesAsync(db.Set<Conversation>().Where(c => c.AgencyId == agencyId && c.LastActivityAtUtc < cutoff), cancellationToken);
            var notifications = await DeleteInBatchesAsync(db.Set<BookingNotification>().Where(n => n.AgencyId == agencyId && n.CreatedAtUtc < cutoff), cancellationToken);
            var unlinked = await UnlinkAnonymizedPersonsAsync(agencyId, cancellationToken);
            await RecordAsync(agencyId, messages, conversations, notifications, unlinked, days, cancellationToken);
        }

        // The exchanges of an agency the directory no longer lists (deleted) are purged all the same: the platform's record.
        var orphanMessages = await DeleteInBatchesAsync(db.Set<Message>().Where(m => !known.Contains(m.AgencyId) && m.CreatedAtUtc < cutoff), cancellationToken);
        var orphanConversations = await DeleteInBatchesAsync(db.Set<Conversation>().Where(c => !known.Contains(c.AgencyId) && c.LastActivityAtUtc < cutoff), cancellationToken);
        var orphanNotifications = await DeleteInBatchesAsync(db.Set<BookingNotification>().Where(n => !known.Contains(n.AgencyId) && n.CreatedAtUtc < cutoff), cancellationToken);
        await RecordAsync(null, orphanMessages, orphanConversations, orphanNotifications, 0, days, cancellationToken);
    }

    private async Task RecordAsync(Guid? agencyId, int messages, int conversations, int notifications, int unlinked, int days, CancellationToken cancellationToken)
    {
        if (messages + conversations + notifications + unlinked == 0)
        {
            return;
        }

        await auditTrail.RecordAsync(
            new AuditEvent(MessagingAuditActions.ConversationsPurged)
            {
                EntityType = nameof(Conversation),
                Details = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{messages} message(s), {conversations} conversation(s) and {notifications} notification record(s) older than {days} days deleted; {unlinked} conversation(s) unlinked from anonymized persons"),
                AgencyId = agencyId,
            },
            cancellationToken);
        LogPurged(messages, conversations, notifications, unlinked, days);
    }

    private static async Task<int> DeleteInBatchesAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken)
        where TEntity : class
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var deleted = await query.Take(BatchSize).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Conversations of the agency still linked to a person the Customers module anonymized (or no longer knows) lose the link.</summary>
    private async Task<int> UnlinkAnonymizedPersonsAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var personIds = await db.Set<Conversation>()
            .Where(c => c.AgencyId == agencyId && c.PersonId != null)
            .Select(c => c.PersonId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var gone = new List<Guid>();
        foreach (var personId in personIds)
        {
            if (await customers.GetPersonAsync(personId, cancellationToken) is null or { IsAnonymized: true })
            {
                gone.Add(personId);
            }
        }

        if (gone.Count == 0)
        {
            return 0;
        }

        var conversations = await db.Set<Conversation>()
            .Where(c => c.AgencyId == agencyId && c.PersonId != null && gone.Contains(c.PersonId.Value))
            .ToListAsync(cancellationToken);
        foreach (var conversation in conversations)
        {
            conversation.LinkPerson(null, null);
        }

        await db.SaveChangesAsync(cancellationToken);
        return conversations.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp retention ({Days} days): {Messages} message(s), {Conversations} conversation(s), {Notifications} notification record(s) deleted, {Unlinked} unlinked.")]
    private partial void LogPurged(int messages, int conversations, int notifications, int unlinked, int days);
}
