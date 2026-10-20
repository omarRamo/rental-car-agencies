using CarRental.Domain.Common;

namespace CarRental.Domain.Cash;

// Events carry identifiers and discriminators only (no amount nor text).

public sealed record CashSessionOpened(Guid SessionId, Guid AgencyId, Guid CashierId) : IDomainEvent;

public sealed record CashMovementRecorded(Guid SessionId, Guid MovementId, CashMovementKind Kind) : IDomainEvent;

/// <summary>The cashier counted and closed the session; <see cref="HasVariance"/> when a pocket did not match.</summary>
public sealed record CashSessionClosed(Guid SessionId, Guid AgencyId, Guid CashierId, bool HasVariance) : IDomainEvent;

public sealed record CashSessionLocked(Guid SessionId, Guid AgencyId, Guid CashierId) : IDomainEvent;
