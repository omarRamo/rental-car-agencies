namespace CarRental.Domain.Customers;

/// <summary>Formats accepted for identity document images (SPEC §5: JPG, PNG or PDF).</summary>
public enum DocumentFileFormat
{
    Jpeg = 1,
    Png = 2,
    Pdf = 3,
}

/// <summary>
/// Detects the real format of an uploaded file from its first bytes ("magic bytes"): the file name and the content type
/// sent by the browser are never trusted.
/// </summary>
public static class DocumentFileFormats
{
    /// <summary>Number of leading bytes needed by <see cref="Detect"/>.</summary>
    public const int SignatureLength = 8;

    public const string JpegContentType = "image/jpeg";
    public const string PngContentType = "image/png";
    public const string PdfContentType = "application/pdf";

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    /// <summary>The format of a file starting with <paramref name="header"/>, or <see langword="null"/> when not accepted.</summary>
    public static DocumentFileFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(JpegSignature))
        {
            return DocumentFileFormat.Jpeg;
        }

        if (header.StartsWith(PngSignature))
        {
            return DocumentFileFormat.Png;
        }

        return header.StartsWith(PdfSignature) ? DocumentFileFormat.Pdf : null;
    }

    public static string ContentTypeOf(DocumentFileFormat format) => format switch
    {
        DocumentFileFormat.Jpeg => JpegContentType,
        DocumentFileFormat.Png => PngContentType,
        DocumentFileFormat.Pdf => PdfContentType,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown document file format."),
    };

    public static string ExtensionOf(DocumentFileFormat format) => format switch
    {
        DocumentFileFormat.Jpeg => "jpg",
        DocumentFileFormat.Png => "png",
        DocumentFileFormat.Pdf => "pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown document file format."),
    };

    /// <summary>True for the content types of the accepted formats.</summary>
    public static bool IsAccepted(string? contentType) =>
        contentType is JpegContentType or PngContentType or PdfContentType;
}
