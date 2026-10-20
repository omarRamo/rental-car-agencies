using System.Globalization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Files;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Tests.Customers.Fakes;

/// <summary>Private file storage in memory; records the temporary links handed out.</summary>
internal sealed class FakeFileStorage(TimeProvider timeProvider) : IFileStorage
{
    public Dictionary<string, (byte[] Content, string ContentType)> Files { get; } = new(StringComparer.Ordinal);

    public List<(string Path, TimeSpan Validity)> Links { get; } = [];

    /// <summary>Makes every upload fail (storage outage).</summary>
    public bool FailUploads { get; set; }

    public async Task<StoredFile> UploadAsync(string container, string path, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (FailUploads)
        {
            throw new IOException("Simulated storage outage.");
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Files[Key(container, path)] = (buffer.ToArray(), contentType);
        return new StoredFile(container, path, buffer.Length, contentType, ETag: "etag");
    }

    public Task<FileDownload?> OpenReadAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue(Key(container, path), out var file)
            ? new FileDownload(new MemoryStream(file.Content, writable: false), file.ContentType, file.Content.Length)
            : null);

    public Task<bool> ExistsAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.ContainsKey(Key(container, path)));

    public Task<bool> DeleteAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.Remove(Key(container, path)));

    public Task<Uri> GetTemporaryReadUriAsync(string container, string path, TimeSpan validity, CancellationToken cancellationToken = default)
    {
        Links.Add((path, validity));
        var expiry = (timeProvider.GetUtcNow() + validity).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return Task.FromResult(new Uri($"https://storage.test/{container}/{path}?se={Uri.EscapeDataString(expiry)}&sig=test"));
    }

    public bool Holds(string path) => Files.ContainsKey(Key(FileContainers.Documents, path));

    private static string Key(string container, string path) => $"{container}/{path}";
}

internal sealed class RecordingDocumentAccessLog : IDocumentAccessLog
{
    public List<(Guid DocumentId, string Kind, string Access, string? Reason)> Entries { get; } = [];

    public Task RecordAsync(Guid documentId, string documentKind, string access, string? reason = null, CancellationToken cancellationToken = default)
    {
        Entries.Add((documentId, documentKind, access, reason));
        return Task.CompletedTask;
    }
}

/// <summary>Setting values: the definition default unless overridden.</summary>
internal sealed class FakeSettingsReader : ISettingsReader
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public List<Guid?> RequestedAgencies { get; } = [];

    public void Set<T>(SettingDefinition<T> definition, T value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _values[definition.Key] = value!;
    }

    public Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequestedAgencies.Add(agencyId);
        return Task.FromResult(_values.TryGetValue(definition.Key, out var value) ? (T)value : definition.Default);
    }
}

internal sealed class FakeIdentityQueries : IIdentityQueries
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<IReadOnlyList<UserContactInfo>> GetActiveUsersInRoleAsync(string role, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserContactInfo>>([]);

    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(Names.Where(n => userIds.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value));
}
