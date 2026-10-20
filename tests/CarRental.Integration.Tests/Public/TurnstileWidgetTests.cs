using System.Globalization;
using CarRental.Web.Components.Public.AntiAbuse;
using CarRental.Web.Endpoints.PublicSite.Turnstile;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Public;

/// <summary>The reusable Turnstile widget rendered on its own (offline test mode, configured, not configured).</summary>
public sealed class TurnstileWidgetTests
{
    private static async Task<string> RenderAsync(TurnstileOptions options)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddLocalization()
            .AddSingleton(Options.Create(options))
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try
        {
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<TurnstileWidget>(
                    ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(TurnstileWidget.Action)] = "booking_submit" }));
                return output.ToHtmlString();
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task Tests_render_the_widget_offline_with_the_dummy_token()
    {
        var html = await RenderAsync(new TurnstileOptions { OfflineTestWidget = true });

        html.ShouldContain($"name=\"{TurnstileOptions.ResponseFieldName}\" value=\"{TurnstileTestKeys.DummyToken}\"");
        html.ShouldNotContain("challenges.cloudflare.com");
    }

    [Fact]
    public async Task A_configured_widget_loads_cloudflare_with_the_site_key_and_the_action()
    {
        var html = await RenderAsync(new TurnstileOptions { SiteKey = TurnstileTestKeys.AlwaysPassesSiteKey, SecretKey = TurnstileTestKeys.AlwaysPassesSecretKey });

        html.ShouldContain($"class=\"cf-turnstile\" data-sitekey=\"{TurnstileTestKeys.AlwaysPassesSiteKey}\" data-action=\"booking_submit\" data-language=\"en\"");
        html.ShouldContain($"data-response-field-name=\"{TurnstileOptions.ResponseFieldName}\"");
        html.ShouldContain("<script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js\" async defer></script>");
        html.ShouldContain("<noscript>");
    }

    [Fact]
    public async Task Without_keys_the_widget_says_the_check_is_unavailable()
    {
        var html = await RenderAsync(new TurnstileOptions());

        html.ShouldContain("role=\"alert\"");
        html.ShouldContain("anti-bot check is temporarily unavailable");
        html.ShouldNotContain("cf-turnstile");
    }
}
