using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Fleet.Deadlines;

/// <summary>A document deadline to notify (one e-mail per recipient).</summary>
public sealed record DeadlineAlertNotice(
    Guid VehicleId,
    string PlateNumber,
    string Make,
    string Model,
    VehicleDocumentType DocumentType,
    string DocumentNumber,
    DateOnly ExpiresOn,
    int DaysLeft,
    DeadlineAlertLevel Level);

/// <summary>Writes the localized e-mail of a deadline alert (implemented by the web host with its resources).</summary>
public interface IDeadlineAlertEmailFactory
{
    EmailMessage Create(DeadlineAlertNotice notice, UserContactInfo recipient);
}

/// <summary>
/// Every day at 07:00 (Tunis): for each current document of a vehicle in the fleet that expires within 30, 15 or 7 days
/// or has expired, one e-mail per active manager, once per (document, level). The sent alert and its e-mails are written
/// in the same transaction (outbox), so a run that fails or is replayed never sends twice; a renewal is a new document.
/// </summary>
internal sealed partial class DeadlineAlertJob(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IIdentityQueries identity,
    IDeadlineAlertEmailFactory emails,
    IEmailQueue emailQueue,
    TimeProvider timeProvider,
    ILogger<DeadlineAlertJob> logger) : IRecurringJob
{
    public static string JobId => "fleet.deadline_alerts";

    public static string CronExpression => "0 7 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = TunisTime.DateOf(now);
        var due = await DueAlertsAsync(today, cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        var managers = await identity.GetActiveUsersInRoleAsync(Roles.Manager, cancellationToken);
        if (managers.Count == 0)
        {
            LogNoRecipient(due.Count);
            return;
        }

        var sent = 0;
        foreach (var alert in due)
        {
            if (await SendAsync(alert, managers, now, cancellationToken))
            {
                sent++;
            }
        }

        LogSent(sent, managers.Count);
    }

    /// <summary>Records the alert and queues its e-mails atomically; false when it was already sent.</summary>
    private async Task<bool> SendAsync(DueAlert alert, IReadOnlyList<UserContactInfo> managers, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    // Checked inside the operation: a replay after a transient failure starts afresh.
                    if (await db.Set<DeadlineAlert>().AnyAsync(a => a.DocumentId == alert.Document.Id && a.Level <= alert.Level, ct))
                    {
                        return Result.Success(false);
                    }

                    db.Set<DeadlineAlert>().Add(DeadlineAlert.Record(alert.Document, alert.AgencyId, alert.Level, now, managers.Count));
                    foreach (var manager in managers)
                    {
                        await emailQueue.EnqueueAsync(emails.Create(alert.Notice, manager), ct);
                    }

                    return Result.Success(true);
                },
                cancellationToken: cancellationToken);
            return outcome is { IsSuccess: true, Value: true };
        }
        catch (DbUpdateException exception)
        {
            // The unique (document, level) index: a concurrent run sent this alert first, and nothing was committed here.
            LogAlreadySent(exception, alert.Document.Id, alert.Level);
            return false;
        }
    }

    /// <summary>The level to send today for each current document of the vehicles that are not sold.</summary>
    internal async Task<IReadOnlyList<DueAlert>> DueAlertsAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var horizon = today.AddDays(30);
        var vehicles = await db.Set<Vehicle>()
            .AsNoTracking()
            .Include(v => v.Documents)
            .Where(v => v.Status != VehicleStatus.Sold && v.Documents.Any(d => d.IsCurrent && d.ExpiresOn != null && d.ExpiresOn <= horizon))
            .ToListAsync(cancellationToken);
        var documents = vehicles
            .SelectMany(v => v.CurrentDocuments.Where(d => d.ExpiresOn <= horizon).Select(d => (Vehicle: v, Document: d)))
            .ToList();
        if (documents.Count == 0)
        {
            return [];
        }

        var documentIds = documents.Select(d => d.Document.Id).ToList();
        var alreadySent = (await db.Set<DeadlineAlert>()
                .AsNoTracking()
                .Where(a => documentIds.Contains(a.DocumentId))
                .Select(a => new { a.DocumentId, a.Level })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.DocumentId, a => a.Level);

        var due = new List<DueAlert>();
        foreach (var (vehicle, document) in documents.OrderBy(d => d.Document.ExpiresOn).ThenBy(d => d.Vehicle.PlateNumber, StringComparer.Ordinal))
        {
            var expiresOn = document.ExpiresOn!.Value;
            if (DeadlineAlert.LevelToSend(expiresOn, today, alreadySent[document.Id]) is not { } level)
            {
                continue;
            }

            var notice = new DeadlineAlertNotice(
                vehicle.Id,
                vehicle.PlateNumber,
                vehicle.Make,
                vehicle.Model,
                document.Type,
                document.Number,
                expiresOn,
                expiresOn.DayNumber - today.DayNumber,
                level);
            due.Add(new DueAlert(document, vehicle.AgencyId, level, notice));
        }

        return due;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} fleet deadline alerts are due but no active manager can receive them; they will be retried at the next run.")]
    private partial void LogNoRecipient(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} fleet deadline alerts sent to {Recipients} managers.")]
    private partial void LogSent(int count, int recipients);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deadline alert of document {DocumentId} at level {Level} already sent by a concurrent run.")]
    private partial void LogAlreadySent(Exception exception, Guid documentId, DeadlineAlertLevel level);

    internal sealed record DueAlert(VehicleDocument Document, Guid AgencyId, DeadlineAlertLevel Level, DeadlineAlertNotice Notice);
}
