namespace CarRental.Application.Common.Files;

/// <summary>
/// Private file storage (Azure Blob Storage; Azurite locally). Containers are never public: files are served by an
/// authorized endpoint or through a short-lived read link (5 minutes) — and identity documents are logged on each access.
/// </summary>
public interface IFileStorage
{
    /// <summary>Uploads (or overwrites) a file. <paramref name="path"/> is relative to the container, e.g. <c>customers/{id}/passport-front.jpg</c>.</summary>
    Task<StoredFile> UploadAsync(
        string container,
        string path,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a file for reading, or returns <see langword="null"/> when it does not exist.</summary>
    Task<FileDownload?> OpenReadAsync(string container, string path, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string container, string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file; returns <see langword="false"/> when it did not exist.</summary>
    Task<bool> DeleteAsync(string container, string path, CancellationToken cancellationToken = default);

    /// <summary>A read-only link valid for <paramref name="validity"/> (at most one hour), e.g. 5 minutes for a document preview.</summary>
    Task<Uri> GetTemporaryReadUriAsync(
        string container,
        string path,
        TimeSpan validity,
        CancellationToken cancellationToken = default);
}

/// <summary>Metadata of a stored file.</summary>
public sealed record StoredFile(string Container, string Path, long Length, string ContentType, string? ETag);

/// <summary>An open file; dispose it to release the underlying stream.</summary>
public sealed class FileDownload(Stream content, string contentType, long length) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;

    public string ContentType { get; } = contentType;

    public long Length { get; } = length;

    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public void Dispose() => Content.Dispose();
}
