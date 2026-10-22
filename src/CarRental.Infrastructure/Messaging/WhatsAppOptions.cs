using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging;

/// <summary>How the platform talks to WhatsApp.</summary>
public enum WhatsAppMode
{
    /// <summary>The fake in development and tests, disabled elsewhere until configured.</summary>
    Auto = 0,

    /// <summary>Not configured: the webhook answers 404, nothing is sent, the inbox says so.</summary>
    Disabled = 1,

    /// <summary><see cref="FakeWhatsApp"/>: records sent messages and simulates their statuses (development, tests, E2E; never in production).</summary>
    Fake = 2,

    /// <summary>The WhatsApp Cloud API through the Graph API.</summary>
    Graph = 3,
}

/// <summary>
/// WhatsApp Business Platform (Cloud API) settings, section <c>WhatsApp</c>. The access token, the app secret (webhook
/// signature) and the verify token (webhook handshake) are secrets: Key Vault <c>WhatsApp--AccessToken</c>,
/// <c>WhatsApp--AppSecret</c>, <c>WhatsApp--VerifyToken</c> in Azure; development and tests use the values of
/// <see cref="WhatsAppDevelopmentKeys"/> when none is configured. Validated at startup. The agency served by the number
/// is read from the same section (<see cref="CarRental.Application.Messaging.Webhooks.WhatsAppRoutingOptions"/>).
/// </summary>
public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    public WhatsAppMode Mode { get; set; } = WhatsAppMode.Auto;

    /// <summary>Base address of the Graph API.</summary>
    public Uri GraphBaseUrl { get; set; } = new("https://graph.facebook.com/");

    /// <summary>Version of the Graph API, e.g. <c>v23.0</c> (TODO(spec): confirm the version in use at go-live).</summary>
    public string ApiVersion { get; set; } = "v23.0";

    /// <summary>Id of the WhatsApp business phone number (sends, and filter of the webhook events).</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>Id of the WhatsApp Business Account (filter of the webhook events).</summary>
    public string? BusinessAccountId { get; set; }

    /// <summary>System user access token of the Graph API (secret).</summary>
    public string? AccessToken { get; set; }

    /// <summary>App secret of the Meta app: key of the <c>X-Hub-Signature-256</c> HMAC (secret).</summary>
    public string? AppSecret { get; set; }

    /// <summary>Token chosen when subscribing the webhook, echoed by Meta in the verification handshake (secret).</summary>
    public string? VerifyToken { get; set; }

    /// <summary>Timeout of a Graph API call (20 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The fake reports "sent" then "delivered" for each accepted message, as Meta would (default true).</summary>
    public bool FakeSimulatesStatuses { get; set; } = true;

    public bool IsEnabled => Mode is WhatsAppMode.Fake or WhatsAppMode.Graph;
}

/// <summary>
/// Values used in development and tests when no secret is configured (they protect nothing outside this machine and the
/// test hosts, and the fake never calls Meta).
/// </summary>
public static class WhatsAppDevelopmentKeys
{
    public const string AppSecret = "dev-only-whatsapp-app-secret-0123456789"; // gitleaks:allow (development and test value)

    public const string VerifyToken = "dev-only-whatsapp-verify-token"; // gitleaks:allow (development and test value)

    public const string PhoneNumberId = "100000000000001";

    public const string BusinessAccountId = "100000000000000";
}

/// <summary>Resolves the automatic mode and the development values (before validation).</summary>
internal sealed class WhatsAppOptionsSetup(IHostEnvironment environment) : IPostConfigureOptions<WhatsAppOptions>
{
    public void PostConfigure(string? name, WhatsAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var local = environment.IsDevelopment() || environment.IsEnvironment(WhatsAppOptionsValidator.TestingEnvironment);
        if (options.Mode == WhatsAppMode.Auto)
        {
            options.Mode = local ? WhatsAppMode.Fake : WhatsAppMode.Disabled;
        }

        if (local && options.Mode == WhatsAppMode.Fake)
        {
            options.AppSecret = string.IsNullOrWhiteSpace(options.AppSecret) ? WhatsAppDevelopmentKeys.AppSecret : options.AppSecret;
            options.VerifyToken = string.IsNullOrWhiteSpace(options.VerifyToken) ? WhatsAppDevelopmentKeys.VerifyToken : options.VerifyToken;
            options.PhoneNumberId = string.IsNullOrWhiteSpace(options.PhoneNumberId) ? WhatsAppDevelopmentKeys.PhoneNumberId : options.PhoneNumberId;
            options.BusinessAccountId = string.IsNullOrWhiteSpace(options.BusinessAccountId) ? WhatsAppDevelopmentKeys.BusinessAccountId : options.BusinessAccountId;
        }
    }
}

/// <summary>Fails the startup on an incomplete or unsafe configuration (the fake is refused in production).</summary>
internal sealed partial class WhatsAppOptionsValidator(IHostEnvironment environment) : IValidateOptions<WhatsAppOptions>
{
    public const string TestingEnvironment = "Testing";

    /// <summary>Shortest verify token accepted (it guards the subscription of the webhook).</summary>
    public const int MinVerifyTokenLength = 16;

    public ValidateOptionsResult Validate(string? name, WhatsAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Mode == WhatsAppMode.Fake && environment.IsProduction())
        {
            failures.Add("WhatsApp:Mode=Fake must never be used in Production.");
        }

        if (options.Timeout < TimeSpan.FromSeconds(1) || options.Timeout > TimeSpan.FromMinutes(2))
        {
            failures.Add("WhatsApp:Timeout must lie between 1 second and 2 minutes.");
        }

        if (options.IsEnabled)
        {
            if (string.IsNullOrWhiteSpace(options.AppSecret))
            {
                failures.Add("WhatsApp:AppSecret is required (Key Vault WhatsApp--AppSecret): webhook signatures cannot be verified without it.");
            }

            if (options.VerifyToken is not { Length: >= MinVerifyTokenLength })
            {
                failures.Add($"WhatsApp:VerifyToken is required, {MinVerifyTokenLength} characters at least (Key Vault WhatsApp--VerifyToken).");
            }
        }

        if (options.Mode == WhatsAppMode.Graph)
        {
            var local = environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment);
            if (options.GraphBaseUrl is not { IsAbsoluteUri: true } baseUrl || (baseUrl.Scheme != Uri.UriSchemeHttps && !local))
            {
                failures.Add("WhatsApp:GraphBaseUrl must be an absolute https address.");
            }

            if (!ApiVersionPattern().IsMatch(options.ApiVersion ?? string.Empty))
            {
                failures.Add("WhatsApp:ApiVersion must look like v23.0.");
            }

            if (!IdPattern().IsMatch(options.PhoneNumberId ?? string.Empty))
            {
                failures.Add("WhatsApp:PhoneNumberId is required (digits).");
            }

            if (!IdPattern().IsMatch(options.BusinessAccountId ?? string.Empty))
            {
                failures.Add("WhatsApp:BusinessAccountId is required (digits).");
            }

            if (string.IsNullOrWhiteSpace(options.AccessToken))
            {
                failures.Add("WhatsApp:AccessToken is required (Key Vault WhatsApp--AccessToken).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex("^v[0-9]{1,3}\\.[0-9]{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();

    [GeneratedRegex("^[0-9]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
