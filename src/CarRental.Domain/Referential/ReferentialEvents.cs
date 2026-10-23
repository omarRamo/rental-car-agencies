using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

public sealed record AgencyRegistered(Guid AgencyId, string Name, string Slug) : IDomainEvent;

/// <summary>The address of the agency's public site changed: links with the previous slug stop working.</summary>
public sealed record AgencySlugChanged(Guid AgencyId, string PreviousSlug, string Slug) : IDomainEvent;

/// <summary>The support switched modules of the agency on or off.</summary>
public sealed record AgencyModulesChanged(Guid AgencyId, AgencyModules Previous, AgencyModules Current) : IDomainEvent;

/// <summary>The support suspended the agency (<see cref="IsActive"/> false) or lifted the suspension.</summary>
public sealed record AgencyActivationChanged(Guid AgencyId, bool IsActive) : IDomainEvent;

public sealed record AgencyDetailsUpdated(Guid AgencyId) : IDomainEvent;

/// <summary>The legal identity printed on the invoices of the agency changed.</summary>
public sealed record AgencyLegalIdentityUpdated(Guid AgencyId) : IDomainEvent;

public sealed record LocationAdded(Guid LocationId, Guid AgencyId, string Code) : IDomainEvent;

public sealed record SettingChanged(Guid SettingId, string Key, Guid? AgencyId, string? PreviousValue, string NewValue) : IDomainEvent;

/// <summary>The value of an agency was taken back: the platform value applies again.</summary>
public sealed record SettingRemoved(Guid SettingId, string Key, Guid? AgencyId, string PreviousValue) : IDomainEvent;
