using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

// Events carry identifiers and discriminators only (no amount nor text).

public sealed record LedgerAccountChanged(Guid AccountId, string Number) : IDomainEvent;

public sealed record AccountMappingChanged(Guid MappingId, string Key) : IDomainEvent;

/// <summary>A month of an agency was closed: its journals are frozen in <see cref="ClosingId"/>.</summary>
public sealed record AccountingPeriodClosed(Guid PeriodId, Guid AgencyId, int Year, int Month, Guid ClosingId) : IDomainEvent;

/// <summary>A manager reopened a closed month (the reason is on the period and in the audit log).</summary>
public sealed record AccountingPeriodReopened(Guid PeriodId, Guid AgencyId, int Year, int Month, Guid? PreviousClosingId) : IDomainEvent;

public sealed record ReconciliationDifferenceJustified(Guid JustificationId, Guid AgencyId, int Year, int Month, ReconciliationArea Area) : IDomainEvent;

public sealed record MerchantStatementRecorded(Guid StatementId, Guid AgencyId, int LineCount) : IDomainEvent;
