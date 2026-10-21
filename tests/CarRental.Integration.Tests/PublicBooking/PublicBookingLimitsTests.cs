using System.Net;
using CarRental.Integration.Tests.Public;
using CarRental.Testing;
using static CarRental.Integration.Tests.Public.PublicTestData;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The abuse limits of the booking funnel and of "Ma réservation" (ADR-0037, ADR-0049, ADR-0051) on the dedicated host of
/// the public scenarios (3 requests per window and per client): choices and access requests beyond the submission limit
/// get a 429, and a refused anti-robot check sends no access e-mail.
/// </summary>
[Collection(PublicScenarioCollection.Name)]
public sealed class PublicBookingLimitsTests(PublicScenarioHost scenario)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Choosing_categories_beyond_the_submission_limit_gets_a_429()
    {
        using var visitor = Create(scenario.Factory);
        var token = Support.HttpSignIn.AntiforgeryToken(await visitor.PageAsync($"/fr/reserver?lieu=TUN&depart={LocalDate(116)}&retour={LocalDate(119)}", Ct));

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt <= PublicScenarioHost.RateLimit; attempt++)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["lieu"] = "TUN",
                ["depart"] = LocalDate(116),
                ["retour"] = LocalDate(119),
                ["cat"] = "ECMR",
                ["__RequestVerificationToken"] = token,
            });
            using var response = await visitor.Client.PostAsync(new Uri("/fr/reserver/selection", UriKind.Relative), form, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(PublicScenarioHost.RateLimit).ShouldAllBe(s => s == HttpStatusCode.SeeOther);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Access_requests_beyond_the_submission_limit_get_a_429_and_a_refused_robot_check_sends_nothing()
    {
        using var visitor = Create(scenario.Factory);
        var page = await visitor.PageAsync("/fr/ma-reservation", Ct);
        var token = Support.HttpSignIn.AntiforgeryToken(page);
        var email = $"robot.{Unique().ToLowerInvariant()}@example.test";

        var statuses = new List<HttpStatusCode>();
        string? robotLocation = null;
        for (var attempt = 0; attempt <= PublicScenarioHost.RateLimit; attempt++)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["numero"] = "LOC-2099-000001",
                ["cf-turnstile-response"] = attempt == 0 ? FakeTurnstileVerifier.RejectedToken : "XXXX.DUMMY.TOKEN.XXXX",
                ["__RequestVerificationToken"] = token,
            });
            using var response = await visitor.Client.PostAsync(new Uri("/fr/ma-reservation", UriKind.Relative), form, Ct);
            statuses.Add(response.StatusCode);
            if (attempt == 0)
            {
                robotLocation = Location(response);
            }
        }

        robotLocation.ShouldBe("/fr/ma-reservation?erreur=booking.funnel.verification_failed");
        statuses.Take(PublicScenarioHost.RateLimit).ShouldAllBe(s => s == HttpStatusCode.SeeOther);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
        TestEmails.ReadAll(scenario.Factory.EmailDirectory).ShouldNotContain(e => e.To.Contains(email, StringComparison.Ordinal));
    }

}
