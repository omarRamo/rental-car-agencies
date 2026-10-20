using Microsoft.AspNetCore.Components;

namespace CarRental.Web.Components.Account;

/// <summary>Redirects of the static account pages, with an optional one-shot status message (cookie, 5 seconds).</summary>
internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "carrental.status";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(5),
    };

    private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    public void RedirectTo(string? uri)
    {
        uri ??= string.Empty;

        // Prevent open redirects.
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative))
        {
            uri = navigationManager.ToBaseRelativePath(uri);
        }

        navigationManager.NavigateTo(uri);
    }

    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        RedirectTo(navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters));
    }

    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    public void RedirectToCurrentPage() => RedirectTo(CurrentPath);

    public void RedirectToCurrentPageWithStatus(string message, HttpContext context) => RedirectToWithStatus(CurrentPath, message, context);

    public void RedirectToInvalidUser(HttpContext context) => RedirectTo("account/invalid-user");
}
