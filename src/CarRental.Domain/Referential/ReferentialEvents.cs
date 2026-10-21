using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

public sealed record AgencyRegistered(Guid AgencyId, string Name) : IDomainEvent;

public sealed record AgencyDetailsUpdated(Guid AgencyId) : IDomainEvent;

/// <summary>The legal identity printed on the invoices of the agency changed.</summary>
public sealed record AgencyLegalIdentityUpdated(Guid AgencyId) : IDomainEvent;

public sealed record LocationAdded(Guid LocationId, Guid AgencyId, string Code) : IDomainEvent;

public sealed record SettingChanged(Guid SettingId, string Key, Guid? AgencyId, string? PreviousValue, string NewValue) : IDomainEvent;

/// <summary>The value of an agency was taken back: the platform value applies again.</summary>
public sealed record SettingRemoved(Guid SettingId, string Key, Guid? AgencyId, string PreviousValue) : IDomainEvent;
