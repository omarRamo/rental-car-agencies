using CarRental.Domain.Common;

namespace CarRental.Domain.Cash.Expenses;

// Events carry identifiers and discriminators only (no amount nor text).

public sealed record ExpenseRecorded(Guid ExpenseId, Guid AgencyId, ExpenseCategory Category, ExpensePaymentMethod PaymentMethod) : IDomainEvent;

public sealed record ExpenseChanged(Guid ExpenseId, Guid AgencyId) : IDomainEvent;

public sealed record ExpenseReceiptAttached(Guid ExpenseId, Guid AgencyId) : IDomainEvent;

public sealed record ExpenseDeleted(Guid ExpenseId, Guid AgencyId) : IDomainEvent;
