using CarRental.Application.Common.Files;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Customers.Documents;

/// <summary>
/// Validation and storage of identity document images in the private <c>documents</c> container: size (10 MB) and real
/// format (JPEG, PNG, PDF, detected from the magic bytes; the browser's file name and content type are ignored). Each
/// upload gets a new path made of identifiers only, so a file is never overwritten while the database still points to it.
/// </summary>
internal sealed partial class DocumentImageStore(IFileStorage storage, TimeProvider timeProvider, ILogger<DocumentImageStore> logger)
{
    /// <summary>Every document image lives under this prefix of the <c>documents</c> container.</summary>
    public const string PathPrefix = "persons/";

    private const int BufferSize = 81_920;

    /// <summary>Reads and checks an uploaded file (never trusted): size, then format from its first bytes.</summary>
    public static async Task<Result<PreparedDocumentImage>> PrepareAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[BufferSize];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > DocumentImage.MaxLength)
            {
                return CustomersErrors.ImageTooLarge;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return CustomersErrors.ImageEmpty;
        }

        var bytes = buffer.ToArray();
        var format = DocumentFileFormats.Detect(bytes.AsSpan(0, Math.Min(bytes.Length, DocumentFileFormats.SignatureLength)));
        if (format is null)
        {
            return CustomersErrors.ImageUnsupportedType;
        }

        return new PreparedDocumentImage(bytes, format.Value);
    }

    /// <summary>Uploads a prepared image under a new path of the person and returns its reference.</summary>
    public async Task<DocumentImage> StoreAsync(Guid personId, PreparedDocumentImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var contentType = DocumentFileFormats.ContentTypeOf(image.Format);
        var path = $"{PathPrefix}{personId:N}/{Guid.CreateVersion7():N}.{DocumentFileFormats.ExtensionOf(image.Format)}";
        using var stream = new MemoryStream(image.Content, writable: false);
        var stored = await storage.UploadAsync(FileContainers.Documents, path, stream, contentType, cancellationToken);
        return DocumentImage.Create(path, contentType, stored.Length, timeProvider.GetUtcNow()).Value;
    }

    /// <summary>
    /// Uploads a prepared image sent by a visitor under a new staging path (<c>staged/{id}.{ext}</c>) and returns the
    /// staging record to add to the unit of work (ADR-0050).
    /// </summary>
    public async Task<StagedDocumentImage> StageAsync(PreparedDocumentImage image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var id = Guid.CreateVersion7();
        var contentType = DocumentFileFormats.ContentTypeOf(image.Format);
        var path = StagedDocumentImage.PathFor(id, image.Format);
        using var stream = new MemoryStream(image.Content, writable: false);
        var stored = await storage.UploadAsync(FileContainers.Documents, path, stream, contentType, cancellationToken);
        var reference = DocumentImage.Create(path, contentType, stored.Length, timeProvider.GetUtcNow()).Value;
        return StagedDocumentImage.Create(id, reference).Value;
    }

    /// <summary>
    /// Copies a staged image to a new path of the person (checked again on its content: the staging area is never
    /// trusted more than the browser). Fails when the staged file is gone.
    /// </summary>
    public async Task<Result<DocumentImage>> CopyStagedAsync(Guid personId, StagedDocumentImage staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        await using var download = await storage.OpenReadAsync(FileContainers.Documents, staged.Image.Path, cancellationToken);
        if (download is null)
        {
            return CustomersErrors.StagedImageNotFound;
        }

        var prepared = await PrepareAsync(download.Content, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        return await StoreAsync(personId, prepared.Value, cancellationToken);
    }

    /// <summary>Deletes a staged file (used or expired); a failure is only logged (the staging purge retries).</summary>
    public Task DeleteStagedAsync(string path) =>
        path.StartsWith(StagedDocumentImage.PathPrefix, StringComparison.Ordinal) ? DeleteUnreferencedAsync(path) : Task.CompletedTask;

    /// <summary>Deletes a file that the database does not reference (failed operation); a failure is only logged.</summary>
    public async Task DeleteUnreferencedAsync(string path)
    {
        try
        {
            await storage.DeleteAsync(FileContainers.Documents, path, CancellationToken.None);
        }
#pragma warning disable CA1031 // Best-effort cleanup after a failed operation: the original error must not be hidden.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogCleanupFailed(exception, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unreferenced document image {Path} could not be deleted.")]
    private partial void LogCleanupFailed(Exception exception, string path);
}

/// <summary>An uploaded file that passed the checks, kept in memory until it is stored.</summary>
internal sealed record PreparedDocumentImage(byte[] Content, DocumentFileFormat Format);
