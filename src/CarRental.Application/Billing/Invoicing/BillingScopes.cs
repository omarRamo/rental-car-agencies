using CarRental.Application.Common.Security;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Billing.Invoicing;

/// <summary>
/// Runs a step of an outbox handler of the Billing module in a DI scope of its own (a fresh <c>DbContext</c>, apart from
/// the scope of the outbox message and of its other handlers), as the system actor <see cref="ActorName"/> with the
/// correlation id of the message.
/// </summary>
internal sealed class BillingScopes(IServiceScopeFactory scopeFactory)
{
    public const string ActorName = "system:billing";

    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, CancellationToken, Task<TResult>> step, string? correlationId, CancellationToken cancellationToken)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(step);
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName, correlationId));
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
