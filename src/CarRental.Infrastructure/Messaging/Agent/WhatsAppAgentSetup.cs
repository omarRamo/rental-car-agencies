using Anthropic;
using Anthropic.Core;
using Anthropic.Foundry;
using Azure.Core;
using CarRental.Application.Common.Caching;
using CarRental.Application.Messaging.Agent;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.BackgroundJobs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging.Agent;

/// <summary>
/// The adapters of the WhatsApp agent: the model (<c>Ai:Mode</c>: Claude API, Microsoft Foundry, the scripted fake or
/// none) behind <see cref="IAgentModel"/>, the embedded system prompt, the pilot audience, the links of the public site,
/// the bot turn executor (background pump, SQL Server lock per conversation, wake-ups after commits) and its readiness
/// entry <c>whatsapp</c>.
/// </summary>
public static class WhatsAppAgentSetup
{
    public static IServiceCollection AddWhatsAppAgent(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<AiOptions>, AiOptionsSetup>();
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddOptions<BotTurnOptions>().Bind(configuration.GetSection(BotTurnOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BotTurnOptions>, BotTurnOptionsValidator>();
        services.AddOptions<AgentSiteOptions>()
            .Configure<IHostEnvironment>((site, environment) =>
            {
                site.PublicBaseUrl = Uri.TryCreate(configuration["PublicSite:BaseUrl"], UriKind.Absolute, out var baseUrl)
                    ? baseUrl
                    : AiOptionsValidator.IsLocal(environment) ? new Uri("https://localhost:7180/") : null;
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AgentSiteOptions>, AgentSiteOptionsValidator>();

        services.AddSingleton<ScriptedChatClient>();
        services.AddSingleton<IAgentModel>(CreateModel);
        services.Replace(ServiceDescriptor.Singleton<IAgentPromptSource, EmbeddedAgentPromptSource>());
        services.Replace(ServiceDescriptor.Singleton<IBotAudience, PilotBotAudience>());

        // The bot turn executor (ADR-0054).
        services.AddSingleton<BotTurnWakeup>();
        services.Replace(ServiceDescriptor.Singleton<IBotTurnDispatcher, BackgroundBotTurnDispatcher>());
        services.Replace(ServiceDescriptor.Singleton<IConversationTurnLock, SqlConversationTurnLock>());
        services.AddSingleton<ICommittedChangesListener, BotTurnCommitListener>();
        var jobs = configuration.GetSection(BackgroundJobsOptions.SectionName).Get<BackgroundJobsOptions>() ?? new BackgroundJobsOptions();
        if (jobs.RunServer)
        {
            // Like the outbox pump: an instance without the job server leaves the turns to the one that has it.
            services.AddHostedService<BotTurnPump>();
        }

        // Outside the outbox, a stuck bot is seen by its own entry (published and alerted like every entry, ADR-0060).
        services.AddHealthChecks().AddCheck<WhatsAppHealthCheck>("whatsapp", tags: [HealthTags.Ready]);
        return services;
    }

    private static IAgentModel CreateModel(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<AiOptions>>().Value;
        var logger = provider.GetRequiredService<ILogger<ClaudeChatClient>>();

        // Metadata only in the SDK's spans, whatever the environment variables say: no message content in the telemetry.
        var telemetry = new OpenTelemetryOptions { Traces = new OpenTelemetryTraces { ContentMode = OpenTelemetryContentMode.MetadataOnly } };
        switch (options.Mode)
        {
            case AiMode.Fake:
                return new ConfiguredAgentModel(provider.GetRequiredService<ScriptedChatClient>(), options.Model, options.MaxOutputTokens, owned: null);

            case AiMode.Anthropic:
                {
                    var client = new AnthropicClient { ApiKey = options.ApiKey, MaxRetries = options.MaxRetries, Timeout = options.Timeout, OpenTelemetry = telemetry };
                    return new ConfiguredAgentModel(
                        new ClaudeChatClient(client.AsIChatClient(options.Model, options.MaxOutputTokens), options.Model, options, logger),
                        options.Model,
                        options.MaxOutputTokens,
                        client);
                }

            case AiMode.Foundry:
                {
                    var resource = options.Foundry.Resource!;
                    IAnthropicFoundryCredentials credentials = string.IsNullOrWhiteSpace(options.Foundry.ApiKey)
                        ? new AnthropicFoundryIdentityTokenCredentials(provider.GetRequiredService<TokenCredential>(), resource)
                        : new AnthropicFoundryApiKeyCredentials(options.Foundry.ApiKey, resource);
                    var client = new AnthropicFoundryClient(credentials) { MaxRetries = options.MaxRetries, Timeout = options.Timeout, OpenTelemetry = telemetry };
                    return new ConfiguredAgentModel(
                        new ClaudeChatClient(client.AsIChatClient(options.Model, options.MaxOutputTokens), options.Model, options, logger),
                        options.Model,
                        options.MaxOutputTokens,
                        client);
                }

            default:
                return DisabledAgentModel.Instance;
        }
    }
}

/// <summary>The configured model of the agent; disposes the SDK client it owns with the application.</summary>
internal sealed class ConfiguredAgentModel(IChatClient client, string modelId, int maxOutputTokens, IDisposable? owned) : IAgentModel, IDisposable
{
    public bool IsAvailable => true;

    public string ModelId => modelId;

    public int MaxOutputTokens => maxOutputTokens;

    public IChatClient Client => client;

    public void Dispose() => owned?.Dispose();
}

/// <summary><c>Ai:Mode=Disabled</c>: the bot stays silent.</summary>
internal sealed class DisabledAgentModel : IAgentModel
{
    public static DisabledAgentModel Instance { get; } = new();

    public bool IsAvailable => false;

    public string ModelId => "none";

    public int MaxOutputTokens => 0;

    public IChatClient Client => throw new InvalidOperationException("No model is configured for the WhatsApp agent (Ai:Mode=Disabled).");
}

/// <summary><c>Ai:PilotNumbers</c>: when set, the bot answers these numbers only (rollout); everyone otherwise.</summary>
internal sealed class PilotBotAudience(IOptions<AiOptions> options) : IBotAudience
{
    private readonly Lazy<HashSet<string>?> _pilot = new(() =>
    {
        var numbers = (options.Value.PilotNumbers ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => WhatsAppNumbers.Normalize(n.Replace(" ", string.Empty, StringComparison.Ordinal)))
            .Where(n => n.IsSuccess)
            .Select(n => n.Value)
            .ToHashSet(StringComparer.Ordinal);
        return numbers.Count == 0 ? null : numbers;
    });

    public bool Includes(string whatsAppNumber) => _pilot.Value is not { } pilot || pilot.Contains(whatsAppNumber);
}

/// <summary>Bounds of the bot turn executor.</summary>
internal sealed class BotTurnOptionsValidator : IValidateOptions<BotTurnOptions>
{
    public ValidateOptionsResult Validate(string? name, BotTurnOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.MaxParallelism is < 1 or > 32)
        {
            failures.Add("BotTurns:MaxParallelism must lie between 1 and 32.");
        }

        if (options.QuietPeriod < TimeSpan.Zero || options.QuietPeriod > TimeSpan.FromMinutes(1))
        {
            failures.Add("BotTurns:QuietPeriod must lie between 0 and 1 minute.");
        }

        if (options.SweepInterval < TimeSpan.FromSeconds(1) || options.SweepInterval > TimeSpan.FromMinutes(10))
        {
            failures.Add("BotTurns:SweepInterval must lie between 1 second and 10 minutes.");
        }

        if (options.ParsedRetryDelays() is not { Count: <= 10 })
        {
            failures.Add("BotTurns:RetryDelays must be up to 10 positive durations separated by ';' (00:00:20;00:01:00;00:05:00).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>A model answering customers needs the absolute address of the public site (links of the funnel), outside development.</summary>
internal sealed class AgentSiteOptionsValidator(IHostEnvironment environment, IOptions<AiOptions> ai) : IValidateOptions<AgentSiteOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentSiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (ai.Value.UsesProvider && !AiOptionsValidator.IsLocal(environment) && options.PublicBaseUrl is not { Scheme: "https" })
        {
            return ValidateOptionsResult.Fail("PublicSite:BaseUrl (absolute https) is required when the WhatsApp agent uses a model: its links point to the booking funnel.");
        }

        return ValidateOptionsResult.Success;
    }
}
