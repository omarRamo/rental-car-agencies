using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Privacy;

/// <summary>
/// Data subject rights (law 2004-63, GDPR-compatible design, SPEC Â§14): export of the data held about a person and
/// anonymization. Manager only (permission <c>customers.privacy</c>); both are written to the audit log (ADR-0027) of
/// the agency of the person, the only one that holds that data (ADR-0067).
/// </summary>
public sealed class PersonalDataService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IAuditTrail auditTrail,
    IUserContext userContext,
    TimeProvider timeProvider)
{
    public const int ReasonMaxLength = 500;
    private const string PersonEntityType = nameof(Person);

    private static readonly JsonSerializerOptions ExportOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The data held about the person as a JSON document (identity, contact, documents without images, consents, payer
    /// accounts). The blacklist is an internal record, included only on request: TODO(spec) whether the right of access
    /// covers it (art. 32 of law 2004-63) is to be confirmed with the lawyer.
    /// </summary>
    public async Task<Result<PersonalDataExportFile>> ExportAsync(
        Guid personId,
        bool includeInternalRecords = false,
        CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersPrivacy))
        {
            return CustomersErrors.Forbidden;
        }

        var person = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (person is null)
        {
            return CustomersErrors.PersonNotFound;
        }

        var payers = await db.Set<Customer>()
            .AsNoTracking()
            .Where(c => c.PersonId == personId)
            .Select(c => new { c.Id, c.Kind, c.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var submissions = await db.Set<DriverSubmission>()
            .AsNoTracking()
            .Where(s => s.PersonId == personId)
            .OrderBy(s => s.SubmittedAtUtc)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var export = new PersonalDataExport(
            Format: "carrental.personal-data/1",
            ExportedAtUtc: now,
            Person: new PersonSection(
                person.Id,
                person.FirstName,
                person.LastName,
                person.Nationality,
                person.BirthDate,
                person.Phone,
                person.Email,
                person.Address?.ToString(),
                person.CreatedAtUtc,
                person.AnonymizedAtUtc),
            IdentityDocuments: [.. person.Documents.Select(d => new DocumentSection(
                d.Id,
                d.Type.ToString(),
                d.Number,
                d.IssuingCountry,
                d.IssuedOn,
                d.ExpiresOn,
                d.Type == IdentityDocumentType.DrivingLicense ? d.IsLatinScript : null,
                d.FrontImage is not null,
                d.BackImage is not null,
                d.VerifiedAtUtc,
                d.SupersededAtUtc))],
            Consents: [.. person.Consents.Select(c => new ConsentSection(
                c.Purpose.ToString(),
                c.GivenThrough.ToString(),
                c.GivenAtUtc,
                c.WithdrawnThrough?.ToString(),
                c.WithdrawnAtUtc))],
            PayerAccounts: [.. payers.Select(c => new PayerSection(c.Id, c.Kind.ToString(), c.CreatedAtUtc))],
            OnlineSubmissions: [.. submissions.Select(s => new SubmissionSection(
                s.Id,
                s.BookingId,
                s.State.ToString(),
                s.SubmittedAtUtc,
                s.FirstName,
                s.LastName,
                s.Nationality,
                s.BirthDate,
                s.Phone,
                s.Email,
                [.. s.Documents.Select(d => new SubmittedDocumentSection(d.Type.ToString(), d.Number, d.IssuingCountry, d.IssuedOn, d.ExpiresOn, d.HasFrontImage, d.HasBackImage))]))],
            InternalRecords: includeInternalRecords
                ? [.. person.BlacklistEntries.Select(e => new BlacklistSection(e.Reason, e.AddedAtUtc, e.LiftedAtUtc, e.LiftReason))]
                : null,
            Notes: "Images of identity documents are not included in this file; a copy can be provided on request.");

        await auditTrail.RecordAsync(
            new AuditEvent(CustomerAuditActions.PersonExported)
            {
                EntityType = PersonEntityType,
                EntityId = personId.ToString(),
                Details = includeInternalRecords ? "with internal records" : null,
                AgencyId = person.AgencyId,
            },
            cancellationToken);

        var fileName = string.Create(CultureInfo.InvariantCulture, $"personal-data-{personId:N}-{TunisTime.DateOf(now):yyyyMMdd}.json");
        return new PersonalDataExportFile(fileName, "application/json", JsonSerializer.SerializeToUtf8Bytes(export, ExportOptions));
    }

    /// <summary>
    /// Anonymizes the person (names, birth date, nationality, contact, address scrubbed; documents, consents and blacklist
    /// history removed; images deleted after the commit) and keeps its identifier for bookings and accounting.
    /// <paramref name="reason"/> (e.g. "written request of 12/10/2027") goes to the audit log: no personal data in it.
    /// </summary>
    public async Task<Result> AnonymizeAsync(Guid personId, string reason, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.CustomersPrivacy))
        {
            return CustomersErrors.Forbidden;
        }

        var cleanReason = reason?.Trim();
        if (string.IsNullOrEmpty(cleanReason) || cleanReason.Length > ReasonMaxLength)
        {
            return CustomersErrors.PrivacyReasonRequired;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var person = await db.LoadPersonAsync(personId, ct);
                if (person is null)
                {
                    return CustomersErrors.PersonNotFound;
                }

                if (person.IsAnonymized)
                {
                    return Result.Success();
                }

                var anonymized = person.Anonymize(timeProvider.GetUtcNow());
                if (anonymized.IsFailure)
                {
                    return anonymized;
                }

                // What the person typed in public pages goes too (ADR-0050); pending images are deleted after the commit.
                var submissions = await db.Set<DriverSubmission>().Where(s => s.PersonId == personId).ToListAsync(ct);
                foreach (var submission in submissions)
                {
                    submission.DiscardForErasure();
                    db.Set<DriverSubmission>().Remove(submission);
                }

                await auditTrail.RecordAsync(
                    new AuditEvent(CustomerAuditActions.PersonAnonymized)
                    {
                        EntityType = PersonEntityType,
                        EntityId = personId.ToString(),
                        Details = cleanReason,
                        AgencyId = person.AgencyId,
                    },
                    ct);
                return Result.Success();
            },
            cancellationToken);
    }

    private sealed record PersonalDataExport(
        string Format,
        DateTimeOffset ExportedAtUtc,
        PersonSection Person,
        IReadOnlyList<DocumentSection> IdentityDocuments,
        IReadOnlyList<ConsentSection> Consents,
        IReadOnlyList<PayerSection> PayerAccounts,
        IReadOnlyList<SubmissionSection> OnlineSubmissions,
        IReadOnlyList<BlacklistSection>? InternalRecords,
        string Notes);

    private sealed record SubmissionSection(
        Guid Id,
        Guid BookingId,
        string State,
        DateTimeOffset SubmittedAtUtc,
        string? FirstName,
        string? LastName,
        string? Nationality,
        DateOnly? BirthDate,
        string? Phone,
        string? Email,
        IReadOnlyList<SubmittedDocumentSection> Documents);

    private sealed record SubmittedDocumentSection(
        string Type,
        string? Number,
        string? IssuingCountry,
        DateOnly? IssuedOn,
        DateOnly? ExpiresOn,
        bool HasFrontImage,
        bool HasBackImage);

    private sealed record PersonSection(
        Guid Id,
        string FirstName,
        string LastName,
        string? Nationality,
        DateOnly? BirthDate,
        string? Phone,
        string? Email,
        string? Address,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? AnonymizedAtUtc);

    private sealed record DocumentSection(
        Guid Id,
        string Type,
        string Number,
        string IssuingCountry,
        DateOnly? IssuedOn,
        DateOnly? ExpiresOn,
        bool? IsLatinScript,
        bool HasFrontImage,
        bool HasBackImage,
        DateTimeOffset? VerifiedAtUtc,
        DateTimeOffset? SupersededAtUtc);

    private sealed record ConsentSection(string Purpose, string GivenThrough, DateTimeOffset GivenAtUtc, string? WithdrawnThrough, DateTimeOffset? WithdrawnAtUtc);

    private sealed record PayerSection(Guid CustomerId, string Kind, DateTimeOffset CreatedAtUtc);

    private sealed record BlacklistSection(string Reason, DateTimeOffset AddedAtUtc, DateTimeOffset? LiftedAtUtc, string? LiftReason);
}

/// <summary>A file to download: the JSON export of a person.</summary>
public sealed record PersonalDataExportFile(string FileName, string ContentType, ReadOnlyMemory<byte> Content);
