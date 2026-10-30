using CarRental.Application.Bookings.Notifications;
using CarRental.Application.Referential.Contracts;
using CarRental.Web.Components.Emails;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Search;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Endpoints.PublicSite.Turnstile;
using CarRental.Web.Hosting;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>Composition of the public site (packages 2A and 2C): one call for the services, one for the endpoints.</summary>
public static class PublicSiteSetup
{
    public static IServiceCollection AddPublicSite(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddSettingDefinitions(PublicSiteSettings.All);
        // The only IMemoryCache of the host holds the public facts of the agencies (one entry per open site, Size = 1):
        // the limit is a guard against a future unbounded key, not a working figure (ADR-0068).
        services.AddMemoryCache(options => options.SizeLimit = 1_000);
        services.AddScoped<PublicAgencyDirectory>();
        services.AddScoped<PublicSearchService>();
        services.AddScoped<FeaturedOffers>();
        services.AddScoped<FunnelTracker>();
        services.AddSingleton<PublicBookingCookies>();
        services.AddScoped<ICustomerEmailComposer, CustomerEmailComposer>();
        services.AddOptions<PublicApiOptions>().Bind(configuration.GetSection(PublicApiOptions.SectionName));
        services.AddPublicRateLimiting(configuration);
        services.AddTurnstile(configuration, environment);
        services.AddCacheablePublicPages();
        return services;
    }

    /// <summary>
    /// sitemap.xml, robots.txt, the address of each site (<c>/{agency}</c>, redirected to its best culture), the public
    /// read and quote APIs, the "Choose" action of the results, the forms of the booking funnel and of "Ma réservation".
    /// </summary>
    public static IEndpointRouteBuilder MapPublicSite(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapSeoEndpoints();
        endpoints.MapGet(PublicSiteAgency.RouteParameterSegment, PublicSiteRoot.Redirect)
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimits.Search)
            .ExcludeFromDescription();
        endpoints.MapPublicAvailabilityApi();
        endpoints.MapPublicQuoteApi();
        endpoints.MapBookingSelection();
        endpoints.MapBookingFunnel();
        endpoints.MapMyBooking();
        return endpoints;
    }

    /// <summary>
    /// Turnstile options and the verifier (typed <see cref="HttpClient"/>). Development and tests use the always-passing
    /// test keys of Cloudflare when none is configured; tests also render the widget offline by default. The answers of
    /// test keys are accepted outside production only, whatever the configuration says.
    /// </summary>
    private static IServiceCollection AddTurnstile(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var testing = environment.IsEnvironment(WebEnvironments.Testing);
        services.AddOptions<TurnstileOptions>()
            .Bind(configuration.GetSection(TurnstileOptions.SectionName))
            .PostConfigure(options =>
            {
                if (environment.IsDevelopment() || testing)
                {
                    options.SiteKey = string.IsNullOrWhiteSpace(options.SiteKey) ? TurnstileTestKeys.AlwaysPassesSiteKey : options.SiteKey;
                    options.SecretKey = string.IsNullOrWhiteSpace(options.SecretKey) ? TurnstileTestKeys.AlwaysPassesSecretKey : options.SecretKey;
                }

                options.OfflineTestWidget = !environment.IsProduction()
                    && (configuration.GetValue<bool?>($"{TurnstileOptions.SectionName}:{nameof(TurnstileOptions.OfflineTestWidget)}") ?? testing);
                options.AcceptTestingKeys = !environment.IsProduction();
            });
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10));
        return services;
    }
}
