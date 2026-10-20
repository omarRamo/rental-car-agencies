using CarRental.Application.Identity.Contracts;

namespace CarRental.Application.Identity.Users;

internal sealed class IdentityQueries(IUserDirectory directory) : IIdentityQueries
{
    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        return userIds.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>())
            : directory.GetDisplayNamesAsync(userIds, cancellationToken);
    }
}
