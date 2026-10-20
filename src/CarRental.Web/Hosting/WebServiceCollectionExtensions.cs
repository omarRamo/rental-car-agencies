using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Users;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Components.Account;
using CarRental.Web.Localization;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using MudBlazor;
using MudBlazor.Services;

namespace CarRental.Web.Hosting;

internal static class WebServiceCollectionExtensions
{
    public static IServiceCollection AddCarRentalWeb(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<PublicSiteOptions>().Bind(configuration.GetSection(PublicSiteOptions.SectionName)).ValidateOnStart();
        services.AddOptions<HostFilteringSettings>().Bind(configuration.GetSection(HostFilteringSettings.SectionName)).ValidateOnStart();
        services.AddOptions<ReverseProxyOptions>().Bind(configuration.GetSection(ReverseProxyOptions.SectionName));
        services.AddOptions<LocalizationSettings>().Bind(configuration.GetSection(LocalizationSettings.SectionName));

        services.AddRazorComponents()
            .AddInteractiveServerComponents(options => options.DetailedErrors = environment.IsDevelopment());
        services.AddMudServices(options =>
        {
            options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            options.SnackbarConfiguration.VisibleStateDuration = 5000;
        });

        AddLocalization(services);
        AddAuthentication(services, environment);
        AddAuthorization(services);

        services.AddHttpContextAccessor();
        services.AddSingleton<ClientIpResolver>();
        services.AddScoped<IAmbientUserContextProvider, HttpUserContextProvider>();
        services.AddScoped<CircuitHandler, UserContextCircuitHandler>();
        services.AddScoped<RequestOrigin>();
        services.AddScoped<IOperationRunner, OperationRunner>();
        services.AddScoped<AppUrls>();
        services.AddScoped<IUserInvitationSender, UserInvitationSender>();
        services.AddScoped<IEmailSender<ApplicationUser>, IdentityEmailSender>();
        services.AddScoped<IdentityErrorDescriber, LocalizedIdentityErrorDescriber>();

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "carrental.af";
            options.Cookie.SecurePolicy = CookieSecurePolicyFor(environment);
        });

        if (!environment.IsDevelopment())
        {
            services.AddHsts(options =>
            {
                options.MaxAge = TimeSpan.FromDays(365);
                options.IncludeSubDomains = true;
                options.Preload = false;
            });
        }

        services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 10 * 1024 * 1024);
        return services;
    }

    private static void AddLocalization(IServiceCollection services)
    {
        services.AddLocalization();
        services.AddSingleton<CultureCatalog>();
        services.AddOptions<RequestLocalizationOptions>().Configure<CultureCatalog>((options, catalog) =>
        {
            options.DefaultRequestCulture = new RequestCulture(CultureCatalog.Default);
            options.SupportedCultures = [.. catalog.SupportedCultures];
            options.SupportedUICultures = [.. catalog.SupportedCultures];
            options.FallBackToParentCultures = true;
            options.FallBackToParentUICultures = true;
            options.ApplyCurrentCultureToResponseHeaders = true;
            options.RequestCultureProviders =
            [
                new PathRequestCultureProvider(catalog),
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider(),
            ];
        });
    }

    private static void AddAuthentication(IServiceCollection services, IHostEnvironment environment)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<IdentityRedirectManager>();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        var securePolicy = CookieSecurePolicyFor(environment);
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "carrental.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.LoginPath = "/account/login";
            options.LogoutPath = AccountEndpoints.LogoutPath;
            options.AccessDeniedPath = "/account/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        });
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.Name = "carrental.2fa";
            options.Cookie.SecurePolicy = securePolicy;
        });

        // A locked account or a changed password ends HTTP sessions within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
    }

    private static void AddAuthorization(IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.AdminArea, policy => policy.RequireAuthenticatedUser().AddRequirements(new MfaRequirement()));
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, MfaAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AdminAuthorizationResultHandler>();
    }

    /// <summary>HTTPS-only cookies, except in development and tests which run over plain HTTP.</summary>
    private static CookieSecurePolicy CookieSecurePolicyFor(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(WebEnvironments.Testing)
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
}
