namespace CarRental.Domain.Common;

/// <summary>
/// Marker for domain events: immutable records raised by aggregates when something meaningful happened.
/// They are persisted to the outbox in the same transaction as the change, then dispatched asynchronously.
/// </summary>
/// <remarks>
/// The CLR full name is the outbox message type: renaming an event type orphans its pending messages.
/// Payloads must be serializable with System.Text.Json (primitives, ids, value objects with converters).
/// </remarks>
#pragma warning disable CA1040 // Marker interface: used for type discovery and generic constraints.
public interface IDomainEvent;
#pragma warning restore CA1040
