using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Users;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Components.Account;
using CarRental.Web.Components.Admin.Accounting;
using CarRental.Web.Components.Admin.Billing;
using CarRental.Web.Components.Admin.Bookings;
using CarRental.Web.Components.Admin.Handovers;
using CarRental.Web.Endpoints;
using CarRental.Web.Endpoints.CustomerApi;
using CarRental.Web.Endpoints.PublicSite;
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

        services.AddOptions<InteractiveCircuitOptions>()
            .Bind(configuration.GetSection(InteractiveCircuitOptions.SectionName))
            .Validate(o => o.IsValid, "Blazor: DisconnectedCircuitMaxRetained and MaxBufferedUnacknowledgedRenderBatches must be positive, DisconnectedCircuitRetentionPeriod between zero and one hour.")
            .ValidateOnStart();
        var circuits = configuration.GetSection(InteractiveCircuitOptions.SectionName).Get<InteractiveCircuitOptions>() ?? new InteractiveCircuitOptions();
        services.AddRazorComponents()
            .AddInteractiveServerComponents(options =>
            {
                options.DetailedErrors = environment.IsDevelopment();

                // Bounded memory of the back office circuits on one small instance (ADR-0068).
                options.DisconnectedCircuitMaxRetained = circuits.DisconnectedCircuitMaxRetained;
                options.DisconnectedCircuitRetentionPeriod = circuits.DisconnectedCircuitRetentionPeriod;
                options.MaxBufferedUnacknowledgedRenderBatches = circuits.MaxBufferedUnacknowledgedRenderBatches;
            });
        services.AddMudServices(options =>
        {
            options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            options.SnackbarConfiguration.VisibleStateDuration = 5000;
        });

        // MudBlazor's own texts (pager, pickers, dialogs…) in the user interface language.
        services.AddTransient<MudLocalizer, MudBlazorLocalizer>();

        AddLocalization(services);
        AddAuthentication(services, configuration, environment);
        AddAuthorization(services);

        services.AddHttpContextAccessor();
        services.AddSingleton<ClientIpResolver>();
        services.AddScoped<IAmbientUserContextProvider, HttpUserContextProvider>();
        services.AddScoped<CircuitHandler, UserContextCircuitHandler>();
        services.AddScoped<RequestOrigin>();
        services.AddScoped<IOperationRunner, OperationRunner>();
        services.AddScoped<CarRental.Web.Components.Admin.Shared.CurrentAgency>();
        services.AddScoped<AppUrls>();
        services.AddScoped<IUserInvitationSender, UserInvitationSender>();
        services.AddFleetWeb();
        services.AddBookingsWeb();
        services.AddClicToPayWeb();
        services.AddChannelsWeb(configuration);
        services.AddHandoversWeb();
        services.AddBillingWeb();
        services.AddAccountingWeb();
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
        services.AddPublicSite(configuration, environment);
        services.AddCustomerApi(configuration);
        services.AddWhatsAppWebhook(configuration);
        services.AddSecurityRateLimiting(configuration);
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

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<StaffSessionOptions>()
            .Bind(configuration.GetSection(StaffSessionOptions.SectionName))
            .Validate(o => o.IsValid, "Identity:Session:AbsoluteLifetime must be positive and at most 7 days.")
            .ValidateOnStart();
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

            // However active, a session ends 12 hours after its sign-in (Identity:Session:AbsoluteLifetime, ADR-0062).
            SessionLifetime.Configure(options);
        });
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.Name = "carrental.2fa";
            options.Cookie.SecurePolicy = securePolicy;
        });

        // A locked account or a changed password ends HTTP sessions within a minute; the claims of a valid session are
        // rebuilt at the same time, keeping how the user signed in (amr). A change of permissions refreshes the claims of
        // the user's sessions at their next request (UserSessionRegistry), without signing in again.
        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.ValidationInterval = TimeSpan.FromMinutes(1);
            options.OnRefreshingPrincipal = SessionClaims.KeepAuthenticationMethodAsync;
        });
        services.AddSingleton<UserSessionRegistry>();
        services.AddSingleton<IUserSessions>(provider => provider.GetRequiredService<UserSessionRegistry>());
        services.AddScoped<ISecurityStampValidator, CarRentalSecurityStampValidator>();
    }

    private static void AddAuthorization(IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.AdminArea, policy => policy.RequireAuthenticatedUser().AddRequirements(new MfaRequirement()));
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, MfaAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, ModuleAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AdminAuthorizationResultHandler>();
    }

    /// <summary>HTTPS-only cookies, except in development and tests which run over plain HTTP.</summary>
    private static CookieSecurePolicy CookieSecurePolicyFor(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(WebEnvironments.Testing)
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
}
