using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Referential.Contracts;
using CarRental.Web.Components.Dev;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints;

/// <summary>
/// HTTP side of Clic to Pay (ADR-0038, ADR-0039). The return and failure addresses (anonymous) only identify the order:
/// the status is always read server to server, then the browser is sent to the local path recorded when the payment
/// started (never a path from the query: no open redirect). In Development and Testing, with the simulated gateway, the
/// payment page of the fake (<c>/dev/clictopay/pay/{orderId}</c>) and a page to start a payment (<c>/dev/clictopay/start</c>)
/// are mapped too; they do not exist anywhere else.
/// </summary>
public static class ClicToPayEndpoints
{
    public const string ReturnPath = "/payments/clictopay/return";
    public const string FailPath = "/payments/clictopay/fail";
    public const string DevStartPath = "/dev/clictopay/start";

    /// <summary>Longest query value read from the gateway redirect (order number: 32, gateway order id: 36 in BPC).</summary>
    private const int MaxIdentifierLength = 100;

    /// <summary>
    /// With the real gateway, relative return addresses are completed with <c>PublicSite:BaseUrl</c> (the gateway needs
    /// absolute addresses; the options validation then checks them).
    /// </summary>
    public static IServiceCollection AddClicToPayWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ClicToPayOptions>().PostConfigure<IOptions<PublicSiteOptions>>((options, site) =>
        {
            if (!options.Enabled || options.UseFake || site.Value.BaseUrl is not { IsAbsoluteUri: true } baseUrl)
            {
                return;
            }

            options.ReturnUrl = Absolute(baseUrl, options.ReturnUrl);
            options.FailUrl = Absolute(baseUrl, options.FailUrl);
        });
        return services;
    }

    public static IEndpointRouteBuilder MapClicToPayEndpoints(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(environment);
        endpoints.MapGet(ReturnPath, (HttpContext context, IOnlinePayments payments, CancellationToken cancellationToken) =>
                ReturnAsync(context, payments, failed: false, cancellationToken))
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(FailPath, (HttpContext context, IOnlinePayments payments, CancellationToken cancellationToken) =>
                ReturnAsync(context, payments, failed: true, cancellationToken))
            .AllowAnonymous()
            .ExcludeFromDescription();

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<ClicToPayOptions>>().Value;
        if (options is { Enabled: true, UseFake: true } && (environment.IsDevelopment() || environment.IsEnvironment(WebEnvironments.Testing)))
        {
            MapDevelopmentPages(endpoints);
        }

        return endpoints;
    }

    /// <summary>
    /// The browser came back from the payment page. Only <c>order</c> (our order number) and <c>orderId</c> (gateway order
    /// id) are read, to identify the order; unknown or tampered values lead to the home page.
    /// </summary>
    private static async Task<IResult> ReturnAsync(HttpContext context, IOnlinePayments payments, bool failed, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var order = Identifier(context.Request.Query[ClicToPayUrls.OrderParameter]);
        var gatewayOrderId = Identifier(context.Request.Query[ClicToPayUrls.GatewayOrderIdParameter]);
        if (order is null && gatewayOrderId is null)
        {
            return Results.LocalRedirect("/");
        }

        var handled = await payments.HandleReturnAsync(new OnlinePaymentReturnRequest(order, gatewayOrderId, failed), cancellationToken);
        return Results.LocalRedirect(handled.IsSuccess && LocalUrls.IsLocal(handled.Value.RedirectPath) ? handled.Value.RedirectPath : "/");
    }

    /// <summary>
    /// Whether <paramref name="url"/> is an absolute web address (http or https). A local path such as <c>/dev/…</c> is not,
    /// although <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> takes it for an absolute file address on Linux.
    /// </summary>
    public static bool IsWebAddress([NotNullWhen(true)] string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string? Identifier(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 1 && values[0] is { Length: > 0 and <= MaxIdentifierLength } value ? value : null;

    /// <summary>A relative return address under the public base address, its path included (<c>https://host/app/</c>).</summary>
    private static string Absolute(Uri baseUrl, string url)
    {
        if (IsWebAddress(url))
        {
            return url;
        }

        var root = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        return new Uri(root, url.TrimStart('/')).ToString();
    }

    private static void MapDevelopmentPages(IEndpointRouteBuilder endpoints)
    {
        var dev = endpoints.MapGroup("/dev/clictopay").ExcludeFromDescription().AllowAnonymous();

        dev.MapGet("/pay/{orderId}", (string orderId, FakeClicToPay fake, IAntiforgery antiforgery, HttpContext context) =>
        {
            if (fake.FindOrder(orderId) is not { } order)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "no-store";
            return new RazorComponentResult<FakeClicToPayPage>(new { Order = order, Antiforgery = antiforgery.GetAndStoreTokens(context) });
        });

        // The forms validate their antiforgery token explicitly (a forged post is a plain 400, never an exception).
        dev.MapPost("/pay/{orderId}", async (string orderId, HttpContext context, IAntiforgery antiforgery, FakeClicToPay fake) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var next = form["action"].ToString() switch
            {
                FakeClicToPayPage.PayAction => fake.Pay(orderId),
                FakeClicToPayPage.DeclineAction => fake.Decline(orderId),
                _ => fake.Abandon(orderId),
            };
            return next.IsSuccess ? Results.Redirect(next.Value) : Results.Conflict();
        }).DisableAntiforgery();

        dev.MapGet("/start", async (IReferentialQueries referential, IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken) =>
        {
            var agencies = (await referential.GetAgenciesAsync(cancellationToken)).Where(a => a.IsActive).ToList();
            return new RazorComponentResult<ClicToPayStartPage>(new { Agencies = agencies, Antiforgery = antiforgery.GetAndStoreTokens(context) });
        });

        dev.MapPost("/start", async (IOnlinePayments payments, IReferentialQueries referential, IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var culture = form["Culture"].ToString() is "en" ? "en" : CultureCatalog.Default;
            var amount = decimal.TryParse(form["Amount"].ToString().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;
            var request = new OnlinePaymentRequest(
                Guid.TryParse(form["BookingId"].ToString(), out var booking) && booking != Guid.Empty ? booking : Guid.CreateVersion7(),
                Guid.TryParse(form["AgencyId"].ToString(), out var agency) ? agency : Guid.Empty,
                amount,
                culture,
                PaymentResultPaths.For(culture),
                PaymentResultPaths.For(culture),
                "DEV " + DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture));
            var started = form["Flow"].ToString() == ClicToPayStartPage.OneStepFlow
                ? await payments.StartPaymentAsync(request, cancellationToken)
                : await payments.StartPreAuthorizationAsync(request, cancellationToken);
            if (started.IsSuccess)
            {
                return Results.Redirect(started.Value.RedirectUrl);
            }

            var agencies = (await referential.GetAgenciesAsync(cancellationToken)).Where(a => a.IsActive).ToList();
            return new RazorComponentResult<ClicToPayStartPage>(new
            {
                Agencies = agencies,
                Antiforgery = antiforgery.GetAndStoreTokens(context),
                ErrorCode = started.Error.Code,
            });
        }).DisableAntiforgery();
    }
}

/// <summary>Local paths of the public page that shows the outcome of an online payment (payment links, development pages).</summary>
public static class PaymentResultPaths
{
    /// <summary>The result page of the payment in <paramref name="culture"/>, with the payment id placeholder of the online payments.</summary>
    public static string For(string culture) =>
        culture == "en"
            ? $"/en/payment/{OnlinePaymentRequest.PaymentIdPlaceholder}"
            : $"/{culture}/paiement/{OnlinePaymentRequest.PaymentIdPlaceholder}";
}
