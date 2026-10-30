using System.Security.Claims;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Accounts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Hosting;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CarRental.Web.Endpoints.CustomerApi;

/// <summary>
/// The authentication of the customers of the API (ADR-0070): a JWT access token (HMAC-SHA256, 15 minutes) issued by
/// the platform after a sign-in or a refresh, carried as <c>Authorization: Bearer</c>. The cookies of the staff are never
/// accepted there (no CSRF surface), and a bearer token opens nothing of the back office. The token names the account,
/// its agency and its security stamp: a request on the site of another agency, or after a password change, is refused.
/// </summary>
public static class CustomerApiAuthentication
{
    /// <summary>The authentication scheme of the API.</summary>
    public const string Scheme = "CustomerBearer";

    /// <summary>The authorization policy of the authenticated endpoints: this scheme, an authenticated customer.</summary>
    public const string Policy = "CustomerApi";

    public const string KindClaim = "kind";

    public const string KindValue = "customer";

    public const string AgencyClaim = "agency";

    public const string StampClaim = "stamp";

    public const string EmailClaim = "email";

    public const string NameClaim = "name";

    /// <summary>The actor of the writes of the API: the system working for the agency of the site, the account recorded as the user.</summary>
    public const string Actor = "system:customer-api";

    private const string ItemKey = "carrental.customer-api-identity";

    /// <summary>The customer of this request (set by <see cref="CustomerApiFilters.RequireCustomerAsync"/>).</summary>
    public static CustomerIdentity Require(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) && value is CustomerIdentity identity
            ? identity
            : throw new InvalidOperationException("This endpoint runs after the customer filter.");
    }

    internal static void Set(HttpContext context, CustomerIdentity identity) => context.Items[ItemKey] = identity;

    /// <summary>
    /// A scope acting for the agency of the site of the request (the system actor of the API, the account as the user,
    /// the client address and the correlation of the request). Outside a site: a visitor of no agency, who sees nothing.
    /// </summary>
    public static AsyncServiceScope CreateScope(HttpContext context, CustomerIdentity? customer = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = context.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var ip = context.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(context);
        var correlationId = CorrelationIds.Current(context);
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(
            PublicSiteAgency.Of(context) is { } agencyId
                ? UserContextSnapshot.System(Actor, correlationId, agencyId) with { IpAddress = ip, UserId = customer?.AccountId }
                : UserContextSnapshot.Anonymous(ip, correlationId));
        return scope;
    }
}

/// <summary>The customer behind an authenticated request of the API.</summary>
public sealed record CustomerIdentity(Guid AccountId, Guid AgencyId, string Email, string Name, string? Phone, string Culture, bool EmailVerified);

/// <summary>An access token and when it expires.</summary>
public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>Issues the access tokens of the customers (ADR-0070).</summary>
public sealed class CustomerTokenIssuer(IOptions<CustomerApiOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>A token for <paramref name="account"/>: subject, agency, e-mail, name and security stamp, valid <c>CustomerApi:AccessTokenLifetime</c>.</summary>
    public IssuedAccessToken Issue(CustomerAccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var expires = now + settings.AccessTokenLifetime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Jwt.Issuer,
            Audience = settings.Jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(settings.Jwt.KeyBytes), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = account.Id.ToString("D"),
                [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString("N"),
                [CustomerApiAuthentication.KindClaim] = CustomerApiAuthentication.KindValue,
                [CustomerApiAuthentication.AgencyClaim] = account.AgencyId.ToString("D"),
                [CustomerApiAuthentication.EmailClaim] = account.Email,
                [CustomerApiAuthentication.NameClaim] = $"{account.FirstName} {account.LastName}",
                [CustomerApiAuthentication.StampClaim] = account.SecurityStamp,
            },
        });
        return new IssuedAccessToken(token, expires);
    }

    /// <summary>The validation parameters of the bearer scheme: same issuer, audience and key, 30 seconds of clock skew.</summary>
    public static TokenValidationParameters ValidationParameters(CustomerApiOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TokenValidationParameters
        {
            ValidIssuer = settings.Jwt.Issuer,
            ValidAudience = settings.Jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(settings.Jwt.KeyBytes),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = CustomerApiAuthentication.NameClaim,
            RoleClaimType = "role",
        };
    }
}

/// <summary>The endpoint filters of the API: the module of the agency, the customer of the token.</summary>
public static class CustomerApiFilters
{
    /// <summary>The API of a site answers 404 when the agency has the module off (the site itself keeps working).</summary>
    public static async ValueTask<object?> RequireModuleAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var site = PublicSiteAgency.SiteOf(http);
        if (site is null || !site.HasModule(AgencyModules.CustomerApi))
        {
            return TypedResults.NotFound();
        }

        return await next(context);
    }

    /// <summary>
    /// The customer of the bearer token: its agency must be the agency of the site (a token of another agency is as good
    /// as none), its account must still be open and its security stamp current (a password change ends every session).
    /// </summary>
    public static async ValueTask<object?> RequireCustomerAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var principal = http.User;
        var site = PublicSiteAgency.Require(http);
        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var accountId)
            || !Guid.TryParse(principal.FindFirstValue(CustomerApiAuthentication.AgencyClaim), out var agencyId)
            || agencyId != site.Id
            || principal.FindFirstValue(CustomerApiAuthentication.StampClaim) is not { Length: > 0 } stamp)
        {
            return Unauthorized();
        }

        await using var scope = CustomerApiAuthentication.CreateScope(http);
        var account = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().GetAsync(accountId, http.RequestAborted);
        if (account is null || !string.Equals(account.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        CustomerApiAuthentication.Set(http, new CustomerIdentity(account.Id, account.AgencyId, account.Email, $"{account.FirstName} {account.LastName}", account.Phone, account.Culture, account.EmailVerified));
        http.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static ProblemHttpResult Unauthorized() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The access token is not valid for this agency: sign in again.", extensions: new Dictionary<string, object?> { ["code"] = "customers.account.token_invalid" });
}

/// <summary>Reads the agency directory for the filters (kept apart so that the filters stay static delegates).</summary>
internal static class CustomerApiModules
{
    public static Task<bool> IsOpenAsync(HttpContext context, Guid agencyId) =>
        context.RequestServices.GetRequiredService<IAgencyDirectory>().HasModuleAsync(agencyId, AgencyModules.CustomerApi, context.RequestAborted);
}
