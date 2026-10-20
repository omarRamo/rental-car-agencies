namespace CarRental.Domain.Common;

/// <summary>
/// Business data that is never physically deleted. Removing such an entity from the persistence context marks it
/// as deleted instead (the persistence layer sets both properties), and a global query filter hides it from queries.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTimeOffset? DeletedAtUtc { get; }
}
