using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Customers.Documents;

/// <summary>
/// Retention of identity document images (SPEC §14 "Minimisation"): every night, removes the images kept for more than
/// <c>customers.document_image_retention_days</c> after the last closed rental of the person (or after their upload);
/// the files are deleted through the outbox. The document data (type, number, dates) is kept.
/// </summary>
/// <remarks>
/// Runs across agencies (the retention is a setting of the platform); each purge is recorded in the journal of the
/// agency of the persons concerned.
/// TODO(spec): disabled by default (<c>customers.document_image_retention_enabled</c> = false) because the legal
/// destruction procedure is not validated: article 45 of law 2004-63 requires a report by a bailiff in the presence of
/// an expert appointed by the INPDP. To be confirmed with the lawyer before enabling (points-ouverts.md).
/// A person who is a driver of an open rental file (Bookings contract) keeps their images.
/// </remarks>
internal sealed partial class DocumentImageRetentionJob(
    IAppDbContext db,
    ISettingsReader settings,
    IAuditTrail auditTrail,
    IBookingsQueries bookings,
    TimeProvider timeProvider,
    ILogger<DocumentImageRetentionJob> logger) : IRecurringJob
{
    public const int BatchSize = 100;
    private const int MaxBatchesPerRun = 20;

    public static string JobId => "customers.purge_document_images";

    /// <summary>Every night at 03:30, Tunis time.</summary>
    public static string CronExpression => "30 3 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!await settings.GetAsync(CustomerSettings.DocumentImageRetentionEnabled, cancellationToken: cancellationToken))
        {
            LogDisabled();
            return;
        }

        var days = await settings.GetAsync(CustomerSettings.DocumentImageRetentionDays, cancellationToken: cancellationToken);
        var now = timeProvider.GetUtcNow();
        var cutoff = now - TimeSpan.FromDays(days);
        var total = 0;
        var withOpenBookings = new List<Guid>();
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var persons = await db.Set<Person>()
                    .Where(p => p.AnonymizedAtUtc == null
                            && !withOpenBookings.Contains(p.Id)
                            && (p.LastRentalClosedAtUtc == null || p.LastRentalClosedAtUtc < cutoff)
                            && p.Documents.Any(d => (d.FrontImage != null && d.FrontImage.UploadedAtUtc < cutoff)
                                                    || (d.BackImage != null && d.BackImage.UploadedAtUtc < cutoff)))
                .OrderBy(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (persons.Count == 0)
            {
                break;
            }

            var purged = new Dictionary<Guid, int>();
            foreach (var person in persons)
            {
                if (await bookings.HasOpenBookingsAsync(person.Id, cancellationToken))
                {
                    withOpenBookings.Add(person.Id);
                    continue;
                }

                purged[person.AgencyId] = purged.GetValueOrDefault(person.AgencyId) + person.PurgeDocumentImages(cutoff, now);
            }

            if (purged.Values.Sum() == 0)
            {
                continue;
            }

            // The purge is recorded in the journal of each agency whose customers it concerns (ADR-0067).
            foreach (var (agencyId, count) in purged.Where(p => p.Value > 0))
            {
                total += count;
                await auditTrail.RecordAsync(
                    new AuditEvent(CustomerAuditActions.DocumentImagesPurged)
                    {
                        EntityType = nameof(IdentityDocument),
                        Details = string.Create(
                            CultureInfo.InvariantCulture,
                            $"{count} image(s) of {persons.Count(p => p.AgencyId == agencyId)} person(s) kept more than {days} days"),
                        AgencyId = agencyId,
                    },
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        LogPurged(total, days);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document image retention is disabled (customers.document_image_retention_enabled).")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} document image(s) purged after {Days} days of retention.")]
    private partial void LogPurged(int count, int days);
}
