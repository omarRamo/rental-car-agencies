using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Customers;

/// <summary>
/// Invariant 9 over HTTP: identity document images are uploaded to the private container (Azurite) and read only through
/// the authorized endpoints (MFA session and customers.documents.read); every read is logged; links last 5 minutes.
/// </summary>
public sealed class DocumentImageEndpointTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<DocumentAccessLog>> AccessesAsync(Guid documentId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>()
            .Set<DocumentAccessLog>()
            .Where(a => a.DocumentId == documentId)
            .OrderBy(a => a.Id)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task An_agent_views_an_uploaded_image_and_the_view_is_logged()
    {
        var (_, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", "203.0.113.77");
        var agentEmail = await CustomerTestData.NewStaffAsync(host, Roles.Agent);
        await HttpSignIn.SignInWithTwoFactorAsync(client, agentEmail, "/admin", Ct);

        var response = await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/front?purpose=verification", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(CustomerTestData.Png());
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        response.Content.Headers.ContentDisposition.FileName!.Trim('"').ShouldBe("identity_card-front.png");
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");

        var access = (await AccessesAsync(documentId)).ShouldHaveSingleItem();
        access.UserName.ShouldBe(agentEmail);
        access.IpAddress.ShouldBe("203.0.113.77");
        access.DocumentKind.ShouldBe("identity_card.front");
        access.Access.ShouldBe("view");
        access.Reason.ShouldBe("verification");
    }

    [Fact]
    public async Task Each_read_writes_its_own_access_row()
    {
        var (_, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        using var client = host.CreateClient();
        var managerEmail = await CustomerTestData.NewStaffAsync(host, Roles.Manager);
        await HttpSignIn.SignInWithTwoFactorAsync(client, managerEmail, "/admin", Ct);

        for (var i = 0; i < 2; i++)
        {
            (await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/front", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var accesses = await AccessesAsync(documentId);
        accesses.Count.ShouldBe(2);
        accesses.ShouldAllBe(a => a.UserName == managerEmail && a.Reason == "customer_file");
    }

    [Fact]
    public async Task The_accountant_cannot_read_identity_document_images_and_nothing_is_logged()
    {
        var (_, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, await CustomerTestData.NewStaffAsync(host, Roles.Accountant), "/admin", Ct);

        var view = await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/front", UriKind.Relative), Ct);
        var link = await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/front/link", UriKind.Relative), Ct);

        view.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(view.Headers.Location!).ShouldContain("/account/access-denied");
        HttpSignIn.PathAndQuery(link.Headers.Location!).ShouldContain("/account/access-denied");
        (await AccessesAsync(documentId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_the_login_page()
    {
        var (_, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/front", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/login");
        (await AccessesAsync(documentId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_images_unknown_sides_and_purposes_are_rejected_without_log()
    {
        var (_, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, await CustomerTestData.NewStaffAsync(host, Roles.Agent), "/admin", Ct);

        (await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/back", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/side", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/front?purpose=curiosity", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync(new Uri($"/admin/customers/documents/{Guid.CreateVersion7()}/images/front", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await AccessesAsync(documentId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_temporary_link_is_a_five_minute_read_only_link_and_is_logged()
    {
        var (_, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        using var client = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(client, await CustomerTestData.NewStaffAsync(host, Roles.Agent), "/admin", Ct);
        var before = DateTimeOffset.UtcNow;

        var response = await client.GetAsync(new Uri($"/admin/customers/documents/{documentId}/images/front/link?purpose=dispute", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var url = new Uri(body.GetProperty("url").GetString()!);
        var expiresAt = body.GetProperty("expiresAtUtc").GetDateTimeOffset();
        var signedExpiry = DateTimeOffset.Parse(QueryHelpers.ParseQuery(url.Query)["se"]!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        signedExpiry.ShouldBeInRange(before.AddMinutes(4), before.AddMinutes(6));
        expiresAt.ShouldBeInRange(before.AddMinutes(4), before.AddMinutes(6));
        QueryHelpers.ParseQuery(url.Query)["sp"].ToString().ShouldBe("r", "read only");

        using var storageClient = new HttpClient();
        (await storageClient.GetByteArrayAsync(url, Ct)).ShouldBe(CustomerTestData.Png());
        (await storageClient.GetAsync(new Uri(url.GetLeftPart(UriPartial.Path)), Ct)).IsSuccessStatusCode.ShouldBeFalse("the container is private");

        var access = (await AccessesAsync(documentId)).ShouldHaveSingleItem();
        access.Access.ShouldBe("temporary_link");
        access.Reason.ShouldBe("dispute");
    }

    [Fact]
    public async Task The_manager_downloads_the_personal_data_export_and_the_agent_cannot()
    {
        var (personId, _) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        using var manager = host.CreateClient();
        var managerEmail = await CustomerTestData.NewStaffAsync(host, Roles.Manager);
        await HttpSignIn.SignInWithTwoFactorAsync(manager, managerEmail, "/admin", Ct);
        using var agent = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(agent, await CustomerTestData.NewStaffAsync(host, Roles.Agent), "/admin", Ct);

        var export = await manager.GetAsync(new Uri($"/admin/customers/persons/{personId}/export", UriKind.Relative), Ct);
        var denied = await agent.GetAsync(new Uri($"/admin/customers/persons/{personId}/export", UriKind.Relative), Ct);

        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        export.Content.Headers.ContentDisposition!.FileNameStar.ShouldStartWith($"personal-data-{personId:N}-");
        var json = await export.Content.ReadFromJsonAsync<JsonElement>(Ct);
        json.GetProperty("person").GetProperty("id").GetGuid().ShouldBe(personId);
        json.GetProperty("identityDocuments").GetArrayLength().ShouldBe(1);
        denied.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(denied.Headers.Location!).ShouldContain("/account/access-denied");

        await using var scope = host.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<AuditLog>()
            .SingleAsync(a => a.Action == "customers.person_exported" && a.EntityId == personId.ToString(), Ct);
        audit.UserName.ShouldBe(managerEmail);
    }
}
