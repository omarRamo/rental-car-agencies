using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Search;
using Microsoft.AspNetCore.Antiforgery;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// TODO(2C): placeholder of the "Choose" action of the results page; package 2C replaces this file with the real
/// handler: create the draft booking and the locked quote (24 h), then redirect to step 1 (options).
/// </summary>
/// <remarks>
/// <para>Contract: <c>POST /{culture}/reserver/selection</c> (<see cref="PublicRoutes.SelectionRouteTemplate"/>), form
/// fields named like the deep link (<see cref="SearchParameters"/>): <c>lieu</c>, <c>retour_lieu</c> (optional),
/// <c>depart</c>, <c>retour</c> (Tunis time, <c>yyyy-MM-ddTHH:mm</c>), <c>cat</c> (ACRISS code of the chosen category),
/// <c>devis</c> (locked quote code, optional), <c>src</c> and <c>utm_*</c> (optional), plus <c>boite</c>, <c>places</c>
/// and <c>tri</c> to come back to the same results, and the antiforgery field <c>__RequestVerificationToken</c>.
/// Parse them with <see cref="SearchQuery.Parse"/>, validate with <see cref="SearchValidator"/> (never trust the posted
/// values), and record the <c>options</c> step with <see cref="FunnelTracker"/> on the next page.</para>
/// <para>The antiforgery middleware validates the token (endpoint metadata) and the handler refuses an invalid one with
/// a 400 page before reading the form (reading it would throw); rate limit <see cref="PublicRateLimits.Search"/> for now.</para>
/// </remarks>
public static class BookingSelectionEndpoint
{
    public static IEndpointRouteBuilder MapBookingSelection(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(PublicRoutes.SelectionRouteTemplate, HandleAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AllowAnonymous()
            .RequireRateLimiting(PublicRateLimits.Search)
            .ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>Back to the results of the same search, the chosen category first, with the "booking opens soon" notice.</summary>
    internal static async Task<IResult> HandleAsync(string culture, HttpContext context)
    {
        if (context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false } || !context.Request.HasFormContentType)
        {
            // A page left open too long or a forged post: say so (the body also stops the status page re-execution).
            await PublicErrorPage.WriteAsync(context, StatusCodes.Status400BadRequest, "FormExpired.Title", "FormExpired.Message", context.RequestAborted);
            return Results.Empty;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var parameters = SearchQuery.Parse(form).ToParameters()
            .Append(new KeyValuePair<string, string?>(SearchParameters.Info, SearchQuery.SoonInfo));
        context.Response.Headers.Location = PublicRoutes.PathFor(PublicPage.Results, culture) + QueryString.Create(parameters).ToUriComponent();
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }
}
