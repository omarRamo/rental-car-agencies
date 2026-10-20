using System.Security.Cryptography;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Fleet.Photos;

/// <summary>
/// An uploaded photo read in memory and checked: at most 10 MB, real type recognized from the file signature
/// ("magic bytes": JPEG, PNG or WebP — the declared type and file name are ignored), SHA-256 of the content.
/// </summary>
internal sealed class PhotoFile
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private PhotoFile(byte[] content, string contentType)
    {
        Content = content;
        ContentType = contentType;
        ContentHash = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public byte[] Content { get; }

    public string ContentType { get; }

    /// <summary>Lower-case hexadecimal SHA-256.</summary>
    public string ContentHash { get; }

    public static async Task<Result<PhotoFile>> ReadAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > StoredPhoto.MaxLength)
            {
                return FleetErrors.PhotoTooLarge;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return FleetErrors.PhotoEmpty;
        }

        var bytes = buffer.ToArray();
        return Detect(bytes) is { } contentType ? new PhotoFile(bytes, contentType) : FleetErrors.PhotoUnsupportedFormat;
    }

    /// <summary>The content type of a JPEG, PNG or WebP signature, or <see langword="null"/>.</summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return StoredPhoto.Jpeg;
        }

        if (header.StartsWith(PngSignature))
        {
            return StoredPhoto.Png;
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return StoredPhoto.WebP;
        }

        return null;
    }

    public StoredPhoto ToStoredPhoto(string path) => StoredPhoto.Create(path, ContentType, ContentHash, Content.Length).Value;
}
