using CarRental.Domain.Common;

namespace CarRental.Domain.Identity;

/// <summary>
/// Append-only trace of every access to a sensitive document (identity card, passport, driving licence images):
/// who viewed or downloaded what, when, from where. Required by invariant 9 (identity documents are never public
/// and each view is logged).
/// </summary>
public sealed class DocumentAccessLog : IAgencyOrPlatform
{
    public const int DocumentKindMaxLength = 64;
    public const int AccessMaxLength = 32;
    public const int ReasonMaxLength = 500;

    private DocumentAccessLog()
    {
    }

    public long Id { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public Guid? UserId { get; private set; }

    public string? UserName { get; private set; }

    public string? IpAddress { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>Identifier of the document (owned by the Customers module).</summary>
    public Guid DocumentId { get; private set; }

    /// <summary>Kind of document, e.g. <c>identity_card</c>, <c>passport</c>, <c>driving_licence</c>.</summary>
    public string DocumentKind { get; private set; } = null!;

    /// <summary>Kind of access: <c>view</c>, <c>download</c>, <c>temporary_link</c>.</summary>
    public string Access { get; private set; } = null!;

    public string? Reason { get; private set; }

    /// <summary>The agency whose customer the document belongs to (ADR-0067).</summary>
    public Guid? AgencyId { get; private set; }

    public static DocumentAccessLog Record(
        DateTimeOffset occurredAtUtc,
        AuditActor actor,
        Guid documentId,
        string documentKind,
        string access,
        string? reason = null,
        Guid? agencyId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(access);
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("The document id is required.", nameof(documentId));
        }

        return new DocumentAccessLog
        {
            AgencyId = agencyId == Guid.Empty ? null : agencyId,
            OccurredAtUtc = occurredAtUtc.ToUniversalTime(),
            UserId = actor.UserId,
            UserName = actor.UserName,
            IpAddress = actor.IpAddress,
            CorrelationId = actor.CorrelationId,
            DocumentId = documentId,
            DocumentKind = documentKind.Length > DocumentKindMaxLength ? documentKind[..DocumentKindMaxLength] : documentKind,
            Access = access.Length > AccessMaxLength ? access[..AccessMaxLength] : access,
            Reason = reason is { Length: > ReasonMaxLength } ? reason[..ReasonMaxLength] : reason,
        };
    }
}
