namespace CarRental.Application.Identity.Contracts;

/// <summary>Public read API of the Identity module for other modules (e.g. author names in a booking timeline).</summary>
public interface IIdentityQueries
{
    /// <summary>Display names of the given users; unknown ids are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);
}
