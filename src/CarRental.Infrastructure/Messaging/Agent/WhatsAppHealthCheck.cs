using System.Globalization;
using CarRental.Application.Common.Security;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging.Agent;

/// <summary>
/// Readiness entry <c>whatsapp</c> (ADR-0060, security review pass 2): Degraded — never Unhealthy, the application keeps
/// serving — while a customer message waits for the bot longer than its turn can take (<see cref="StaleAfter"/>). Since
/// the bot turns left the outbox (ADR-0054), the <c>outbox</c> entry no longer sees a stuck bot: the turn executor
/// stopped (no job server on the instance, pump down), a conversation lock never released, or turns failing without
/// their hand-over. The outbound messages still go through the outbox and its entry. Healthy again once the turns are
/// answered or handed over (the conversations wait in <c>/admin/whatsapp</c> meanwhile).
/// </summary>
/// <remarks>
/// Read across agencies in a scope of its own (the platform's entry, ADR-0067), for the agencies whose WhatsApp module
/// is on: the messages of the others wait for the module on purpose.
/// </remarks>
internal sealed class WhatsAppHealthCheck(IServiceScopeFactory scopes, IAgencyDirectory agencies, IOptions<BotTurnOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    internal const string ActorName = "system:health";

    /// <summary>The shortest wait reported, whatever the retry delays: a turn takes seconds, its retries minutes.</summary>
    public static readonly TimeSpan MinimumStaleAfter = TimeSpan.FromMinutes(20);

    /// <summary>Room for the model calls of the attempts of a turn (30 seconds each at most, a few per attempt).</summary>
    private static readonly TimeSpan ModelAllowance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a customer message may wait for the bot: the quiet period, every retry delay (after the last one the turn
    /// is handed over to an agent) and the model calls — at least <see cref="MinimumStaleAfter"/>.
    /// </summary>
    public static TimeSpan StaleAfter(BotTurnOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var retries = (options.ParsedRetryDelays() ?? []).Aggregate(TimeSpan.Zero, (total, delay) => total + delay);
        var expected = options.QuietPeriod + retries + ModelAllowance;
        return expected > MinimumStaleAfter ? expected : MinimumStaleAfter;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var staleAfter = StaleAfter(options.Value);
        var cutoff = now - staleAfter;
        var open = (await agencies.ListAsync(cancellationToken)).Where(a => a.IsActive && a.HasModule(AgencyModules.WhatsApp)).Select(a => a.Id).ToList();

        int stale;
        DateTimeOffset? oldest;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System(ActorName));
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();

            // Served by the filtered index of the queue (IX_Messages_PendingBotTurns).
            var pending = db.Set<Message>()
                .AsNoTracking()
                .Where(m => m.Direction == MessageDirection.Inbound && m.BotTurn == BotTurnState.Pending && open.Contains(m.AgencyId));
            stale = await pending.Where(m => m.CreatedAtUtc < cutoff).Select(m => m.ConversationId).Distinct().CountAsync(cancellationToken);
            oldest = await pending.MinAsync(m => (DateTimeOffset?)m.CreatedAtUtc, cancellationToken);
        }

        var data = new Dictionary<string, object>
        {
            ["staleConversations"] = stale,
            ["oldestPendingMinutes"] = oldest is { } since ? Math.Round((now - since).TotalMinutes, 1) : 0d,
            ["staleAfterMinutes"] = Math.Round(staleAfter.TotalMinutes, 1),
        };

        return stale == 0
            ? HealthCheckResult.Healthy("Bot turns flow.", data)
            : HealthCheckResult.Degraded(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{stale} WhatsApp conversation(s) wait for the bot for over {staleAfter.TotalMinutes:F0} minutes (bot turn executor stopped?): answer them in /admin/whatsapp."),
                data: data);
    }
}
