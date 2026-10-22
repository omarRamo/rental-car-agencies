using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Tenancy;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Payments;

/// <summary>Isolated finance data for a test: its own agency and its own staff ids (sessions never collide between parallel tests).</summary>
internal static class FinanceData
{
    /// <summary>A staff actor of the scenario tests, across agencies (see <see cref="Fleet.FleetData.Actor"/>).</summary>
    public static UserContextSnapshot Actor(string role)
    {
        var actor = UserContextSnapshot.ForUser(
            Guid.CreateVersion7(),
            $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local",
            RolePresets.PermissionsOf(role),
            "203.0.113.30",
            Guid.NewGuid().ToString("N"));
        return actor with { AllAgencies = true };
    }

    /// <summary>A member of the staff of <paramref name="agencyId"/> with the presets of <paramref name="role"/> (ADR-0067).</summary>
    public static UserContextSnapshot StaffOf(Guid agencyId, string role) =>
        TestActors.Staff(agencyId, role, $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local");

    /// <summary>A new agency, provisioned like an agency the support registers (its office, its chart of accounts…).</summary>
    public static async Task<Guid> NewAgencyAsync(IntegrationHost host, CancellationToken cancellationToken)
    {
        Guid agencyId;
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var agency = Agency.Register(new AgencyDetails($"Agence finance {Guid.NewGuid():N}", "Tunis")).Value;
            db.Set<Agency>().Add(agency);
            await db.SaveChangesAsync(cancellationToken);
            agencyId = agency.Id;
        }

        await ProvisionAsync(host, agencyId, cancellationToken);
        return agencyId;
    }

    /// <summary>
    /// Prepares the modules of an agency a test created directly (ADR-0067), as the registration by the support and the
    /// start of the application do: the agency initializers, as the system working for that agency.
    /// </summary>
    public static async Task ProvisionAsync(IntegrationHost host, Guid agencyId, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope(TestActors.SystemFor(agencyId, "system:seed"));
        await scope.ServiceProvider.GetRequiredService<AgencyProvisioning>().ProvisionAsync(agencyId, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IAppDbContext>().SaveChangesAsync(cancellationToken);
    }

    /// <summary>Switches the modules of an agency, as the support does (the agency directory drops it at the commit).</summary>
    public static async Task SetModulesAsync(IntegrationHost host, Guid agencyId, AgencyModules modules, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = await db.Set<Agency>().SingleAsync(a => a.Id == agencyId, cancellationToken);
        agency.SetModules(modules).IsSuccess.ShouldBeTrue();
        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task<Guid> OpenSessionAsync(IntegrationHost host, UserContextSnapshot cashier, Guid agencyId, decimal openingFloat, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope(cashier);
        var opened = await scope.ServiceProvider.GetRequiredService<ICashSessions>().OpenAsync(new OpenCashSessionRequest(agencyId, openingFloat), cancellationToken);
        opened.IsSuccess.ShouldBeTrue(opened.IsFailure ? opened.Error.ToString() : null);
        return opened.Value;
    }
}
