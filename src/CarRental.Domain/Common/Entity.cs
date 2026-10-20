namespace CarRental.Domain.Common;

/// <summary>
/// Base class of every entity: identity-based equality on a client-generated <see cref="Guid"/> (version 7, time-ordered).
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An entity id cannot be empty.", nameof(id));
        }

        Id = id;
    }

    /// <summary>Used by EF Core when materializing; the id is then set from the database.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; private init; }

    public static bool operator ==(Entity? left, Entity? right) => Equals(left, right);

    public static bool operator !=(Entity? left, Entity? right) => !Equals(left, right);

    /// <summary>New identifier for an entity: <see cref="Guid.CreateVersion7()"/> (sortable by creation time).</summary>
    public static Guid NewId() => Guid.CreateVersion7();

    public bool Equals(Entity? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other.GetType() == GetType() && Id != Guid.Empty && Id == other.Id;
    }

    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
