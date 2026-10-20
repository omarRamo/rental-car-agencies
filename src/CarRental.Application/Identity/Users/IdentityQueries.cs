using CarRental.Application.Common.Paging;
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

    public async Task<IReadOnlyList<UserContactInfo>> GetActiveUsersInRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var contacts = new List<UserContactInfo>();
        for (var page = new PageRequest(1, PageRequest.MaxPageSize); ; page = new PageRequest(page.Page + 1, PageRequest.MaxPageSize))
        {
            var users = await directory.ListAsync(search: null, page, cancellationToken);
            contacts.AddRange(users.Items
                .Where(u => !u.IsLockedOut && !string.IsNullOrWhiteSpace(u.Email) && u.Roles.Contains(role, StringComparer.Ordinal))
                .Select(u => new UserContactInfo(u.Id, u.DisplayName, u.Email)));
            if (page.Page >= users.TotalPages)
            {
                break;
            }
        }

        return [.. contacts.OrderBy(c => c.DisplayName, StringComparer.Ordinal)];
    }
}
