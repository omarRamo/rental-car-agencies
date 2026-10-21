using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Common.Files;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// Where the files of the hand-overs live (private containers, ADR-0041): photos, thumbnails and signatures in
/// <see cref="FileContainers.Inspections"/>, contracts in <see cref="FileContainers.Contracts"/>; and the back office
/// routes that stream them (authorized endpoints: the containers are never public).
/// </summary>
public static class HandoverMedia
{
    /// <summary>Largest photo accepted (the browser reduces it to about 1 600 px first).</summary>
    public const int MaxPhotoLength = 10 * 1024 * 1024;

    /// <summary>Largest thumbnail or signature accepted.</summary>
    public const int MaxSmallImageLength = 1024 * 1024;

    public const string Prefix = "/admin/handovers/media";

    public const string PhotoRoute = Prefix + "/photos/{photoId:guid}";

    public const string SignatureRoute = Prefix + "/signatures/{signatureId:guid}";

    public const string ContractRoute = Prefix + "/contracts/{contractId:guid}";

    public static string PhotoUrl(Guid photoId, bool thumbnail = false) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}/photos/{photoId:D}{(thumbnail ? "?size=thumb" : string.Empty)}");

    public static string SignatureUrl(Guid signatureId) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}/signatures/{signatureId:D}");

    public static string ContractUrl(Guid contractId) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}/contracts/{contractId:D}");

    internal static string PhotoPath(Guid bookingId, Guid inspectionId, Guid photoId, string contentType, bool thumbnail) =>
        string.Create(CultureInfo.InvariantCulture, $"bookings/{bookingId:D}/{inspectionId:D}/{photoId:D}{(thumbnail ? "-thumb" : string.Empty)}.{ExtensionOf(contentType)}");

    internal static string SignaturePath(Guid bookingId, Guid inspectionId, Guid signatureId) =>
        string.Create(CultureInfo.InvariantCulture, $"bookings/{bookingId:D}/{inspectionId:D}/signatures/{signatureId:D}.png");

    /// <summary>One path per generation (a replayed or failed attempt never overwrites a stored contract).</summary>
    internal static string ContractPath(Guid bookingId, string number, int version, Guid attemptId) =>
        string.Create(CultureInfo.InvariantCulture, $"bookings/{bookingId:D}/{number}-v{version}-{attemptId:N}.pdf");

    internal static string ExtensionOf(string contentType) => contentType switch
    {
        HandoverFile.Png => "png",
        HandoverFile.WebP => "webp",
        HandoverFile.Pdf => "pdf",
        _ => "jpg",
    };
}

/// <summary>
/// An uploaded image read in memory and checked: real type recognized from its signature ("magic bytes": JPEG, PNG or
/// WebP — the declared type and name are ignored), size limit, SHA-256 of the content. Same rule as the fleet and the
/// customer documents, kept in this module (modules share no internals).
/// </summary>
internal sealed class HandoverImage
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private HandoverImage(byte[] content, string contentType)
    {
        Content = content;
        ContentType = contentType;
        ContentHash = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public byte[] Content { get; }

    public string ContentType { get; }

    public string ContentHash { get; }

    public static async Task<Result<HandoverImage>> ReadAsync(Stream content, int maxLength, bool pngOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxLength)
            {
                return HandoverErrors.ImageTooLarge;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return HandoverErrors.ImageEmpty;
        }

        var bytes = buffer.ToArray();
        var type = Detect(bytes);
        if (type is null || (pngOnly && type != HandoverFile.Png))
        {
            return HandoverErrors.ImageUnsupported;
        }

        return new HandoverImage(bytes, type);
    }

    /// <summary>The content type of a JPEG, PNG or WebP signature, or <see langword="null"/>.</summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return HandoverFile.Jpeg;
        }

        if (header.StartsWith(PngSignature))
        {
            return HandoverFile.Png;
        }

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return HandoverFile.WebP;
        }

        return null;
    }

    public HandoverFile ToFile(string path) => HandoverFile.Create(path, ContentType, ContentHash, Content.Length);
}
