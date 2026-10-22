using System.Net;
using CarRental.Application.Fleet;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Fleet.Photos;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Fleet;
using CarRental.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Fleet;

/// <summary>Fleet pages and photo endpoints over HTTP: anonymous public category photos, protected vehicle photos.</summary>
public sealed class FleetWebTests(IntegrationHost host)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_category_photo_is_served_anonymously_with_cache_headers_and_entity_tag()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            (await scope.ServiceProvider.GetRequiredService<FleetPhotoService>().UploadCategoryPhotoAsync(categoryId, new MemoryStream(Png), Ct)).IsSuccess.ShouldBeTrue();
        }

        string photoUrl;
        await using (var scope = host.CreateScope())
        {
            photoUrl = (await scope.ServiceProvider.GetRequiredService<IFleetQueries>().GetCategoryAsync(categoryId, Ct))!.PhotoUrl!;
        }

        using var client = host.CreateClient();
        var response = await client.GetAsync(new Uri(photoUrl, UriKind.Relative), Ct);

        photoUrl.ShouldStartWith($"/media/categories/{categoryId:D}?v=");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Png);
        response.Headers.CacheControl!.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromDays(1));
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        var entityTag = response.Headers.ETag.ShouldNotBeNull();

        using var revalidation = new HttpRequestMessage(HttpMethod.Get, new Uri(photoUrl, UriKind.Relative));
        revalidation.Headers.IfNoneMatch.Add(entityTag);
        var notModified = await client.SendAsync(revalidation, Ct);
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        (await client.GetAsync(new Uri($"/media/categories/{Guid.CreateVersion7():D}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Vehicle_photos_are_reserved_to_signed_in_fleet_users()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "CDMR", Ct);
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct);
        string url;
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            (await scope.ServiceProvider.GetRequiredService<FleetPhotoService>().UploadVehiclePhotoAsync(vehicleId, new MemoryStream(Png), Ct)).IsSuccess.ShouldBeTrue();
            url = (await scope.ServiceProvider.GetRequiredService<FleetAdminQueries>().GetVehicleAsync(vehicleId, Ct)).Value.Photos.ShouldHaveSingleItem().Url;
        }

        using var anonymous = host.CreateClient();
        var refused = await anonymous.GetAsync(new Uri(url, UriKind.Relative), Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(refused.Headers.Location!).ShouldContain("/account/login");

        var (_, accountantEmail) = await TestStaff.CreateAsync(host, Roles.Accountant, agencyId: agencyId);
        using var accountant = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(accountant, accountantEmail, "/admin", Ct);
        var served = await accountant.GetAsync(new Uri(url, UriKind.Relative), Ct);

        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        served.Headers.CacheControl!.Private.ShouldBeTrue();
        served.Headers.CacheControl.NoCache.ShouldBeTrue();
        (await accountant.GetAsync(new Uri($"/media/vehicles/{vehicleId:D}/photos/{Guid.CreateVersion7():D}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The staff of another agency (ADR-0067): the vehicle does not exist for them.
        var (_, otherEmail) = await TestStaff.CreateAsync(host, Roles.Manager);
        using var other = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(other, otherEmail, "/admin", Ct);
        (await other.GetAsync(new Uri(url, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_fleet_screens_require_a_signed_in_user_and_open_for_the_manager()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "IFMR", Ct);
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct);
        string[] pages = ["/admin/fleet", "/admin/fleet/categories", "/admin/fleet/deadlines", $"/admin/fleet/vehicles/{vehicleId}"];

        using var anonymous = host.CreateClient();
        foreach (var page in pages)
        {
            var response = await anonymous.GetAsync(new Uri(page, UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect, page);
            HttpSignIn.PathAndQuery(response.Headers.Location!).ShouldContain("/account/login");
        }

        await FleetAccounts.EnsureAsync(host, FleetAccounts.Manager, Roles.Manager);
        using var manager = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(manager, FleetAccounts.Manager, "/admin", Ct);
        foreach (var page in pages)
        {
            var response = await manager.GetAsync(new Uri(page, UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, page);
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(Roles.Agent)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Fleet_writes_are_refused_without_the_manage_permission(string role)
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "MCMR", Ct);

        await using var scope = host.CreateScope(FleetData.Actor(role));
        var vehicles = scope.ServiceProvider.GetRequiredService<VehicleService>();
        var categories = scope.ServiceProvider.GetRequiredService<VehicleCategoryService>();
        var photos = scope.ServiceProvider.GetRequiredService<FleetPhotoService>();

        (await vehicles.RegisterAsync(FleetData.Form(categoryId), 0, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await categories.SetActiveAsync(categoryId, false, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await photos.UploadCategoryPhotoAsync(categoryId, new MemoryStream(Png), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await scope.ServiceProvider.GetRequiredService<FleetAdminQueries>().ListCategoriesAsync(cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
    }
}
