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
/// Retention of the WhatsApp exchanges (SPEC §11 "rétention limitée, 12 mois par défaut"): every night, deletes the
/// messages older than <c>messaging.retention_days</c> (365 by default), then the conversations without activity since,
/// and the notification records of the same age, in batches; and unlinks the conversations of persons anonymized in the
/// Customers module (ADR-0027: an anonymized person keeps no link to personal data). The counts go to the audit log.
/// </summary>
/// <remarks>
/// Conversations and messages are plain tables (no temporal history), so that a purged message is really gone. The bulk
/// deletes bypass the audit log by design: the job writes one audit event with the counts.
/// </remarks>
internal sealed partial class ConversationRetentionJob(
    IAppDbContext db,
    ISettingsReader settings,
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

        var messages = await DeleteInBatchesAsync(db.Set<Message>().Where(m => m.CreatedAtUtc < cutoff), cancellationToken);
        var conversations = await DeleteInBatchesAsync(db.Set<Conversation>().Where(c => c.LastActivityAtUtc < cutoff), cancellationToken);
        var notifications = await DeleteInBatchesAsync(db.Set<BookingNotification>().Where(n => n.CreatedAtUtc < cutoff), cancellationToken);
        var unlinked = await UnlinkAnonymizedPersonsAsync(cancellationToken);

        if (messages + conversations + notifications + unlinked > 0)
        {
            await auditTrail.RecordAsync(
                new AuditEvent(MessagingAuditActions.ConversationsPurged)
                {
                    EntityType = nameof(Conversation),
                    Details = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{messages} message(s), {conversations} conversation(s) and {notifications} notification record(s) older than {days} days deleted; {unlinked} conversation(s) unlinked from anonymized persons"),
                },
                cancellationToken);
        }

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

    /// <summary>Conversations still linked to a person the Customers module anonymized (or no longer knows) lose the link.</summary>
    private async Task<int> UnlinkAnonymizedPersonsAsync(CancellationToken cancellationToken)
    {
        var personIds = await db.Set<Conversation>()
            .Where(c => c.PersonId != null)
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

        var conversations = await db.Set<Conversation>().Where(c => c.PersonId != null && gone.Contains(c.PersonId.Value)).ToListAsync(cancellationToken);
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
