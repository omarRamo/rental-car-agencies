using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.PublicBooking;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using static CarRental.Integration.Tests.Public.PublicTestData;

namespace CarRental.Integration.Tests.CustomerApi;

/// <summary>
/// The API of the mobile application over HTTP (ADR-0070): registration and sign-in with JWT access tokens and rotating
/// refresh tokens, the account kept to the agency of the site, the verification code read from the e-mail, a booking
/// request made from the application and followed there, the module switch and the OpenAPI document.
/// </summary>
public sealed class CustomerApiTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Api = "/" + DemoSlug + "/api/v1";

    [Fact]
    public async Task A_customer_registers_signs_in_refreshes_and_stays_within_the_agency_of_the_site()
    {
        using var app = Visitor.Create(host.Factory);
        var email = $"api.{Visitor.Unique().ToLowerInvariant()}@example.test";

        using var registered = await app.Client.PostAsJsonAsync($"{Api}/auth/register", new { email, password = "correct horse battery", firstName = "Amel", lastName = "Trabelsi", phone = "20 111 001", culture = "fr", device = "Pixel 9" }, Ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(Ct));
        registered.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var session = await registered.Content.ReadFromJsonAsync<JsonElement>(Ct);
        session.GetProperty("tokenType").GetString().ShouldBe("Bearer");
        session.GetProperty("account").GetProperty("email").GetString().ShouldBe(email);
        session.GetProperty("account").GetProperty("emailVerified").GetBoolean().ShouldBeFalse();
        var access = session.GetProperty("accessToken").GetString()!;
        var refresh = session.GetProperty("refreshToken").GetString()!;
        access.Split('.').Length.ShouldBe(3, "a JWT");

        // The same address cannot register twice on this agency.
        using var duplicate = await app.Client.PostAsJsonAsync($"{Api}/auth/register", new { email, password = "correct horse battery", firstName = "Amel", lastName = "Trabelsi" }, Ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("customers.account.email_taken");

        // Without a token: 401 with the bearer challenge; with the token: the account.
        using var anonymous = await app.Client.GetAsync(new Uri($"{Api}/me", UriKind.Relative), Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
        using var me = await GetAsync(app.Client, $"{Api}/me", access);
        me.StatusCode.ShouldBe(HttpStatusCode.OK, await me.Content.ReadAsStringAsync(Ct));
        (await me.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("firstName").GetString().ShouldBe("Amel");

        // The token of the Tunis site opens nothing on the Sousse site (ADR-0067), nor does it open the back office.
        using var elsewhere = await GetAsync(app.Client, $"/{SecondDemoSlug}/api/v1/me", access);
        elsewhere.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var admin = await GetAsync(app.Client, "/admin", access);
        admin.StatusCode.ShouldBeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);

        // Wrong password, then the right one on another device: a second family of refresh tokens.
        using var wrong = await app.Client.PostAsJsonAsync($"{Api}/auth/login", new { email, password = "not the password" }, Ct);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("customers.account.credentials_invalid");
        using var login = await app.Client.PostAsJsonAsync($"{Api}/auth/login", new { email = email.ToUpperInvariant(), password = "correct horse battery", device = "iPhone" }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(Ct));

        // Rotation: the new refresh token works, the old one is dead and kills its family when replayed.
        using var refreshed = await app.Client.PostAsJsonAsync($"{Api}/auth/refresh", new { refreshToken = refresh }, Ct);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync(Ct));
        var rotated = (await refreshed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refreshToken").GetString()!;
        rotated.ShouldNotBe(refresh);
        using var replayed = await app.Client.PostAsJsonAsync($"{Api}/auth/refresh", new { refreshToken = refresh }, Ct);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var afterReplay = await app.Client.PostAsJsonAsync($"{Api}/auth/refresh", new { refreshToken = rotated }, Ct);
        afterReplay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the family was revoked");

        // The verification code arrives by e-mail from the agency, in French.
        var mail = await TestEmails.WaitForAsync(host.Factory.EmailDirectory, m => m.To.Contains(email, StringComparison.OrdinalIgnoreCase) && m.Subject.Contains("Vérifiez", StringComparison.Ordinal), Ct);
        mail.Subject.ShouldContain(DemoAgencyName);
        var code = TestEmails.Code(mail).ShouldNotBeNull();
        using var badCode = await PostAsync(app.Client, $"{Api}/auth/verify-email", new { code = code == "000000" ? "111111" : "000000" }, access);
        badCode.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using var verified = await PostAsync(app.Client, $"{Api}/auth/verify-email", new { code }, access);
        verified.StatusCode.ShouldBe(HttpStatusCode.OK, await verified.Content.ReadAsStringAsync(Ct));
        (await verified.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("emailVerified").GetBoolean().ShouldBeTrue();

        // A password change ends the sessions that carried the old stamp.
        using var changed = await PostAsync(app.Client, $"{Api}/auth/change-password", new { currentPassword = "correct horse battery", newPassword = "another strong one" }, access);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        using var stale = await GetAsync(app.Client, $"{Api}/me", access);
        stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the old access token carries the old stamp");
        var fresh = (await changed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
        using var again = await GetAsync(app.Client, $"{Api}/me", fresh);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_with_the_emailed_code()
    {
        using var app = Visitor.Create(host.Factory);
        var email = $"reset.{Visitor.Unique().ToLowerInvariant()}@example.test";
        (await app.Client.PostAsJsonAsync($"{Api}/auth/register", new { email, password = "correct horse battery", firstName = "Sami", lastName = "Jaziri" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var unknown = await app.Client.PostAsJsonAsync($"{Api}/auth/forgot-password", new { email = $"nobody.{Visitor.Unique().ToLowerInvariant()}@example.test" }, Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted, "same answer whether the address has an account or not");
        using var asked = await app.Client.PostAsJsonAsync($"{Api}/auth/forgot-password", new { email }, Ct);
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var mail = await TestEmails.WaitForAsync(host.Factory.EmailDirectory, m => m.To.Contains(email, StringComparison.OrdinalIgnoreCase) && m.Subject.Contains("mot de passe", StringComparison.Ordinal), Ct);
        var code = TestEmails.Code(mail).ShouldNotBeNull();
        using var reset = await app.Client.PostAsJsonAsync($"{Api}/auth/reset-password", new { email, code, newPassword = "a brand new password" }, Ct);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reset.Content.ReadAsStringAsync(Ct));

        using var old = await app.Client.PostAsJsonAsync($"{Api}/auth/login", new { email, password = "correct horse battery" }, Ct);
        old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var login = await app.Client.PostAsJsonAsync($"{Api}/auth/login", new { email, password = "a brand new password" }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("account").GetProperty("emailVerified").GetBoolean().ShouldBeTrue("the reset proved the address");
    }

    [Fact]
    public async Task The_site_and_the_catalogue_are_public_and_the_module_can_be_switched_off()
    {
        using var app = Visitor.Create(host.Factory);

        using var site = await app.Client.GetAsync(new Uri($"{Api}/site", UriKind.Relative), Ct);
        site.StatusCode.ShouldBe(HttpStatusCode.OK, await site.Content.ReadAsStringAsync(Ct));
        site.Headers.CacheControl!.Public.ShouldBeTrue();
        var facts = await site.Content.ReadFromJsonAsync<JsonElement>(Ct);
        facts.GetProperty("name").GetString().ShouldBe(DemoAgencyName);
        facts.GetProperty("slug").GetString().ShouldBe(DemoSlug);
        facts.GetProperty("currency").GetString().ShouldBe("TND");
        facts.GetProperty("locations").EnumerateArray().Select(l => l.GetProperty("code").GetString()).ShouldContain("TUN");
        facts.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ShouldContain("ECMR");
        facts.GetProperty("termsUrls").GetProperty("fr").GetString().ShouldEndWith($"/{DemoSlug}/fr/conditions");
        facts.GetProperty("policies").GetProperty("emailMustBeVerifiedToBook").GetBoolean().ShouldBeTrue();

        using var availability = await app.Client.GetAsync(new Uri($"{Api}/availability?lieu=TUN&depart={LocalDate(161)}&retour={LocalDate(164)}", UriKind.Relative), Ct);
        availability.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var quote = await app.Client.PostAsJsonAsync($"{Api}/quotes", new { pickupLocation = "TUN", pickup = LocalDate(161), @return = LocalDate(164), category = "ECMR" }, Ct);
        quote.StatusCode.ShouldBe(HttpStatusCode.Created, "no API key needed by the application: rate limited like the site");

        using var document = await app.Client.GetAsync(new Uri("/api/openapi/customer-api-v1.json", UriKind.Relative), Ct);
        document.StatusCode.ShouldBe(HttpStatusCode.OK, await document.Content.ReadAsStringAsync(Ct));
        var openApi = await document.Content.ReadAsStringAsync(Ct);
        openApi.ShouldContain("/{agency}/api/v1/auth/login");
        openApi.ShouldContain("/{agency}/api/v1/me/bookings");
        openApi.ShouldNotContain("/admin/");

        // An agency whose module is off: its site works, its API answers 404.
        var agency = await TestAgencies.RegisterAsync(host, "SansApi", Ct);
        await TestAgencies.SetModulesAsync(host, agency.Id, AgencyModuleCatalog.Defaults & ~AgencyModules.CustomerApi, Ct);
        using var closed = await app.Client.GetAsync(new Uri($"/{agency.Slug}/api/v1/site", UriKind.Relative), Ct);
        closed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var page = await app.Client.GetAsync(new Uri($"/{agency.Slug}/fr", UriKind.Relative), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_customer_books_from_the_application_and_follows_the_booking_there()
    {
        using var app = Visitor.Create(host.Factory);
        var suffix = Visitor.Unique();
        var email = $"book.{suffix.ToLowerInvariant()}@example.test";
        using var registered = await app.Client.PostAsJsonAsync($"{Api}/auth/register", new { email, password = "correct horse battery", firstName = "Amel", lastName = $"World{suffix}", phone = Visitor.RandomPhone() }, Ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var access = (await registered.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
        var request = new
        {
            pickupLocation = "TUN",
            pickup = LocalDate(171),
            @return = LocalDate(174),
            category = "ECMR",
            extras = new[] { new { code = "CHILD_SEAT", quantity = 1 } },
            mainDriver = new { nationality = "FR", birthDate = "1984-03-14", licenseNumber = $"L{suffix}", licenseIssuedOn = "2004-06-01", licenseCountry = "FR", identityNumber = $"P{suffix}", identityExpiresOn = "2035-01-31" },
            consents = new { documents = true, whatsApp = false, marketing = false },
            termsAccepted = true,
        };

        // The address must be verified first.
        using var tooEarly = await PostAsync(app.Client, $"{Api}/me/bookings", request, access);
        tooEarly.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tooEarly.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("customers.account.email_not_verified");
        var mail = await TestEmails.WaitForAsync(host.Factory.EmailDirectory, m => m.To.Contains(email, StringComparison.OrdinalIgnoreCase), Ct);
        (await PostAsync(app.Client, $"{Api}/auth/verify-email", new { code = TestEmails.Code(mail) }, access)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Missing driver fields come back as field errors, like step 2 of the site.
        using var incomplete = await PostAsync(app.Client, $"{Api}/me/bookings", new { request.pickupLocation, request.pickup, request.@return, request.category, consents = request.consents, termsAccepted = true }, access);
        incomplete.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await incomplete.Content.ReadAsStringAsync(Ct));
        (await incomplete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldContain("main.birth_date");

        using var created = await PostAsync(app.Client, $"{Api}/me/bookings", request, access);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var booking = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var number = booking.GetProperty("number").GetString()!;
        number.ShouldStartWith("LOC-");
        booking.GetProperty("status").GetString().ShouldBe("pending_validation");
        booking.GetProperty("guarantee").GetProperty("mode").GetString().ShouldBe("pre_authorization");
        booking.GetProperty("paymentUrl").GetString().ShouldNotBeNullOrEmpty("the payment page of the simulated bank");
        booking.GetProperty("confirmationUrl").GetString().ShouldEndWith($"/{DemoSlug}/fr/reserver/confirmation/{number}");
        var bookingId = booking.GetProperty("bookingId").GetGuid();

        using var list = await GetAsync(app.Client, $"{Api}/me/bookings", access);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await list.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Select(b => b.GetProperty("number").GetString()).ShouldContain(number);

        using var details = await GetAsync(app.Client, $"{Api}/me/bookings/{bookingId:D}", access);
        details.StatusCode.ShouldBe(HttpStatusCode.OK, await details.Content.ReadAsStringAsync(Ct));
        var file = await details.Content.ReadFromJsonAsync<JsonElement>(Ct);
        file.GetProperty("categoryCode").GetString().ShouldBe("ECMR");
        file.GetProperty("pickupLocation").GetProperty("code").GetString().ShouldBe("TUN");
        file.GetProperty("canSendDocuments").GetBoolean().ShouldBeTrue();
        file.GetProperty("drivers").EnumerateArray().First().GetProperty("name").GetString()!.ShouldContain($"World{suffix}");
        var slot = file.GetProperty("drivers").EnumerateArray().First().GetProperty("slots").EnumerateArray().First().GetProperty("key").GetString()!;

        using var upload = new MultipartFormDataContent { { new ByteArrayContent(Visitor.Jpeg) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } }, "file", "licence.jpg" } };
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"{Api}/me/bookings/{bookingId:D}/documents/{slot}") { Content = upload };
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var uploaded = await app.Client.SendAsync(uploadRequest, Ct);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync(Ct));
        (await uploaded.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("slot").GetString().ShouldBe(slot);

        // Another customer of the same agency never sees it.
        using var other = Visitor.Create(host.Factory);
        using var otherRegistered = await other.Client.PostAsJsonAsync($"{Api}/auth/register", new { email = $"other.{Visitor.Unique().ToLowerInvariant()}@example.test", password = "correct horse battery", firstName = "Sami", lastName = "Jaziri" }, Ct);
        var otherAccess = (await otherRegistered.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
        using var forbidden = await GetAsync(other.Client, $"{Api}/me/bookings/{bookingId:D}", otherAccess);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, Ct);
    }
}
