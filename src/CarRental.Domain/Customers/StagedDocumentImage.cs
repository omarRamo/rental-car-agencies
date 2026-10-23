using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// An identity document image sent by a visitor before any person exists (photo uploaded during the public booking
/// funnel or from "Ma réservation"): already checked (real format, size), stored in the private <c>documents</c>
/// container under <see cref="PathPrefix"/>, then copied to the person when the submission is registered
/// (<see cref="MarkConsumed"/>). Unconsumed images are deleted by the staging purge after a short delay. An image is
/// sent on the public site of one agency and only used for a driver of that agency (ADR-0067).
/// </summary>
public sealed class StagedDocumentImage : Entity, IAgencyOwned
{
    /// <summary>Every staged image lives under this prefix of the <c>documents</c> container (identifiers only).</summary>
    public const string PathPrefix = "staged/";

    private StagedDocumentImage()
    {
    }

    private StagedDocumentImage(Guid id, Guid agencyId, DocumentImage image)
        : base(id)
    {
        AgencyId = agencyId;
        Image = image;
    }

    /// <summary>The agency of the public site the image was sent on.</summary>
    public Guid AgencyId { get; private set; }

    /// <summary>The stored file (path made of the staging id, detected content type, size, upload instant).</summary>
    public DocumentImage Image { get; private set; } = null!;

    /// <summary>When the image was copied to a person or a submission; it cannot be used twice.</summary>
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public bool IsConsumed => ConsumedAtUtc is not null;

    /// <summary>The staging path of a new upload: <c>staged/{id}.{extension}</c>.</summary>
    public static string PathFor(Guid id, DocumentFileFormat format) => $"{PathPrefix}{id:N}.{DocumentFileFormats.ExtensionOf(format)}";

    public static Result<StagedDocumentImage> Create(Guid agencyId, Guid id, DocumentImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (id == Guid.Empty || agencyId == Guid.Empty)
        {
            throw new ArgumentException("A staged image needs an id and an agency.", nameof(id));
        }

        return image.Path.StartsWith(PathPrefix, StringComparison.Ordinal)
            ? new StagedDocumentImage(id, agencyId, image)
            : throw new ArgumentException($"A staged image lives under {PathPrefix}.", nameof(image));
    }

    /// <summary>Records that the image was copied to a person; a second use is refused.</summary>
    public Result MarkConsumed(DateTimeOffset now)
    {
        if (IsConsumed)
        {
            return CustomersErrors.StagedImageNotFound;
        }

        ConsumedAtUtc = now.ToUniversalTime();
        return Result.Success();
    }
}
