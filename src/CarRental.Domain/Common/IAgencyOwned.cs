namespace CarRental.Domain.Common;

/// <summary>
/// Data of one agency (the tenant, ADR-0067): an agency never reads nor changes the rows of another one. The persistence
/// layer hides the rows of the other agencies (named query filter <c>Agency</c>) and refuses to save them while an
/// operation works for one agency.
/// </summary>
/// <remarks>
/// <see cref="AgencyId"/> is set at creation and never changes: an aggregate gives it to the child entities it creates.
/// </remarks>
public interface IAgencyOwned
{
    Guid AgencyId { get; }
}

/// <summary>
/// Data that is either shared by the whole platform (<see cref="AgencyId"/> is <see langword="null"/>) or specific to one
/// agency (e.g. a setting: platform value and agency values). An agency reads the shared rows and its own ones only.
/// </summary>
public interface IAgencyShared
{
    Guid? AgencyId { get; }
}

/// <summary>
/// A record of one agency or of the platform itself (<see cref="AgencyId"/> is <see langword="null"/>), e.g. an audit
/// entry: an agency reads its own rows only, the platform rows are read across agencies only (support, system).
/// </summary>
public interface IAgencyOrPlatform
{
    Guid? AgencyId { get; }
}
