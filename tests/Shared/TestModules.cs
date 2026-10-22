using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Testing;

/// <summary>
/// The optional modules of the test agencies (ADR-0067): an agency starts with WhatsApp and the sales channels off, and a
/// host or a test that needs them switches them on explicitly — on the aggregate, in a system scope, like the support
/// does (the agency directory drops its copy when the change is committed).
/// </summary>
public static class TestModules
{
    /// <summary>Address of the public site of the main demo agency ("Agence Démo Tunis").</summary>
    public const string DemoAgencySlug = "agence-demo-tunis";

    /// <summary>The modules of the demo agency that the test hosts switch on (off by default for any agency).</summary>
    public const AgencyModules DemoModules = AgencyModules.WhatsApp | AgencyModules.Channels;

    /// <summary>Switches <paramref name="modules"/> on for the agency, the others unchanged.</summary>
    public static Task EnableAsync(IServiceProvider services, Guid agencyId, AgencyModules modules, CancellationToken cancellationToken = default) =>
        ChangeAsync(services, a => a.Id == agencyId, current => current | modules, cancellationToken);

    /// <summary>Switches <paramref name="modules"/> off for the agency, the others unchanged (its data is kept).</summary>
    public static Task DisableAsync(IServiceProvider services, Guid agencyId, AgencyModules modules, CancellationToken cancellationToken = default) =>
        ChangeAsync(services, a => a.Id == agencyId, current => current & ~modules, cancellationToken);

    /// <summary>Switches <paramref name="modules"/> on for the agency at <paramref name="slug"/> and returns its id.</summary>
    public static Task<Guid> EnableBySlugAsync(IServiceProvider services, string slug, AgencyModules modules, CancellationToken cancellationToken = default) =>
        ChangeAsync(services, a => a.Slug == slug, current => current | modules, cancellationToken);

    private static async Task<Guid> ChangeAsync(
        IServiceProvider services,
        System.Linq.Expressions.Expression<Func<Agency, bool>> which,
        Func<AgencyModules, AgencyModules> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:tests"));
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = await db.Set<Agency>().SingleAsync(which, cancellationToken);
        var changed = agency.SetModules(change(agency.Modules));
        if (changed.IsFailure)
        {
            throw new InvalidOperationException($"The modules of {agency.Name} could not be changed: {changed.Error}.");
        }

        await db.SaveChangesAsync(cancellationToken);
        return agency.Id;
    }
}
