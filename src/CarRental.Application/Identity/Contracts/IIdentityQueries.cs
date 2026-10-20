namespace CarRental.Application.Identity.Contracts;

/// <summary>Public read API of the Identity module for other modules (e.g. author names in a booking timeline).</summary>
public interface IIdentityQueries
{
    /// <summary>Display names of the given users; unknown ids are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Users of <paramref name="role"/> (see <see cref="Roles"/>) who can sign in (not locked out) and have an e-mail
    /// address, ordered by display name: e.g. the managers who receive the fleet deadline alerts.
    /// </summary>
    Task<IReadOnlyList<UserContactInfo>> GetActiveUsersInRoleAsync(string role, CancellationToken cancellationToken = default);
}

/// <summary>A user to notify.</summary>
public sealed record UserContactInfo(Guid UserId, string DisplayName, string Email);
