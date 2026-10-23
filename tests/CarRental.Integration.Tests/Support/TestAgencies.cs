using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Referential.Platform;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Support;

/// <summary>
/// Agencies of their own for the tests (ADR-0067), registered by a support account of their own as the platform pages do:
/// the office location and the first manager come with them. Names keep a <see cref="Guid"/> (the address is unique).
/// </summary>
internal static class TestAgencies
{
    /// <summary>A new support account, across agencies (outside any agency).</summary>
    public static async Task<UserContextSnapshot> SupportAsync(IntegrationHost host)
    {
        var (id, email) = await TestStaff.CreateAsync(host, Roles.Support);
        return await TestStaff.ActorAsync(host, id, email, Roles.Support);
    }

    /// <summary>A scope of the platform pages: the support across agencies, the address of the back office for the invitations.</summary>
    public static AsyncServiceScope PlatformScope(IntegrationHost host, UserContextSnapshot support)
    {
        ArgumentNullException.ThrowIfNull(host);
        var scope = host.CreateScope(support);
        scope.ServiceProvider.GetRequiredService<CarRental.Web.Security.RequestOrigin>().BaseUri = "https://staff.example.test/";
        return scope;
    }

    /// <summary>A new agency with the default modules, as the directory sees it.</summary>
    public static async Task<AgencySummary> RegisterAsync(IntegrationHost host, string label, CancellationToken cancellationToken)
    {
        await using var scope = PlatformScope(host, await SupportAsync(host));
        var registered = await scope.ServiceProvider.GetRequiredService<AgencyPlatformService>().RegisterAsync(
            new RegisterAgencyRequest(
                $"Agence {label} {Guid.NewGuid():N}",
                "Bizerte",
                Slug: null,
                Address: null,
                Phone: null,
                Email: null,
                AgencyModuleCatalog.Defaults,
                $"gerant-{Guid.NewGuid():N}@tests.local",
                $"Gérant {label}"),
            cancellationToken);
        registered.IsSuccess.ShouldBeTrue(registered.IsFailure ? registered.Error.ToString() : null);
        return (await scope.ServiceProvider.GetRequiredService<IAgencyDirectory>().FindAsync(registered.Value.AgencyId, cancellationToken))!;
    }

    /// <summary>Switches the modules of an agency as the support does (exactly <paramref name="modules"/> on afterwards).</summary>
    public static async Task SetModulesAsync(IntegrationHost host, Guid agencyId, AgencyModules modules, CancellationToken cancellationToken)
    {
        await using var scope = PlatformScope(host, await SupportAsync(host));
        var changed = await scope.ServiceProvider.GetRequiredService<AgencyPlatformService>().SetModulesAsync(agencyId, modules, cancellationToken);
        changed.IsSuccess.ShouldBeTrue(changed.IsFailure ? changed.Error.ToString() : null);
    }

    /// <summary>Suspends an agency or lifts its suspension, as the support does.</summary>
    public static async Task SetActiveAsync(IntegrationHost host, Guid agencyId, bool active, CancellationToken cancellationToken)
    {
        await using var scope = PlatformScope(host, await SupportAsync(host));
        var changed = await scope.ServiceProvider.GetRequiredService<AgencyPlatformService>().SetActiveAsync(agencyId, active, cancellationToken);
        changed.IsSuccess.ShouldBeTrue(changed.IsFailure ? changed.Error.ToString() : null);
    }
}
