using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Referential;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Payments;

/// <summary>Isolated finance data for a test: its own agency and its own staff ids (sessions never collide between parallel tests).</summary>
internal static class FinanceData
{
    public static UserContextSnapshot Actor(string role) =>
        UserContextSnapshot.ForUser(
            Guid.CreateVersion7(),
            $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local",
            RolePresets.PermissionsOf(role),
            "203.0.113.30",
            Guid.NewGuid().ToString("N"));

    public static async Task<Guid> NewAgencyAsync(IntegrationHost host, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = Agency.Register(new AgencyDetails($"Agence finance {Guid.NewGuid():N}", "Tunis")).Value;
        db.Set<Agency>().Add(agency);
        await db.SaveChangesAsync(cancellationToken);
        return agency.Id;
    }

    public static async Task<Guid> OpenSessionAsync(IntegrationHost host, UserContextSnapshot cashier, Guid agencyId, decimal openingFloat, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope(cashier);
        var opened = await scope.ServiceProvider.GetRequiredService<ICashSessions>().OpenAsync(new OpenCashSessionRequest(agencyId, openingFloat), cancellationToken);
        opened.IsSuccess.ShouldBeTrue(opened.IsFailure ? opened.Error.ToString() : null);
        return opened.Value;
    }
}
