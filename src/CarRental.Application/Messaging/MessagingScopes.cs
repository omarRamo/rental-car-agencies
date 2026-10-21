using CarRental.Application.Common.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging;

/// <summary>
/// Runs the steps of the WhatsApp pipeline (record a webhook event, run a bot turn, send a message, notify a file) in
/// DI scopes of their own, as the system actor <see cref="ActorName"/>. The outbox handlers delegate to it so that their
/// work never shares the context of the outbox message being processed (a failed unit of work clears its change
/// tracker), and so that one event of a webhook delivery is committed at a time.
/// </summary>
internal sealed partial class MessagingScopes(IServiceScopeFactory scopeFactory, ILogger<MessagingScopes> logger)
{
    public const string ActorName = "system:whatsapp";

    /// <summary>Attempts of a step replayed after a concurrent update or a duplicate insert.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs <paramref name="step"/> once in a new scope. For a step with an effect outside the database (a Graph API
    /// call): never replayed here.
    /// </summary>
    public async Task<TResult> RunOnceAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> step,
        string? correlationId,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(step);
        await using var scope = CreateScope(correlationId);
        return await step(scope.ServiceProvider.GetRequiredService<TService>(), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="step"/> in a new scope; when another writer changed the same rows meanwhile (row version,
    /// unique index), replays it in a fresh scope — the step reloads its data and sees what changed — up to
    /// <see cref="MaxAttempts"/> times. Database work only.
    /// </summary>
    public async Task<TResult> RunAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> step,
        string? correlationId,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(step);
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = CreateScope(correlationId);
            try
            {
                return await step(scope.ServiceProvider.GetRequiredService<TService>(), cancellationToken);
            }
            catch (DbUpdateException exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Not the exception itself: the message of a duplicate key names the key (a customer's number).
                LogReplayed(typeof(TService).Name, attempt, exception is DbUpdateConcurrencyException ? "row version" : "unique index or constraint");
            }
        }
    }

    private AsyncServiceScope CreateScope(string? correlationId)
    {
        var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName, correlationId));
        return scope;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp step {Step} met a concurrent change (attempt {Attempt}, {Conflict}); replayed on fresh data.")]
    private partial void LogReplayed(string step, int attempt, string conflict);
}
