using CarRental.Application.Common.Security;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Billing.Invoicing;

/// <summary>
/// Runs a step of an outbox handler of the Billing module in a DI scope of its own (a fresh <c>DbContext</c>, apart from
/// the scope of the outbox message and of its other handlers), as the system actor <see cref="ActorName"/> with the
/// correlation id of the message, working for the agency of the document or the file only (ADR-0067).
/// </summary>
internal sealed class BillingScopes(IServiceScopeFactory scopeFactory)
{
    public const string ActorName = "system:billing";

    /// <summary>Runs <paramref name="step"/> for <paramref name="agencyId"/> (across agencies when <see langword="null"/>).</summary>
    public async Task<TResult> RunAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> step,
        Guid? agencyId,
        string? correlationId,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(step);
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(
            UserContextSnapshot.System(ActorName, correlationId, agencyId is { } agency && agency != Guid.Empty ? agency : null));
        return await step(scope.ServiceProvider.GetRequiredService<TService>(), cancellationToken);
    }
}

/// <summary>
/// A billing step that must be tried again later (a TTN transmission that failed, a lock held too long): thrown by an
/// outbox handler so that the outbox retries the message with its back-off and lists it when it keeps failing.
/// </summary>
public sealed class BillingRetryException : Exception
{
    public BillingRetryException()
    {
    }

    public BillingRetryException(string message)
        : base(message)
    {
    }

    public BillingRetryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
