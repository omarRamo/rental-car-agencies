using System.Net;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using CarRental.Web.Endpoints.PublicSite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.Public;

/// <summary>The results page records the search and results steps of the funnel, anonymously.</summary>
public sealed class FunnelTrackingTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> SearchAsync(string campaign, string extra = "", Action<HttpRequestMessage>? configure = null)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/fr/reserver?lieu=TUN&depart={LocalDate(60)}&retour={LocalDate(63)}&cat=ECMR&src=whatsapp&utm_source=meta&utm_campaign={campaign}{extra}", UriKind.Relative));
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        configure?.Invoke(request);
        var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response;
    }

    private async Task<List<FunnelEvent>> EventsAsync(string campaign)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<FunnelEvent>()
            .AsNoTracking()
            .Where(e => e.UtmCampaign == campaign)
            .OrderBy(e => e.Step)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task A_search_with_results_records_both_steps_for_one_anonymous_visit()
    {
        var campaign = $"funnel-{Guid.NewGuid():N}";

        var response = await SearchAsync(campaign);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(VisitCookie.Name + "=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=lax", Case.Insensitive);
        var visitId = Guid.ParseExact(cookie[(VisitCookie.Name.Length + 1)..cookie.IndexOf(';', StringComparison.Ordinal)], "N");
        var events = await EventsAsync(campaign);
        events.Select(e => e.Step).ShouldBe([FunnelStep.Search, FunnelStep.Results]);
        events.ShouldAllBe(e => e.VisitId == visitId);
        events.ShouldAllBe(e => e.Channel == SalesChannel.WhatsApp && e.Source == "whatsapp" && e.UtmSource == "meta" && e.Culture == "fr");
        events.ShouldAllBe(e => e.CategoryCode != null && e.CategoryCode.Value == "ECMR");
        events.ShouldAllBe(e => e.BookingId == null);
    }

    [Fact]
    public async Task A_returning_visit_keeps_its_identifier()
    {
        var campaign = $"funnel-{Guid.NewGuid():N}";
        var visitId = Guid.NewGuid();

        await SearchAsync(campaign, configure: request => request.Headers.Add("Cookie", $"{VisitCookie.Name}={visitId:N}"));

        (await EventsAsync(campaign)).ShouldAllBe(e => e.VisitId == visitId);
    }

    [Fact]
    public async Task An_opt_out_signal_means_no_cookie_and_no_visit_identifier()
    {
        var campaign = $"funnel-{Guid.NewGuid():N}";

        var response = await SearchAsync(campaign, configure: request => request.Headers.Add("Sec-GPC", "1"));

        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).ShouldNotContain(c => c.StartsWith(VisitCookie.Name, StringComparison.Ordinal));
        var events = await EventsAsync(campaign);
        events.Count.ShouldBe(2);
        events.ShouldAllBe(e => e.VisitId == null);
    }

    [Fact]
    public async Task Robots_and_invalid_searches_are_not_measured()
    {
        var robotCampaign = $"funnel-{Guid.NewGuid():N}";
        var invalidCampaign = $"funnel-{Guid.NewGuid():N}";

        await SearchAsync(robotCampaign, configure: request =>
        {
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");
        });
        using var client = host.CreateClient();
        await client.GetAsync(new Uri($"/fr/reserver?lieu=TUN&depart=2020-01-01T10:00&retour=2020-01-03T10:00&utm_campaign={invalidCampaign}", UriKind.Relative), Ct);

        (await EventsAsync(robotCampaign)).ShouldBeEmpty();
        (await EventsAsync(invalidCampaign)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_search_without_any_result_records_the_search_only()
    {
        var campaign = $"funnel-{Guid.NewGuid():N}";

        await SearchAsync(campaign, extra: "&boite=auto&places=9");

        (await EventsAsync(campaign)).Select(e => e.Step).ShouldBe([FunnelStep.Search]);
    }
}
