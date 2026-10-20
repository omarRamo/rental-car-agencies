using System.Net;
using System.Text.RegularExpressions;
using CarRental.Testing;

namespace CarRental.Integration.Tests.Support;

/// <summary>Drives the static-SSR account forms over HTTP (antiforgery token + Blazor form handler name).</summary>
public static partial class HttpSignIn
{
    /// <summary>Password step; returns the redirect target (<c>/account/login-with-2fa…</c>, <c>/admin</c>, lockout…).</summary>
    public static async Task<Uri> SubmitPasswordAsync(HttpClient client, string email, string password, string returnUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var loginUrl = $"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        var page = await client.GetStringAsync(new Uri(loginUrl, UriKind.Relative), cancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(page),
            ["_handler"] = "login",
            ["Input.Email"] = email,
            ["Input.Password"] = password,
        });

        var response = await client.PostAsync(new Uri(loginUrl, UriKind.Relative), form, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(cancellationToken));
        return new Uri(PathAndQuery(response.Headers.Location!), UriKind.Relative);
    }

    /// <summary>Authenticator step at <paramref name="twoFactorUrl"/>; returns the response (302 on success).</summary>
    public static async Task<HttpResponseMessage> SubmitAuthenticatorCodeAsync(HttpClient client, Uri twoFactorUrl, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var page = await client.GetStringAsync(twoFactorUrl, cancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(page),
            ["_handler"] = "login-with-2fa",
            ["Input.Code"] = code,
        });

        return await client.PostAsync(twoFactorUrl, form, cancellationToken);
    }

    /// <summary>Full sign-in of a demo account with password and TOTP; asserts the final redirect to <paramref name="returnUrl"/>.</summary>
    public static async Task SignInWithTwoFactorAsync(HttpClient client, string email, string returnUrl, CancellationToken cancellationToken)
    {
        var twoFactor = await SubmitPasswordAsync(client, email, TestAccounts.Password, returnUrl, cancellationToken);
        twoFactor.OriginalString.ShouldStartWith("/account/login-with-2fa");

        var response = await SubmitAuthenticatorCodeAsync(client, twoFactor, Totp.Compute(TestAccounts.AuthenticatorKey), cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync(cancellationToken));
        PathAndQuery(response.Headers.Location!).ShouldBe(returnUrl);
    }

    /// <summary>Path and query of a redirect (Blazor emits absolute locations, the cookie handler relative ones).</summary>
    public static string PathAndQuery(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    public static string AntiforgeryToken(string html)
    {
        var match = AntiforgeryPattern().Match(html);
        match.Success.ShouldBeTrue("The page contains no antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"")]
    private static partial Regex AntiforgeryPattern();
}
