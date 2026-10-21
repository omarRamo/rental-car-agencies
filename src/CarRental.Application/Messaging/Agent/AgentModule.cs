using CarRental.Application.Common.Seeding;
using CarRental.Application.Messaging.Agent.Costs;
using CarRental.Application.Messaging.Agent.Knowledge;
using CarRental.Application.Messaging.Agent.Tools;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Messaging.Agent;

/// <summary>
/// Registration of the WhatsApp agent (package 4B) inside the Messaging module: the bot turn execution (dispatcher,
/// runner, queue, per-conversation lock — the infrastructure replaces the in-process defaults with the background
/// executor and the SQL Server lock), the agent as <see cref="IConversationResponder"/> with its tools, its knowledge base
/// and cost reports, and its settings. Without a configured model (<see cref="IAgentModel"/>) the agent stays silent.
/// </summary>
internal static class AgentModule
{
    public static IServiceCollection AddWhatsAppAgent(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Bot turns: run inline by default; the infrastructure runs them in the background executor (ADR-0054).
        services.AddOptions<BotTurnOptions>();
        services.TryAddSingleton<IBotTurnDispatcher, InlineBotTurnDispatcher>();
        services.TryAddSingleton<IConversationTurnLock, InProcessConversationTurnLock>();
        services.TryAddSingleton<IBotAudience, EveryoneBotAudience>();
        services.AddSingleton<IBotTurnRunner, BotTurnRunner>();
        services.AddScoped<IBotTurnQueue, BotTurnQueue>();
        services.AddScoped<BotTurnRetries>();

        // The agent replaces the default responder (ADR-0055, ADR-0056).
        services.AddOptions<AgentSiteOptions>();
        services.TryAddSingleton<IAgentModel, UnavailableAgentModel>();
        services.TryAddSingleton<IAgentPromptSource, EmptyAgentPromptSource>();
        services.Replace(ServiceDescriptor.Scoped<IConversationResponder, AgentConversationResponder>());
        services.AddScoped<AgentPromptRenderer>();
        services.AddScoped<AgentAgencyLoader>();
        services.AddScoped<AgentMemory>();
        services.AddScoped<SearchAvailabilityTool>();
        services.AddScoped<QuoteLinkTool>();
        services.AddScoped<AgencyInfoTool>();
        services.AddScoped<BookingStatusTool>();
        services.AddScoped<TransferTool>();
        services.AddScoped<AgentToolbox>();

        // Knowledge base (validated by the manager) and cost reports.
        services.AddScoped<KnowledgeBaseQueries>();
        services.AddScoped<KnowledgeBaseService>();
        services.AddDataSeeder<KnowledgeSeeder>();
        services.AddScoped<AgentCostQueries>();

        services.AddSettingDefinitions(AgentSettings.All);
        return services;
    }
}

/// <summary>No model configured (<c>Ai:Mode=Disabled</c>): the agent stays silent and agents answer in the shared inbox.</summary>
internal sealed class UnavailableAgentModel : IAgentModel
{
    public bool IsAvailable => false;

    public string ModelId => "none";

    public int MaxOutputTokens => 0;

    public IChatClient Client => throw new InvalidOperationException("No model is configured for the WhatsApp agent (Ai:Mode).");
}

/// <summary>No prompt available (hosts without the infrastructure): the agent is never called with a model there.</summary>
internal sealed class EmptyAgentPromptSource : IAgentPromptSource
{
    public string SystemPromptTemplate => "Tu es l'assistant WhatsApp de {{AGENCY_NAME}}.";

    public string Addendum => string.Empty;
}
