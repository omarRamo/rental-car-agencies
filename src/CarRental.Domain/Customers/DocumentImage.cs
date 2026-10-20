using System.ComponentModel.DataAnnotations.Schema;
using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// Reference to the image (or PDF) of one side of an identity document, stored in the private <c>documents</c> blob
/// container. The path only contains identifiers, never personal data; the content type is the detected one.
/// </summary>
// [ComplexType] (BCL annotation): a value stored in the columns of its owner, never a table of its own.
[ComplexType]
public sealed record DocumentImage
{
    /// <summary>10 MB: the browser compresses photos to about 1600 px, so real images are far smaller.</summary>
    public const long MaxLength = 10 * 1024 * 1024;

    public const int PathMaxLength = 300;
    public const int ContentTypeMaxLength = 64;

    private DocumentImage(string path, string contentType, long length, DateTimeOffset uploadedAtUtc)
    {
        Path = path;
        ContentType = contentType;
        Length = length;
        UploadedAtUtc = uploadedAtUtc;
    }

    /// <summary>Path in the <c>documents</c> container, e.g. <c>persons/{personId}/{uploadId}.jpg</c> (identifiers only).</summary>
    public string Path { get; private init; }

    public string ContentType { get; private init; }

    /// <summary>Size in bytes.</summary>
    public long Length { get; private init; }

    /// <summary>Upload instant: the retention period of the image counts from it (or from the last closed rental).</summary>
    public DateTimeOffset UploadedAtUtc { get; private init; }

    public static Result<DocumentImage> Create(string path, string contentType, long length, DateTimeOffset uploadedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > PathMaxLength)
        {
            throw new ArgumentException($"A document image path has {PathMaxLength} characters at most.", nameof(path));
        }

        if (!DocumentFileFormats.IsAccepted(contentType))
        {
            return CustomersErrors.ImageUnsupportedType;
        }

        if (length <= 0)
        {
            return CustomersErrors.ImageEmpty;
        }

        if (length > MaxLength)
        {
            return CustomersErrors.ImageTooLarge;
        }

        return new DocumentImage(path, contentType, length, uploadedAtUtc.ToUniversalTime());
    }
}
