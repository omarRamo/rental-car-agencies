using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// A consent of a person for one purpose: when and through which channel it was given, and withdrawn. A consent given
/// again after a withdrawal is a new row, so the history proves what was allowed at any date. Part of <see cref="Person"/>.
/// </summary>
public sealed class Consent : Entity, IAgencyOwned
{
    private Consent()
    {
    }

    private Consent(Guid id, Guid personId, Guid agencyId, ConsentPurpose purpose, ConsentChannel channel, DateTimeOffset givenAtUtc)
        : base(id)
    {
        PersonId = personId;
        AgencyId = agencyId;
        Purpose = purpose;
        GivenThrough = channel;
        GivenAtUtc = givenAtUtc;
    }

    public Guid PersonId { get; private set; }

    /// <summary>The agency of the person (given by the person at creation).</summary>
    public Guid AgencyId { get; private set; }

    public ConsentPurpose Purpose { get; private set; }

    public ConsentChannel GivenThrough { get; private set; }

    public DateTimeOffset GivenAtUtc { get; private set; }

    public ConsentChannel? WithdrawnThrough { get; private set; }

    public DateTimeOffset? WithdrawnAtUtc { get; private set; }

    public bool IsActive => WithdrawnAtUtc is null;

    internal static Consent Give(Guid personId, Guid agencyId, ConsentPurpose purpose, ConsentChannel channel, DateTimeOffset now) =>
        new(NewId(), personId, agencyId, purpose, channel, now);

    internal void Withdraw(ConsentChannel channel, DateTimeOffset now)
    {
        WithdrawnThrough = channel;
        WithdrawnAtUtc = now;
    }
}
