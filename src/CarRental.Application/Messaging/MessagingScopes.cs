using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging;

/// <summary>
/// Runs the steps of the WhatsApp pipeline (record a webhook event, run a bot turn, send a message, notify a file) in
/// DI scopes of their own, as the system actor <see cref="ActorName"/>: working for the agency of the conversation when
/// the step concerns one (ADR-0067, the step then sees and writes the data of that agency only), across agencies
/// otherwise. The outbox handlers delegate to it so that their work never shares the context of the outbox message being
/// processed (a failed unit of work clears its change tracker), and so that one event of a webhook delivery is committed
/// at a time.
/// </summary>
internal sealed partial class MessagingScopes(IServiceScopeFactory scopeFactory, WhatsAppNumber number, ILogger<MessagingScopes> logger)
{
    public const string ActorName = "system:whatsapp";

    /// <summary>Attempts of a step replayed after a concurrent update or a duplicate insert.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs <paramref name="step"/> once in a new scope (for <paramref name="agencyId"/> only when given). For a step with
    /// an effect outside the database (a Graph API call): never replayed here.
    /// </summary>
    public async Task<TResult> RunOnceAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> step,
        string? correlationId,
        CancellationToken cancellationToken,
        Guid? agencyId = null)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(step);
        await using var scope = CreateScope(correlationId, agencyId);
        return await step(scope.ServiceProvider.GetRequiredService<TService>(), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="step"/> in a new scope (for <paramref name="agencyId"/> only when given); when another writer
    /// changed the same rows meanwhile (row version, unique index), replays it in a fresh scope — the step reloads its
    /// data and sees what changed — up to <see cref="MaxAttempts"/> times. Database work only.
    /// </summary>
    public async Task<TResult> RunAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> step,
        string? correlationId,
        CancellationToken cancellationToken,
        Guid? agencyId = null)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(step);
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = CreateScope(correlationId, agencyId);
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

    /// <summary>A new scope acting as <see cref="ActorName"/>, for <paramref name="agencyId"/> only when given (the caller disposes it).</summary>
    public AsyncServiceScope CreateScope(string? correlationId, Guid? agencyId = null)
    {
        var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName, correlationId, agencyId));
        return scope;
    }

    /// <summary>The agency of a conversation (read across agencies), or <see langword="null"/> when it no longer exists.</summary>
    public async Task<Guid?> AgencyOfConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var scope = CreateScope(correlationId: null);
        return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Conversation>()
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => (Guid?)c.AgencyId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The agency of a message (read across agencies), or <see langword="null"/> when it no longer exists.</summary>
    public async Task<Guid?> AgencyOfMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var scope = CreateScope(correlationId: null);
        return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Message>()
            .AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => (Guid?)m.AgencyId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Whether WhatsApp is open for the agency — the platform's number serves it (active, module on, and the routing of
    /// the inbound messages resolves to it): the work of the module skips it otherwise.
    /// </summary>
    public Task<bool> IsOpenAsync(Guid agencyId, CancellationToken cancellationToken) => number.IsOpenAsync(agencyId, cancellationToken);

    /// <summary>The agencies WhatsApp is open for: the one the platform's number serves, if any.</summary>
    public Task<IReadOnlyList<Guid>> OpenAgenciesAsync(CancellationToken cancellationToken) => number.OpenAgenciesAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp step {Step} met a concurrent change (attempt {Attempt}, {Conflict}); replayed on fresh data.")]
    private partial void LogReplayed(string step, int attempt, string conflict);
}
