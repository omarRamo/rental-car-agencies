using CarRental.Application.Customers.Accounts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.CustomerApi;

/// <summary>
/// The API of the mobile application and of the storefronts of an agency (ADR-0070): <c>/{agency}/api/v1/…</c>, on the
/// site of the agency like every public address, so that an unknown, suspended or closed site answers 404 and every
/// read and write works for that agency only. Anonymous reads (site, categories, availability, quotes) are rate limited
/// per address; the account endpoints take the bearer token of a customer of that agency; every answer is JSON, errors
/// are RFC 9457 problem details with the stable business code in <c>code</c>.
/// </summary>
public static partial class CustomerApiEndpoints
{
    /// <summary>The prefix of the API of a site: <c>/{agency}/api/v1</c>.</summary>
    public const string Prefix = PublicSiteAgency.RouteParameterSegment + "/api/v1";

    /// <summary>The OpenAPI document of the API (<c>/api/openapi/customer-api-v1.json</c>).</summary>
    public const string OpenApiDocument = "customer-api-v1";

    public const string OpenApiRoute = "/api/openapi/{documentName}.json";

    public const string CorsPolicy = "customer-api";

    private static readonly string[] Cultures = ["fr", "en"];

    public static IEndpointRouteBuilder MapCustomerApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup(Prefix)
            .WithGroupName(OpenApiDocument)
            .WithTags("Customer API v1")
            .RequireCors(CorsPolicy)
            .AddEndpointFilter(CustomerApiFilters.RequireModuleAsync);

        // Anonymous reads: the site, its fleet, what is available and a locked quote.
        api.MapGet("/site", SiteAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.Search)
            .WithName("CustomerApiSite").WithSummary("The agency of the site: identity, locations, categories, policies.").Produces<CustomerApiSite>();
        api.MapGet("/categories", CategoriesAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.Search)
            .WithName("CustomerApiCategories").WithSummary("The categories of the fleet.").Produces<IReadOnlyList<CustomerApiCategory>>();
        api.MapGet("/availability", PublicAvailabilityApi.GetAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.Search)
            .WithName("CustomerApiAvailability").WithSummary("Available categories and total prices TTC for a pick-up location and dates (Tunis time).");
        api.MapPost("/quotes", PublicQuoteApi.CreateCoreAsync).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(PublicRateLimits.Search)
            .WithName("CustomerApiCreateQuote").WithSummary("Prices a rental and locks the quote for 24 hours.");
        api.MapGet("/quotes/{code}", PublicQuoteApi.GetCoreAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.Search)
            .WithName("CustomerApiGetQuote").WithSummary("A locked quote by its code.");

        // Authentication (anonymous, counted per address: password spraying, code guessing).
        var auth = api.MapGroup("/auth").DisableAntiforgery();
        auth.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.CustomerAuth)
            .WithName("CustomerApiRegister").WithSummary("Opens a customer account and signs it in; a verification code is e-mailed.").Produces<CustomerApiSession>(StatusCodes.Status201Created);
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.CustomerAuth)
            .WithName("CustomerApiLogin").WithSummary("Signs a customer in: access token and refresh token.").Produces<CustomerApiSession>();
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.CustomerAuth)
            .WithName("CustomerApiRefresh").WithSummary("Exchanges a refresh token for new tokens (the old one dies).").Produces<CustomerApiSession>();
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.CustomerAuth)
            .WithName("CustomerApiLogout").WithSummary("Revokes a refresh token (this device).");
        auth.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.CustomerAuth)
            .WithName("CustomerApiForgotPassword").WithSummary("E-mails a reset code when the address has an account (same answer otherwise).");
        auth.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting(PublicRateLimits.CustomerAuth)
            .WithName("CustomerApiResetPassword").WithSummary("A new password with the reset code; every device signs out.");

        // The customer (bearer token of this agency, account open, stamp current).
        var me = api.MapGroup(string.Empty)
            .RequireAuthorization(CustomerApiAuthentication.Policy)
            .RequireRateLimiting(PublicRateLimits.Search)
            .DisableAntiforgery()
            .AddEndpointFilter(CustomerApiFilters.RequireCustomerAsync);
        me.MapGet("/me", MeAsync).WithName("CustomerApiMe").WithSummary("The account of the customer.").Produces<CustomerApiAccount>();
        me.MapPatch("/me", UpdateMeAsync).WithName("CustomerApiUpdateMe").WithSummary("Changes the names, phone or language of the account.").Produces<CustomerApiAccount>();
        me.MapDelete("/me", CloseMeAsync).WithName("CustomerApiCloseMe").WithSummary("Closes the account (password confirmed).");
        me.MapPost("/auth/verify-email", VerifyEmailAsync).WithName("CustomerApiVerifyEmail").WithSummary("The code of the verification e-mail.").Produces<CustomerApiAccount>();
        me.MapPost("/auth/resend-verification", ResendVerificationAsync).RequireRateLimiting(PublicRateLimits.CustomerAuth).WithName("CustomerApiResendVerification").WithSummary("A new verification code.");
        me.MapPost("/auth/change-password", ChangePasswordAsync).RequireRateLimiting(PublicRateLimits.CustomerAuth).WithName("CustomerApiChangePassword").WithSummary("Changes the password; the other devices sign out.").Produces<CustomerApiSession>();
        me.MapPost("/auth/logout-all", LogoutEverywhereAsync).WithName("CustomerApiLogoutAll").WithSummary("Signs every device out.");
        me.MapGet("/me/bookings", BookingsAsync).WithName("CustomerApiBookings").WithSummary("The bookings of the customer, most recent pick-up first.").Produces<IReadOnlyList<CustomerApiBookingSummary>>();
        me.MapGet("/me/bookings/{bookingId:guid}", BookingAsync).WithName("CustomerApiBooking").WithSummary("A booking as the customer follows it.").Produces<CustomerApiBookingDetails>();
        me.MapPost("/me/bookings", CreateBookingAsync).RequireRateLimiting(PublicRateLimits.Submit).WithName("CustomerApiCreateBooking").WithSummary("Sends a booking request (quote, drivers, consents, terms) and starts its guarantee.").Produces<CustomerApiBookingCreated>(StatusCodes.Status201Created);
        me.MapPost("/me/bookings/{bookingId:guid}/documents/{slot}", UploadDocumentAsync).RequireRateLimiting(PublicRateLimits.Upload)
            .WithMetadata(new RequestSizeLimit(PublicForms.UploadRequestLimit)).WithFormOptions(multipartBodyLengthLimit: PublicForms.UploadRequestLimit)
            .WithName("CustomerApiUploadDocument").WithSummary("A photo of a document of a driver (multipart field 'file': JPEG, PNG or PDF, 10 MB).").Produces<CustomerApiUploadReceived>();
        me.MapGet("/me/bookings/{bookingId:guid}/documents/{documentId}", DocumentAsync).WithName("CustomerApiDocument").WithSummary("A document of the booking (contract, invoice), as a file.");
        me.MapPost("/me/bookings/{bookingId:guid}/information-provided", InformationProvidedAsync).RequireRateLimiting(PublicRateLimits.Submit).WithName("CustomerApiInformationProvided").WithSummary("The customer sent what the agent asked for.");
        me.MapPost("/me/bookings/{bookingId:guid}/pay-balance", PayBalanceAsync).RequireRateLimiting(PublicRateLimits.Submit).WithName("CustomerApiPayBalance").WithSummary("The payment page of the bank for the balance.").Produces<CustomerApiPaymentRedirect>();
        me.MapPost("/me/bookings/{bookingId:guid}/cancel", CancelAsync).RequireRateLimiting(PublicRateLimits.Submit).WithName("CustomerApiCancel").WithSummary("Cancels the booking under the policy (fee shown first).").Produces<CustomerApiCancelled>();
        return endpoints;
    }

    /// <summary>The OpenAPI document, at the root of the platform (it describes the API of every site).</summary>
    public static IEndpointRouteBuilder MapCustomerApiDocument(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapOpenApi(OpenApiRoute).AllowAnonymous().RequireRateLimiting(PublicRateLimits.Search).CacheOutput();
        return endpoints;
    }

    // Catalogue

    internal static async Task<Results<Ok<CustomerApiSite>, ProblemHttpResult>> SiteAsync(
        HttpContext context,
        PublicAgencyDirectory directory,
        CultureCatalog cultures,
        IOptions<PublicSiteOptions> site,
        CancellationToken cancellationToken)
    {
        var agencySite = PublicSiteAgency.Require(context);
        var agency = await directory.GetAsync(agencySite, cancellationToken);
        if (agency is null)
        {
            return Unavailable();
        }

        var baseUri = PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
        var requireVerified = await context.RequestServices.GetRequiredService<ISettingsReader>().GetAsync(CustomerAccountSettings.RequireVerifiedEmailToBook, agency.Agency.Id, cancellationToken);
        context.Response.Headers.CacheControl = "public, max-age=300";
        return TypedResults.Ok(new CustomerApiSite(
            agencySite.Slug,
            agency.Agency.Name,
            agency.Agency.City,
            agency.Agency.Address,
            agency.Agency.Phone,
            agency.Agency.Email,
            agency.Agency.Latitude,
            agency.Agency.Longitude,
            PublicSiteAgency.WhatsAppNumber(context, site.Value),
            agency.OpeningHours.ToString(),
            [.. cultures.Enabled],
            Currency.Tnd.Code,
            agency.EurExchangeRate > 0 ? agency.EurExchangeRate : null,
            new CustomerApiPolicies(agency.Policies.DriverMinimumAge, agency.Policies.DriverMinimumLicenseYears, agency.Policies.YoungDriverAgeBelow, requireVerified),
            [.. agency.Locations.Select(PublicLocationDto.From)],
            [.. agency.Categories.Select(c => CustomerApiCategory.From(c, baseUri))],
            Cultures.ToDictionary(c => c, c => PublicUrls.Absolute(baseUri, PublicRoutes.PathFor(agencySite.Slug, PublicPage.Terms, c)).AbsoluteUri),
            Cultures.ToDictionary(c => c, c => PublicUrls.Absolute(baseUri, PublicRoutes.PathFor(agencySite.Slug, PublicPage.Privacy, c)).AbsoluteUri)));
    }

    internal static async Task<Results<Ok<IReadOnlyList<CustomerApiCategory>>, ProblemHttpResult>> CategoriesAsync(
        HttpContext context,
        PublicAgencyDirectory directory,
        IOptions<PublicSiteOptions> site,
        CancellationToken cancellationToken)
    {
        var agency = await directory.GetAsync(PublicSiteAgency.Require(context), cancellationToken);
        if (agency is null)
        {
            return Unavailable();
        }

        var baseUri = PublicUrls.BaseUri(site.Value.BaseUrl, context.Request);
        context.Response.Headers.CacheControl = "public, max-age=300";
        return TypedResults.Ok<IReadOnlyList<CustomerApiCategory>>([.. agency.Categories.Select(c => CustomerApiCategory.From(c, baseUri))]);
    }

    // Authentication

    internal static async Task<IResult> RegisterAsync(CustomerApiRegisterRequest? request, HttpContext context, CustomerTokenIssuer issuer, CancellationToken cancellationToken)
    {
        NoStore(context);
        if (request is null)
        {
            return BodyRequired();
        }

        await using var scope = CustomerApiAuthentication.CreateScope(context);
        var registered = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().RegisterAsync(
            new CustomerRegistrationRequest(request.Email, request.Password, request.FirstName, request.LastName, request.Phone, request.Culture, request.Device),
            cancellationToken);
        return registered.IsSuccess
            ? Results.Created($"{context.Request.Path.Value![..context.Request.Path.Value!.IndexOf("/auth/", StringComparison.Ordinal)]}/me", CustomerApiSession.From(registered.Value, issuer.Issue(registered.Value.Account)))
            : Problem(context, registered.Error);
    }

    internal static async Task<IResult> LoginAsync(CustomerApiLoginRequest? request, HttpContext context, CustomerTokenIssuer issuer, CancellationToken cancellationToken)
    {
        NoStore(context);
        if (request is null)
        {
            return BodyRequired();
        }

        await using var scope = CustomerApiAuthentication.CreateScope(context);
        var signedIn = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().SignInAsync(new CustomerSignInRequest(request.Email, request.Password, request.Device), cancellationToken);
        return signedIn.IsSuccess ? Results.Ok(CustomerApiSession.From(signedIn.Value, issuer.Issue(signedIn.Value.Account))) : Problem(context, signedIn.Error);
    }

    internal static async Task<IResult> RefreshAsync(CustomerApiRefreshRequest? request, HttpContext context, CustomerTokenIssuer issuer, CancellationToken cancellationToken)
    {
        NoStore(context);
        if (request is null)
        {
            return BodyRequired();
        }

        await using var scope = CustomerApiAuthentication.CreateScope(context);
        var refreshed = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().RefreshAsync(request.RefreshToken, request.Device, cancellationToken);
        return refreshed.IsSuccess ? Results.Ok(CustomerApiSession.From(refreshed.Value, issuer.Issue(refreshed.Value.Account))) : Problem(context, refreshed.Error);
    }

    internal static async Task<IResult> LogoutAsync(CustomerApiLogoutRequest? request, HttpContext context, CancellationToken cancellationToken)
    {
        NoStore(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context);
        var signedOut = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().SignOutAsync(request?.RefreshToken, cancellationToken);
        return signedOut.IsSuccess ? Results.NoContent() : Problem(context, signedOut.Error);
    }

    internal static async Task<IResult> ForgotPasswordAsync(CustomerApiForgotPasswordRequest? request, HttpContext context, CancellationToken cancellationToken)
    {
        NoStore(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context);
        var asked = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().ForgotPasswordAsync(request?.Email, cancellationToken);
        return asked.IsSuccess ? Results.Accepted() : Problem(context, asked.Error);
    }

    internal static async Task<IResult> ResetPasswordAsync(CustomerApiResetPasswordRequest? request, HttpContext context, CancellationToken cancellationToken)
    {
        NoStore(context);
        if (request is null)
        {
            return BodyRequired();
        }

        await using var scope = CustomerApiAuthentication.CreateScope(context);
        var reset = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().ResetPasswordAsync(request.Email, request.Code, request.NewPassword, cancellationToken);
        return reset.IsSuccess ? Results.NoContent() : Problem(context, reset.Error);
    }

    // Helpers

    internal static void NoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    internal static ProblemHttpResult Unavailable() =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The agency is not open for online booking.", extensions: new Dictionary<string, object?> { ["code"] = "public.site_unavailable" });

    internal static IResult BodyRequired() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["required"] }, title: "The request is invalid.");

    /// <summary>A business error as problem details: the status from its kind, the stable code and its localized text.</summary>
    internal static IResult Problem(HttpContext context, Error error)
    {
        var status = error.Code switch
        {
            "customers.account.credentials_invalid" or "customers.account.refresh_token_invalid" => StatusCodes.Status401Unauthorized,
            _ => error.Kind switch
            {
                ErrorKind.NotFound => StatusCodes.Status404NotFound,
                ErrorKind.Conflict => StatusCodes.Status409Conflict,
                ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                ErrorKind.Failure => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status422UnprocessableEntity,
            },
        };
        return TypedResults.Problem(
            statusCode: status,
            title: BookingFunnelEndpoints.ErrorText(context.RequestServices, error.Code),
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
