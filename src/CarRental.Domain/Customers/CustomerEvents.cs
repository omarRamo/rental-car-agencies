using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

// Events carry identifiers only: they are stored in the outbox, which is not masked like the audit log.

public sealed record PersonRegistered(Guid PersonId) : IDomainEvent;

public sealed record PersonDetailsChanged(Guid PersonId) : IDomainEvent;

public sealed record IdentityDocumentAdded(Guid PersonId, Guid DocumentId, IdentityDocumentType Type) : IDomainEvent;

public sealed record IdentityDocumentVerified(Guid PersonId, Guid DocumentId) : IDomainEvent;

/// <summary>
/// Image files no longer referenced (replaced, document removed, retention purge, anonymization): the outbox handler
/// deletes them from the private container once the change is committed.
/// </summary>
public sealed record DocumentImagesDiscarded(Guid PersonId, Guid DocumentId, IReadOnlyList<string> Paths) : IDomainEvent;

public sealed record ConsentChanged(Guid PersonId, ConsentPurpose Purpose, bool IsGiven) : IDomainEvent;

public sealed record PersonBlacklisted(Guid PersonId) : IDomainEvent;

public sealed record PersonBlacklistLifted(Guid PersonId) : IDomainEvent;

public sealed record PersonAnonymized(Guid PersonId) : IDomainEvent;

public sealed record CustomerRegistered(Guid CustomerId, CustomerKind Kind) : IDomainEvent;

public sealed record CompanyCustomerUpdated(Guid CustomerId) : IDomainEvent;
