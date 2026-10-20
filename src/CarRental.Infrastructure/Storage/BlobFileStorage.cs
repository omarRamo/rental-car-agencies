using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using CarRental.Application.Common.Files;

namespace CarRental.Infrastructure.Storage;

/// <summary>
/// <see cref="IFileStorage"/> on Azure Blob Storage. Containers are created private. Temporary links are user delegation
/// SAS (managed identity, Azure) or, only when the client holds an account key (Azurite), service SAS.
/// </summary>
internal sealed partial class BlobFileStorage(BlobServiceClient serviceClient, TimeProvider timeProvider) : IFileStorage
{
    private static readonly TimeSpan MaxLinkValidity = TimeSpan.FromHours(1);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _ensuredContainers = new(StringComparer.Ordinal);

    public async Task<StoredFile> UploadAsync(
        string container,
        string path,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var blob = Blob(container, path);
        await EnsureContainerAsync(container, cancellationToken);

        var uploaded = await blob.UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        return new StoredFile(container, path, properties.Value.ContentLength, contentType, uploaded.Value.ETag.ToString());
    }

    public async Task<FileDownload?> OpenReadAsync(string container, string path, CancellationToken cancellationToken = default)
    {
        var blob = Blob(container, path);
        try
        {
            var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            var details = download.Value.Details;
            return new FileDownload(download.Value.Content, details.ContentType ?? "application/octet-stream", details.ContentLength);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string container, string path, CancellationToken cancellationToken = default) =>
        (await Blob(container, path).ExistsAsync(cancellationToken)).Value;

    public async Task<bool> DeleteAsync(string container, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await Blob(container, path).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return false;
        }
    }

    public async Task<Uri> GetTemporaryReadUriAsync(
        string container,
        string path,
        TimeSpan validity,
        CancellationToken cancellationToken = default)
    {
        if (validity <= TimeSpan.Zero || validity > MaxLinkValidity)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), validity, "A temporary link is valid for one hour at most.");
        }

        var blob = Blob(container, path);
        var now = timeProvider.GetUtcNow();
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, now + validity)
        {
            BlobContainerName = container,
            BlobName = path,
            Resource = "b",
            StartsOn = now - ClockSkew,
            Protocol = string.Equals(blob.Uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? SasProtocol.Https
                : SasProtocol.HttpsAndHttp,
        };

        if (blob.CanGenerateSasUri)
        {
            // Account key available: Azurite in development and tests only (shared keys are disabled in Azure).
            return blob.GenerateSasUri(sas);
        }

        var key = await serviceClient.GetUserDelegationKeyAsync(now - ClockSkew, now + validity, cancellationToken);
        return new BlobUriBuilder(blob.Uri)
        {
            Sas = sas.ToSasQueryParameters(key.Value, serviceClient.AccountName),
        }.ToUri();
    }

    internal static void ValidateContainer(string container)
    {
        if (string.IsNullOrEmpty(container) || !ContainerPattern().IsMatch(container) || container.Contains("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{container}' is not a valid container name.", nameof(container));
        }
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 1024
            || path.StartsWith('/')
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"'{path}' is not a valid file path.", nameof(path));
        }
    }

    private BlobClient Blob(string container, string path)
    {
        ValidateContainer(container);
        ValidatePath(path);
        return serviceClient.GetBlobContainerClient(container).GetBlobClient(path);
    }

    private Task EnsureContainerAsync(string container, CancellationToken cancellationToken)
    {
        var lazy = _ensuredContainers.GetOrAdd(
            container,
            name => new Lazy<Task>(() => serviceClient.GetBlobContainerClient(name).CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken)));
        var task = lazy.Value;
        if (task.IsFaulted || task.IsCanceled)
        {
            _ensuredContainers.TryRemove(container, out _);
        }

        return task;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerPattern();
}
