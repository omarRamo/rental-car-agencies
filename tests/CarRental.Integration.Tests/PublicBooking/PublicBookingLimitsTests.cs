using System.Net;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings.Access;
using CarRental.Integration.Tests.Bookings;
using CarRental.Integration.Tests.Public;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// The abuse limits of the booking funnel and of "Ma réservation" (ADR-0037, ADR-0049, ADR-0051) on the dedicated host of
/// the public scenarios (3 requests per window and per client): choices and access requests beyond the submission limit
/// get a 429, and a refused anti-robot check issues no access to the booking it names. Everything goes through that host
/// (its services and its pages), whatever database it runs on.
/// </summary>
[Collection(PublicScenarioCollection.Name)]
public sealed class PublicBookingLimitsTests(PublicScenarioHost scenario)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Choosing_categories_beyond_the_submission_limit_gets_a_429()
    {
        using var visitor = Create(scenario.Factory);
        var token = Support.HttpSignIn.AntiforgeryToken(await visitor.PageAsync($"/agence-demo-tunis/fr/reserver?lieu=TUN&depart={LocalDate(116)}&retour={LocalDate(119)}", Ct));

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
            using var response = await visitor.Client.PostAsync(new Uri("/agence-demo-tunis/fr/reserver/selection", UriKind.Relative), form, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(PublicScenarioHost.RateLimit).ShouldAllBe(s => s == HttpStatusCode.SeeOther);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Access_requests_beyond_the_submission_limit_get_a_429_and_a_refused_robot_check_issues_no_access()
    {
        var request = await FunnelWorld.SubmitDemoAsync(scenario.Factory, BookingWorld.InDays(101), BookingWorld.InDays(104), Ct);
        using var visitor = Create(scenario.Factory);
        var token = Support.HttpSignIn.AntiforgeryToken(await visitor.PageAsync("/agence-demo-tunis/fr/ma-reservation", Ct));

        var statuses = new List<HttpStatusCode>();
        string? robotLocation = null;
        for (var attempt = 0; attempt <= PublicScenarioHost.RateLimit; attempt++)
        {
            // The refused check names a real booking; the other requests name none (they only count for the limit).
            var robot = attempt == 0;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = robot ? request.Email : $"nobody.{Unique().ToLowerInvariant()}@example.test",
                ["numero"] = robot ? request.Number : "LOC-2099-000001",
                ["cf-turnstile-response"] = robot ? FakeTurnstileVerifier.RejectedToken : "XXXX.DUMMY.TOKEN.XXXX",
                ["__RequestVerificationToken"] = token,
            });
            using var response = await visitor.Client.PostAsync(new Uri("/agence-demo-tunis/fr/ma-reservation", UriKind.Relative), form, Ct);
            statuses.Add(response.StatusCode);
            if (robot)
            {
                robotLocation = Location(response);
            }
        }

        robotLocation.ShouldBe("/agence-demo-tunis/fr/ma-reservation?erreur=booking.funnel.verification_failed");
        statuses.Take(PublicScenarioHost.RateLimit).ShouldAllBe(s => s == HttpStatusCode.SeeOther);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);

        // An access is issued (and its e-mail queued) within the request itself: none exists for the booking.
        await using var scope = FunnelWorld.CreateScope(scenario.Factory, FunnelWorld.Funnel);
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var issued = await db.Set<BookingAccessGrant>()
            .AnyAsync(g => g.BookingId == request.BookingId && g.Purpose == AccessPurpose.MyBooking, Ct);
        issued.ShouldBeFalse("a refused anti-robot check issues no link and sends no code");
    }
}
