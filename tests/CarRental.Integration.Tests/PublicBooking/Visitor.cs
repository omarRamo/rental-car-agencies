using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// A visitor of the public site over HTTP: one browser (cookies kept, redirects not followed, a phone user agent and a
/// client address of its own), reading the pages and posting their forms with their antiforgery token.
/// </summary>
internal sealed partial class Visitor : IDisposable
{
    /// <summary>A real 1×1 PNG (the upload module turns photos into JPEG; the server checks the content only).</summary>
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>The start of a JPEG file (the server checks the signature).</summary>
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01];

    private Visitor(HttpClient client) => Client = client;

    public HttpClient Client { get; }

    /// <summary>A new browser on <paramref name="factory"/>, from its own client address.</summary>
    public static Visitor Create(CarRentalAppFactory factory, string? clientAddress = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", clientAddress ?? RandomAddress());
        return new Visitor(client);
    }

    /// <summary>A fresh documentation address (198.18.0.0/15), one rate limit partition per visitor.</summary>
    public static string RandomAddress() =>
        string.Create(CultureInfo.InvariantCulture, $"198.{RandomNumberGenerator.GetInt32(18, 20)}.{RandomNumberGenerator.GetInt32(0, 256)}.{RandomNumberGenerator.GetInt32(1, 255)}");

    /// <summary>A short unique suffix of letters only (names accept letters only), for names, e-mails and document numbers.</summary>
    public static string Unique() =>
        new([.. Guid.NewGuid().ToString("N")[..10].Select(c => char.IsAsciiDigit(c) ? (char)('G' + (c - '0')) : char.ToUpperInvariant(c))]);

    public static string Location(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.Location.ShouldNotBeNull($"No redirection ({(int)response.StatusCode}).");
        return Uri.UnescapeDataString(HttpSignIn.PathAndQuery(response.Headers.Location));
    }

    /// <summary>The page at <paramref name="path"/> (status checked), as sent.</summary>
    public async Task<string> PageAsync(string path, CancellationToken cancellationToken, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.ShouldBe(expected, $"GET {path}");
        return html;
    }

    /// <summary>The text of a page, entities decoded and spaces of the culture made plain.</summary>
    public async Task<string> TextAsync(string path, CancellationToken cancellationToken) => Plain(WebUtility.HtmlDecode(await PageAsync(path, cancellationToken)));

    /// <summary>Posts <paramref name="fields"/> to <paramref name="action"/> with the antiforgery token of the page at <paramref name="page"/>.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string page, string action, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        var html = await PageAsync(page, cancellationToken);
        html.ShouldContain("__RequestVerificationToken", customMessage: $"No form on {page}: {MainText(html)}");
        var values = fields.ToList();
        values.Add(new("__RequestVerificationToken", HttpSignIn.AntiforgeryToken(html)));
        using var content = new FormUrlEncodedContent(values);
        return await Client.PostAsync(new Uri(action, UriKind.Relative), content, cancellationToken);
    }

    /// <summary>Posts the form of the page to itself.</summary>
    public Task<HttpResponseMessage> SubmitAsync(string page, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken) =>
        SubmitAsync(page, page, fields, cancellationToken);

    /// <summary>The "Choose" button of a category on the results of <paramref name="search"/>: the token of the funnel session.</summary>
    public async Task<string> ChooseAsync(string search, string category, CancellationToken cancellationToken, string culture = "fr")
    {
        var results = $"/{culture}/{(culture == "en" ? "book" : "reserver")}?{search}";
        var fields = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(search)
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value.ToString()))
            .Append(new("cat", category));
        using var chosen = await SubmitAsync(results, $"/{culture}/reserver/selection", fields, cancellationToken);
        chosen.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var location = Location(chosen);
        var match = StepPath().Match(location);
        match.Success.ShouldBeTrue($"Not a funnel address: {location}");
        return match.Groups["token"].Value;
    }

    /// <summary>A photo for a slot, as the upload module sends it (multipart, JSON answer).</summary>
    public async Task<(HttpStatusCode Status, JsonElement Answer)> UploadAsync(
        string page,
        string action,
        string slot,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var html = await PageAsync(page, cancellationToken);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(HttpSignIn.AntiforgeryToken(html)), "__RequestVerificationToken" },
            { new StringContent(slot), "slot" },
        };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", contentType == "application/pdf" ? "document.pdf" : "photo.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(action, UriKind.Relative)) { Content = form };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }

    /// <summary>The fields of a valid driver of step 2 (a French national with a passport and a licence held for 20 years).</summary>
    public static List<KeyValuePair<string, string>> Driver(string role, string suffix, string? email = null, string? phone = null) =>
    [
        new($"{role}.first_name", "Amel"),
        new($"{role}.last_name", $"Funnel{suffix}"),
        new($"{role}.nationality", "FR"),
        new($"{role}.birth_date", "1984-03-14"),
        new($"{role}.phone", phone ?? RandomPhone()),
        new($"{role}.email", email ?? $"amel.{suffix.ToLowerInvariant()}@example.test"),
        new($"{role}.license_number", $"L{suffix}"),
        new($"{role}.license_country", "FR"),
        new($"{role}.license_issued_on", "2004-06-01"),
        new($"{role}.license_latin", "1"),
        new($"{role}.identity_number", $"P{suffix}"),
        new($"{role}.identity_expires_on", "2035-01-31"),
    ];

    /// <summary>A French mobile number of its own (deduplication by phone and name stays per test).</summary>
    public static string RandomPhone() => string.Create(CultureInfo.InvariantCulture, $"+336{RandomNumberGenerator.GetInt32(10_000_000, 99_999_999)}");

    /// <summary>The text of the main element of a page (diagnostics of a failed step).</summary>
    public static string MainText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var start = html.IndexOf("<main", StringComparison.Ordinal);
        var end = html.IndexOf("</main>", StringComparison.Ordinal);
        var main = start >= 0 && end > start ? html[start..end] : html;
        return Plain(WebUtility.HtmlDecode(Tags().Replace(main, " "))).Trim();
    }

    public void Dispose() => Client.Dispose();

    [GeneratedRegex("<[^>]+>|\\s{2,}")]
    private static partial Regex Tags();

    [GeneratedRegex("^/(fr|en)/(reserver|book)/(?<token>[A-Za-z0-9_-]{43})/options$")]
    private static partial Regex StepPath();
}
