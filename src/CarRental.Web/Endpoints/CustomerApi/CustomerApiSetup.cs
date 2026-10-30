using CarRental.Application.Customers.Contracts;
using CarRental.Web.Components.Emails;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.CustomerApi;

/// <summary>Composition of the API of the mobile application and of the storefronts (ADR-0070).</summary>
public static class CustomerApiSetup
{
    public static IServiceCollection AddCustomerApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CustomerApiOptions>().Bind(configuration.GetSection(CustomerApiOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<CustomerApiOptions>, CustomerApiOptionsSetup>();
        services.AddSingleton<IValidateOptions<CustomerApiOptions>, CustomerApiOptionsSetup>();
        services.AddSingleton<CustomerTokenIssuer>();
        services.AddScoped<ICustomerAccountEmailComposer, CustomerAccountEmailComposer>();

        // The bearer scheme of the customers, next to the cookies of the staff (the default scheme stays the cookie one).
        services.AddAuthentication().AddJwtBearer(CustomerApiAuthentication.Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(CustomerApiAuthentication.Scheme).Configure<IOptions<CustomerApiOptions>>((bearer, api) =>
        {
            bearer.MapInboundClaims = false;
            bearer.SaveToken = false;
            bearer.TokenValidationParameters = CustomerTokenIssuer.ValidationParameters(api.Value);
        });
        services.AddAuthorizationBuilder().AddPolicy(CustomerApiAuthentication.Policy, policy => policy
            .AddAuthenticationSchemes(CustomerApiAuthentication.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(CustomerApiAuthentication.KindClaim, CustomerApiAuthentication.KindValue));

        // Browsers of the storefronts hosted elsewhere (CustomerApi:AllowedOrigins); the applications need no CORS.
        services.AddCors(options => options.AddPolicy(CustomerApiEndpoints.CorsPolicy, _ => { }));
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<IOptions<CustomerApiOptions>>((cors, api) =>
            cors.AddPolicy(CustomerApiEndpoints.CorsPolicy, policy =>
            {
                var origins = api.Value.AllowedOrigins.Select(o => o.TrimEnd('/')).ToArray();
                if (origins.Length > 0)
                {
                    policy.WithOrigins(origins).WithMethods("GET", "POST", "PATCH", "DELETE").WithHeaders("Authorization", "Content-Type", "Accept-Language").SetPreflightMaxAge(TimeSpan.FromHours(1));
                }
            }));

        services.AddOutputCache(options => options.AddBasePolicy(policy => policy.NoCache()));
        services.AddOpenApi(CustomerApiEndpoints.OpenApiDocument, options =>
        {
            options.ShouldInclude = description => string.Equals(description.GroupName, CustomerApiEndpoints.OpenApiDocument, StringComparison.Ordinal);
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "CarRental — API clients (application mobile et sites vitrines)";
                document.Info.Version = "v1";
                document.Info.Description = "Chaque adresse commence par l'adresse de la vitrine de l'agence : /{agence}/api/v1/… (ADR-0070). "
                    + "Les points d'entrée du compte prennent un jeton d'accès (Authorization: Bearer) obtenu par /auth/login ou /auth/refresh.";
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
