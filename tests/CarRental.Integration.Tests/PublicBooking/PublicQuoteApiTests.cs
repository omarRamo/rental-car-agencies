using System.Net;
using System.Text.Json;
using CarRental.Testing;
using static CarRental.Integration.Tests.Public.PublicTestData;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The public quote API (ADR-0049): an API key of the configuration, a locked quote priced by the engine and the deep link
/// that books it (<c>devis=CODE</c>, reused by the funnel when it matches the search), read back by its code.
/// </summary>
public sealed class PublicQuoteApiTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_quote_api_needs_its_key_and_locks_a_quote_that_the_deep_link_books()
    {
        using var visitor = Create(host.Factory);
        var body = JsonContent(new { pickupLocation = "TUN", pickup = LocalDate(121), @return = LocalDate(124), category = "ECMR", source = "whatsapp" });

        using var refused = await visitor.Client.PostAsync(new Uri("/agence-demo-tunis/api/public/quotes", UriKind.Relative), body, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        visitor.Client.DefaultRequestHeaders.Add("X-Api-Key", CarRentalAppFactory.QuoteApiKey);
        using var created = await visitor.Client.PostAsync(new Uri("/agence-demo-tunis/api/public/quotes", UriKind.Relative), JsonContent(new { pickupLocation = "TUN", pickup = LocalDate(121), @return = LocalDate(124), category = "ECMR", source = "whatsapp" }), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var quote = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement;
        var code = quote.GetProperty("code").GetString()!;
        quote.GetProperty("channel").GetString().ShouldBe("whatsapp");
        quote.GetProperty("expired").GetBoolean().ShouldBeFalse();
        quote.GetProperty("totalInclVat").GetDecimal().ShouldBeGreaterThan(0m);
        var link = quote.GetProperty("bookingUrls").GetProperty("fr").GetString()!;
        link.ShouldContain($"devis={code}");
        link.ShouldContain("src=whatsapp");
        created.Headers.Location!.OriginalString.ShouldBe($"/agence-demo-tunis/api/public/quotes/{code}");

        using var read = await visitor.Client.GetAsync(new Uri($"/agence-demo-tunis/api/public/quotes/{code}", UriKind.Relative), Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("category").GetString().ShouldBe("ECMR");
        using var unknown = await visitor.Client.GetAsync(new Uri("/agence-demo-tunis/api/public/quotes/ZZZZZZ", UriKind.Relative), Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The deep link of the answer: choosing the category there carries on with the same locked quote.
        var deepLink = new Uri(link).PathAndQuery;
        var token = await visitor.ChooseAsync(deepLink[(deepLink.IndexOf('?', StringComparison.Ordinal) + 1)..], "ECMR", Ct);
        (await visitor.TextAsync($"/agence-demo-tunis/fr/reserver/{token}/options", Ct)).ShouldContain($"Devis {code}");
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
