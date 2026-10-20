using System.Net;
using System.Text;
using CarRental.Application.Common.Files;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests;

public sealed class StorageAndWebTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Files_are_stored_privately_and_shared_with_short_lived_links()
    {
        var path = $"tests/{Guid.NewGuid():N}/document.txt";
        await using var scope = host.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("contenu privé"));

        var stored = await storage.UploadAsync(FileContainers.Documents, path, content, "text/plain", Ct);
        var link = await storage.GetTemporaryReadUriAsync(FileContainers.Documents, path, TimeSpan.FromMinutes(5), Ct);
        using var http = new HttpClient();
        var anonymous = await http.GetAsync(new Uri(link.GetLeftPart(UriPartial.Path)), Ct);
        var viaLink = await http.GetStringAsync(link, Ct);
        await using (var download = await storage.OpenReadAsync(FileContainers.Documents, path, Ct))
        {
            download.ShouldNotBeNull();
            download.ContentType.ShouldBe("text/plain");
            using var reader = new StreamReader(download.Content);
            (await reader.ReadToEndAsync(Ct)).ShouldBe("contenu privé");
        }

        stored.Length.ShouldBe(Encoding.UTF8.GetByteCount("contenu privé"));
        anonymous.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        viaLink.ShouldBe("contenu privé");
        (await storage.ExistsAsync(FileContainers.Documents, path, Ct)).ShouldBeTrue();
        (await storage.DeleteAsync(FileContainers.Documents, path, Ct)).ShouldBeTrue();
        (await storage.OpenReadAsync(FileContainers.Documents, path, Ct)).ShouldBeNull();
        (await storage.DeleteAsync(FileContainers.Documents, path, Ct)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("")]
    public async Task Unsafe_paths_are_rejected(string path)
    {
        await using var scope = host.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        await Should.ThrowAsync<ArgumentException>(() => storage.ExistsAsync(FileContainers.Documents, path, Ct));
    }

    [Fact]
    public async Task Temporary_links_last_one_hour_at_most()
    {
        await using var scope = host.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => storage.GetTemporaryReadUriAsync(FileContainers.Documents, "x.txt", TimeSpan.FromHours(2), Ct));
    }

    [Theory]
    [InlineData("/fr", "fr", "Louez votre voiture en Tunisie")]
    [InlineData("/en", "en", "Rent your car in Tunisia")]
    public async Task Public_pages_are_rendered_in_the_culture_of_the_url(string path, string culture, string heading)
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain($"<html lang=\"{culture}\" dir=\"ltr\">");
        html.ShouldContain(heading);
        html.ShouldContain("hreflang=\"x-default\"");
        html.ShouldContain("wa.me/21600000000");
        html.ShouldNotContain("MudBlazor");
        response.Content.Headers.ContentLanguage.ShouldContain(culture);
    }

    [Theory]
    [InlineData("fr-FR,fr;q=0.9", "/fr")]
    [InlineData("en-GB,en;q=0.8", "/en")]
    [InlineData("de-DE", "/fr")]
    public async Task The_root_redirects_to_the_preferred_culture(string acceptLanguage, string expected)
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Language", acceptLanguage);

        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe(expected);
    }

    [Theory]
    [InlineData("/de")]
    [InlineData("/ar")]
    [InlineData("/does/not/exist")]
    public async Task Unknown_cultures_and_pages_are_not_found(string path)
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_culture_cookie_endpoint_only_accepts_local_return_urls()
    {
        using var client = host.CreateClient();

        var local = await client.GetAsync(new Uri("/culture/set?culture=en&returnUrl=%2Faccount%2Flogin", UriKind.Relative), Ct);
        var external = await client.GetAsync(new Uri("/culture/set?culture=en&returnUrl=https%3A%2F%2Fevil.example", UriKind.Relative), Ct);
        var unknown = await client.GetAsync(new Uri("/culture/set?culture=de&returnUrl=%2F", UriKind.Relative), Ct);
        var login = await client.GetStringAsync(new Uri("/account/login", UriKind.Relative), Ct);

        local.Headers.Location!.OriginalString.ShouldBe("/account/login");
        external.Headers.Location!.OriginalString.ShouldBe("/");
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        login.ShouldContain("Sign in to the staff area");
    }

    [Fact]
    public async Task Security_headers_protect_every_area()
    {
        using var client = host.CreateClient();

        var home = await client.GetAsync(new Uri("/fr", UriKind.Relative), Ct);
        var login = await client.GetAsync(new Uri("/account/login", UriKind.Relative), Ct);

        Header(home, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(home, "X-Frame-Options").ShouldBe("DENY");
        Header(home, "Referrer-Policy").ShouldBe("strict-origin-when-cross-origin");
        var csp = Header(home, "Content-Security-Policy");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("script-src 'self'");
        csp.ShouldNotContain("unsafe-inline");
        Header(login, "Cache-Control").ShouldContain("no-store");
        home.Headers.Contains("Server").ShouldBeFalse();
    }

    [Theory]
    [InlineData("app-carrental-prod.azurewebsites.net", "/fr", 200)]
    [InlineData("www.example.tn", "/fr", 200)]
    [InlineData("localhost", "/fr", 200)]
    [InlineData("attacker.example", "/fr", 400)]
    [InlineData("azurewebsites.net", "/fr", 400)]
    [InlineData("attacker.example", "/health/ready", 200)]
    [InlineData("attacker.example", "/robots933456.txt", 200)]
    public async Task Host_filtering_accepts_configured_hosts_and_never_blocks_probes(string hostName, string path, int expectedStatus)
    {
        // The test host itself accepts every host; the production patterns are checked on the middleware directly.
        var settings = Microsoft.Extensions.Options.Options.Create(new CarRental.Web.Hosting.HostFilteringSettings
        {
            AllowedHosts = "*.azurewebsites.net;localhost;127.0.0.1",
            CustomDomains = "example.tn;www.example.tn",
        });
        var middleware = new CarRental.Web.Hosting.HostFilteringMiddleware(
            context =>
            {
                context.Response.StatusCode = 200;
                return Task.CompletedTask;
            },
            settings);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString(hostName);
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(expectedStatus);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : string.Empty;
}
