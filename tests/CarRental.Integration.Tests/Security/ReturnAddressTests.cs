using System.Net;
using CarRental.Integration.Tests.Support;

namespace CarRental.Integration.Tests.Security;

/// <summary>
/// The language switcher is anonymous and never rate limited: a return address that the framework refuses to redirect
/// to (a control character, which a browser would drop to make <c>//evil.example</c>) must lead home, never to an
/// unhandled exception (a 500 per request, an error log and an exception alert for any visitor).
/// </summary>
public sealed class ReturnAddressTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/%09/evil.example")]
    [InlineData("/%0D%0A/evil.example")]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example/")]
    public async Task The_language_switcher_sends_any_foreign_return_address_home(string returnUrl)
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri($"/culture/set?culture=en&returnUrl={returnUrl}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldBe("/");
    }

    [Fact]
    public async Task The_language_switcher_goes_back_to_a_page_of_the_site()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/culture/set?culture=en&returnUrl=/en/book", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldBe("/en/book");
    }
}
