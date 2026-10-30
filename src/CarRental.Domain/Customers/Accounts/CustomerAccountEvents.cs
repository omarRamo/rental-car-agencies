using CarRental.Domain.Common;

namespace CarRental.Domain.Customers.Accounts;

// Events carry identifiers only: they are stored in the outbox, which is not masked like the audit log.

public sealed record CustomerAccountRegistered(Guid AccountId, Guid AgencyId) : IDomainEvent;

public sealed record CustomerAccountEmailVerified(Guid AccountId) : IDomainEvent;

public sealed record CustomerAccountPasswordChanged(Guid AccountId) : IDomainEvent;

public sealed record CustomerAccountProfileUpdated(Guid AccountId) : IDomainEvent;

public sealed record CustomerAccountClosed(Guid AccountId) : IDomainEvent;
