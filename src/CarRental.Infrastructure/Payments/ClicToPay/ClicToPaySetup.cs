using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using CarRental.Application.Payments.Online;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Payments.ClicToPay;

/// <summary>
/// Composition of the Clic to Pay gateway (ADR-0038): <c>ClicToPay:Enabled</c> false → every call refused;
/// <c>ClicToPay:UseFake</c> (Development and Testing only) → the in-app <see cref="FakeClicToPay"/>; otherwise the typed
/// <see cref="ClicToPayClient"/>. The call log is written by <see cref="EfGatewayCallLog"/>.
/// </summary>
public static class ClicToPaySetup
{
    /// <summary>Largest answer read from the gateway (its JSON answers are a few kilobytes).</summary>
    public const int MaxResponseBytes = 256 * 1024;

    public static IServiceCollection AddClicToPay(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ClicToPayOptions>().Bind(configuration.GetSection(ClicToPayOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClicToPayOptions>, ClicToPayOptionsValidator>();
        services.AddSingleton<IGatewayCallLog, EfGatewayCallLog>();
        services.AddSingleton<FakeClicToPay>();
        services.AddHttpClient<ClicToPayClient>((serviceProvider, http) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ClicToPayOptions>>().Value;
            http.BaseAddress = options.BaseUrl;
            http.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120));
            http.MaxResponseContentBufferSize = MaxResponseBytes;
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddScoped<IClicToPayGateway>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ClicToPayOptions>>().Value;
            if (!options.Enabled)
            {
                return new DisabledClicToPayGateway();
            }

            return options.UseFake
                ? serviceProvider.GetRequiredService<FakeClicToPay>()
                : serviceProvider.GetRequiredService<ClicToPayClient>();
        });
        return services;
    }
}

/// <summary>
/// Checks the gateway settings at start: the simulated gateway only in Development and Testing; with the real gateway, an
/// HTTPS API root ending with '/', credentials and absolute HTTPS return addresses; sane multiplier, time-outs, language,
/// currency and status codes.
/// </summary>
internal sealed partial class ClicToPayOptionsValidator(IHostEnvironment environment) : IValidateOptions<ClicToPayOptions>
{
    /// <summary>Name of the environment of the automated tests.</summary>
    public const string TestingEnvironment = "Testing";

    private static readonly int[] Multipliers = [1, 10, 100, 1000];

    public ValidateOptionsResult Validate(string? name, ClicToPayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (!Multipliers.Contains(options.AmountMultiplier))
        {
            errors.Add("ClicToPay:AmountMultiplier must be 1, 10, 100 or 1000 (1000 = millimes).");
        }

        if (options.TimeoutSeconds is < 1 or > 120)
        {
            errors.Add("ClicToPay:TimeoutSeconds must be between 1 and 120.");
        }

        if (options.StatusReadAttempts is < 1 or > 5 || options.StatusRetryDelayMilliseconds is < 0 or > 10_000)
        {
            errors.Add("ClicToPay:StatusReadAttempts must be between 1 and 5 and ClicToPay:StatusRetryDelayMilliseconds between 0 and 10000.");
        }

        if (options.Language is not { Length: 2 } language || !language.All(char.IsAsciiLetterLower))
        {
            errors.Add("ClicToPay:Language must be a two-letter lower-case language code (ISO 639-1).");
        }

        if (!string.IsNullOrWhiteSpace(options.Currency) && !CurrencyPattern().IsMatch(options.Currency.Trim()))
        {
            errors.Add("ClicToPay:Currency must be empty or a three-digit ISO 4217 numeric code (788 for TND).");
        }

        if (string.IsNullOrWhiteSpace(options.ReturnUrl) || string.IsNullOrWhiteSpace(options.FailUrl))
        {
            errors.Add("ClicToPay:ReturnUrl and ClicToPay:FailUrl are required.");
        }

        errors.AddRange(ClicToPayStatusMap.Validate(options, out _));

        if (options.Enabled && options.UseFake && !(environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment)))
        {
            errors.Add("ClicToPay:UseFake is refused outside Development and Testing: the simulated gateway accepts any payment.");
        }

        if (options.Enabled && !options.UseFake)
        {
            if (options.BaseUrl is not { IsAbsoluteUri: true } baseUrl || !IsSecure(baseUrl) || !baseUrl.AbsolutePath.EndsWith('/'))
            {
                errors.Add("ClicToPay:BaseUrl must be an absolute HTTPS address ending with '/' (e.g. https://test.clictopay.com/payment/rest/).");
            }

            if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.Password))
            {
                errors.Add("ClicToPay:UserName and ClicToPay:Password are required (Key Vault secrets ClicToPay--UserName and ClicToPay--Password).");
            }

            if (!IsAbsoluteSecure(options.ReturnUrl) || !IsAbsoluteSecure(options.FailUrl))
            {
                errors.Add("ClicToPay:ReturnUrl and ClicToPay:FailUrl must be absolute HTTPS addresses with the real gateway (set PublicSite:BaseUrl or give them in full).");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>HTTPS, or HTTP on the local machine (test stubs).</summary>
    private static bool IsSecure(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static bool IsAbsoluteSecure(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsSecure(uri);

    [GeneratedRegex("^[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();
}
