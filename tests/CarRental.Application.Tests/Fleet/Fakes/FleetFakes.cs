using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Concurrency;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Fleet.Deadlines;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Fleet.Fakes;

/// <summary>Records the categories locked, in order; can be told to time out.</summary>
internal sealed class RecordingAvailabilityLock : IAvailabilityLock
{
    public List<Guid> Acquired { get; } = [];

    public Error? FailWith { get; set; }

    public Task<Result> AcquireAsync(Guid categoryId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (FailWith is { } error)
        {
            return Task.FromResult(Result.Failure(error));
        }

        Acquired.Add(categoryId);
        return Task.FromResult(Result.Success());
    }
}

internal sealed class RecordingAvailabilityNotifier : IAvailabilityChangeNotifier
{
    public List<Guid> Changed { get; } = [];

    public int AllChangedCount { get; private set; }

    public void CategoryChanged(Guid categoryId) => Changed.Add(categoryId);

    public void AllChanged() => AllChangedCount++;
}

internal sealed class InMemoryFileStorage : IFileStorage
{
    public Dictionary<(string Container, string Path), (byte[] Content, string ContentType)> Files { get; } = [];

    public async Task<StoredFile> UploadAsync(string container, string path, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Files[(container, path)] = (buffer.ToArray(), contentType);
        return new StoredFile(container, path, buffer.Length, contentType, "\"etag\"");
    }

    public Task<FileDownload?> OpenReadAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue((container, path), out var file)
            ? new FileDownload(new MemoryStream(file.Content), file.ContentType, file.Content.Length)
            : null);

    public Task<bool> ExistsAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.ContainsKey((container, path)));

    public Task<bool> DeleteAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.Remove((container, path)));

    public Task<Uri> GetTemporaryReadUriAsync(string container, string path, TimeSpan validity, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Uri($"https://storage.test/{container}/{path}"));
}

internal sealed class RecordingEmailQueue : IEmailQueue
{
    public List<EmailMessage> Messages { get; } = [];

    public Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class FakeIdentityQueries : IIdentityQueries
{
    public List<(UserContactInfo User, string Role)> Users { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(Users.Where(u => userIds.Contains(u.User.UserId)).ToDictionary(u => u.User.UserId, u => u.User.DisplayName));

    public Task<IReadOnlyList<UserContactInfo>> GetActiveUsersInRoleAsync(Guid agencyId, string role, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserContactInfo>>([.. Users.Where(u => u.Role == role).Select(u => u.User)]);
}

/// <summary>Always the default value of the setting.</summary>
internal sealed class DefaultSettingsReader : ISettingsReader
{
    public Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(definition.Default);
}

internal sealed class FakeReferentialQueries : IReferentialQueries
{
    public List<AgencyInfo> Agencies { get; } = [];

    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>(Agencies);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationInfo>>([]);

    public Task<LocationInfo?> FindLocationByCodeAsync(Guid agencyId, string code, CancellationToken cancellationToken = default) =>
        Task.FromResult<LocationInfo?>(null);
}

internal sealed class PlainDeadlineAlertEmails : IDeadlineAlertEmailFactory
{
    public EmailMessage Create(DeadlineAlertNotice notice, UserContactInfo recipient) =>
        new(recipient.Email, $"{notice.PlateNumber} {notice.DocumentType} {notice.Level}", $"<p>{notice.ExpiresOn:O}</p>") { ToName = recipient.DisplayName };
}
