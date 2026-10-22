namespace CarRental.Infrastructure.Persistence.Interceptors;

/// <summary>
/// A save that would cross the boundary between agencies (ADR-0067): always a bug or a forged request, never a business
/// error. The message names the entity type only, never a row or a value.
/// </summary>
public sealed class AgencyBoundaryException : InvalidOperationException
{
    public AgencyBoundaryException()
    {
    }

    public AgencyBoundaryException(string message)
        : base(message)
    {
    }

    public AgencyBoundaryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal static AgencyBoundaryException OtherAgency(Type entityType) =>
        new($"A {entityType.Name} of another agency (or of the platform) cannot be written by an operation that works for one agency.");

    internal static AgencyBoundaryException Moved(Type entityType) =>
        new($"A {entityType.Name} cannot move to another agency.");

    internal static AgencyBoundaryException Missing(Type entityType) =>
        new($"A {entityType.Name} must belong to an agency.");
}
