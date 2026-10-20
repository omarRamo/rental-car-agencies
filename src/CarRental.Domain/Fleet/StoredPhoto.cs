using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// A photo stored in the private blob container of the fleet: its path in the container, real content type (checked from
/// the file signature), SHA-256 of the content (hexadecimal, used as entity tag) and size.
/// </summary>
public sealed record StoredPhoto
{
    public const int PathMaxLength = 300;
    public const int ContentTypeMaxLength = 32;
    public const int ContentHashLength = 64;

    /// <summary>10 MB.</summary>
    public const long MaxLength = 10L * 1024 * 1024;

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    private StoredPhoto(string path, string contentType, string contentHash, long length)
    {
        Path = path;
        ContentType = contentType;
        ContentHash = contentHash;
        Length = length;
    }

    public string Path { get; }

    public string ContentType { get; }

    public string ContentHash { get; }

    public long Length { get; }

    /// <summary>Short version tag derived from the content hash (cache busting of public URLs).</summary>
    public string Version => ContentHash[..16];

    /// <summary>
    /// Describes a stored photo. An invalid path or hash is a programming error (both are produced by the upload
    /// service); an unsupported type or size is a business error.
    /// </summary>
    public static Result<StoredPhoto> Create(string path, string contentType, string contentHash, long length)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > PathMaxLength)
        {
            throw new ArgumentException($"A photo path has 1 to {PathMaxLength} characters.", nameof(path));
        }

        if (contentType is not (Jpeg or Png or WebP))
        {
            return FleetErrors.PhotoUnsupportedFormat;
        }

        if (length <= 0)
        {
            return FleetErrors.PhotoEmpty;
        }

        if (length > MaxLength)
        {
            return FleetErrors.PhotoTooLarge;
        }

        if (contentHash is null || contentHash.Length != ContentHashLength || !contentHash.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("The content hash must be a lower-case hexadecimal SHA-256.", nameof(contentHash));
        }

        return new StoredPhoto(path, contentType, contentHash, length);
    }

    /// <summary>File extension of the content type (without dot).</summary>
    public static string ExtensionOf(string contentType) => contentType switch
    {
        Jpeg => "jpg",
        Png => "png",
        WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "Unsupported photo content type."),
    };
}
