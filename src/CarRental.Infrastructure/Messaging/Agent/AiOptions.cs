using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging.Agent;

/// <summary>Which model answers the WhatsApp customers.</summary>
public enum AiMode
{
    /// <summary>The scripted fake in Development and Testing, disabled elsewhere until configured.</summary>
    Auto = 0,

    /// <summary>No model: the bot stays silent, agents answer in the shared inbox.</summary>
    Disabled = 1,

    /// <summary><see cref="ScriptedChatClient"/>: scripted replies, no provider (Development, Testing; refused in Production).</summary>
    Fake = 2,

    /// <summary>The Claude API of Anthropic (official SDK, key <c>Ai:ApiKey</c>).</summary>
    Anthropic = 3,

    /// <summary>Claude in Microsoft Foundry (<c>Anthropic.Foundry</c>: Global or US Data Zone deployments, no European zone).</summary>
    Foundry = 4,
}

/// <summary>Where the variable context of a turn goes in the request (ADR-0055).</summary>
public enum AiContextPlacement
{
    /// <summary>A mid-conversation system message on the Claude API, the last user turn on Microsoft Foundry.</summary>
    Auto = 0,

    /// <summary>A mid-conversation system message (<c>role: system</c> after the transcript).</summary>
    SystemMessage = 1,

    /// <summary>A delimited block (<c>&lt;contexte_plateforme&gt;</c>) at the end of the last user turn.</summary>
    UserTurn = 2,
}

/// <summary>
/// Settings of the model of the WhatsApp agent, section <c>Ai</c> (ADR-0055), validated at startup. The API key is a
/// secret (Key Vault <c>Ai--ApiKey</c>, never logged); on Foundry, a key (<c>Ai--Foundry--ApiKey</c>) or, without one,
/// the managed identity of the application.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public const string DefaultModel = "claude-haiku-5-5";

    public AiMode Mode { get; set; } = AiMode.Auto;

    /// <summary>The model id (<c>claude-haiku-5-5</c>).</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Output limit of one call, thinking included (2 048).</summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>Timeout of one HTTP call to the provider (30 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Retries of the SDK on a rate limit, an overload or a server error, within one call (2).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Key of the Claude API (secret: Key Vault <c>Ai--ApiKey</c>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Lifetime of the prompt cache entries: <c>5m</c> (default) or <c>1h</c>.</summary>
    public string PromptCacheTtl { get; set; } = "5m";

    public AiContextPlacement ContextPlacement { get; set; } = AiContextPlacement.Auto;

    /// <summary>Pilot numbers (E.164, separated by <c>;</c>): when set, the bot answers them only.</summary>
    public string? PilotNumbers { get; set; }

    public FoundryOptions Foundry { get; set; } = new();

    public bool UsesProvider => Mode is AiMode.Anthropic or AiMode.Foundry;
}

/// <summary>Microsoft Foundry resource of the agent (<c>https://{Resource}.services.ai.azure.com/anthropic</c>).</summary>
public sealed class FoundryOptions
{
    /// <summary>Name of the Foundry resource.</summary>
    public string? Resource { get; set; }

    /// <summary>Key of the resource (secret: Key Vault <c>Ai--Foundry--ApiKey</c>); without it, the managed identity is used.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Resolves the automatic mode before validation: the scripted fake in Development and Testing, disabled elsewhere.</summary>
internal sealed class AiOptionsSetup(IHostEnvironment environment) : IPostConfigureOptions<AiOptions>
{
    public void PostConfigure(string? name, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode == AiMode.Auto)
        {
            options.Mode = AiOptionsValidator.IsLocal(environment) ? AiMode.Fake : AiMode.Disabled;
        }
    }
}

/// <summary>Fails the startup on an incomplete or unsafe configuration of the model (the fake is refused in production).</summary>
internal sealed class AiOptionsValidator(IHostEnvironment environment) : IValidateOptions<AiOptions>
{
    public const string TestingEnvironment = "Testing";

    public static bool IsLocal(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment);

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Mode == AiMode.Fake && !IsLocal(environment))
        {
            failures.Add("Ai:Mode=Fake is refused outside Development and Testing: the scripted model answers anything.");
        }

        if (string.IsNullOrWhiteSpace(options.Model) || options.Model.Length > 100)
        {
            failures.Add("Ai:Model is required (e.g. claude-haiku-5-5).");
        }

        if (options.MaxOutputTokens is < 256 or > 16_000)
        {
            failures.Add("Ai:MaxOutputTokens must lie between 256 and 16000 (thinking counts in it).");
        }

        if (options.Timeout < TimeSpan.FromSeconds(5) || options.Timeout > TimeSpan.FromMinutes(2))
        {
            failures.Add("Ai:Timeout must lie between 5 seconds and 2 minutes.");
        }

        if (options.MaxRetries is < 0 or > 5)
        {
            failures.Add("Ai:MaxRetries must lie between 0 and 5.");
        }

        if (options.PromptCacheTtl is not ("5m" or "1h"))
        {
            failures.Add("Ai:PromptCacheTtl must be 5m or 1h.");
        }

        if (options.Mode == AiMode.Anthropic && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add("Ai:ApiKey is required with Ai:Mode=Anthropic (Key Vault secret Ai--ApiKey).");
        }

        if (options.Mode == AiMode.Foundry && (string.IsNullOrWhiteSpace(options.Foundry.Resource) || !options.Foundry.Resource.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            failures.Add("Ai:Foundry:Resource is required with Ai:Mode=Foundry (name of the Foundry resource, letters, digits and dashes).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
