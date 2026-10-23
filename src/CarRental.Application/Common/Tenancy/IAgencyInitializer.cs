using CarRental.Application.Common.Security;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Common.Tenancy;

/// <summary>
/// Prepares what a module needs for an agency to work (ADR-0067): e.g. its default chart of accounts. Run by
/// <see cref="AgencyProvisioning"/> when the support registers an agency (in the same transaction), when a demo agency is
/// created, and at every start for each existing agency: it must be idempotent (check before insert). It runs for that
/// agency only: the operation works for <c>agencyId</c>.
/// </summary>
public interface IAgencyInitializer
{
    /// <summary>Run order (lower first), e.g. 100 referential, 600 accounting.</summary>
    int Order { get; }

    Task InitializeAsync(Guid agencyId, CancellationToken cancellationToken);
}

/// <summary>Runs every <see cref="IAgencyInitializer"/> for one agency, in order, in the caller's scope.</summary>
public sealed class AgencyProvisioning(IEnumerable<IAgencyInitializer> initializers, IUserContext userContext)
{
    public async Task ProvisionAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("An agency id cannot be empty.", nameof(agencyId));
        }

        if (!userContext.IsAcrossAgencies && userContext.AgencyId != agencyId)
        {
            throw new InvalidOperationException("An agency is provisioned by the support, the system or an operation of that agency.");
        }

        foreach (var initializer in initializers.OrderBy(i => i.Order))
        {
            await initializer.InitializeAsync(agencyId, cancellationToken);
        }
    }
}

public static class AgencyInitializerServiceCollectionExtensions
{
    public static IServiceCollection AddAgencyInitializer<TInitializer>(this IServiceCollection services)
        where TInitializer : class, IAgencyInitializer
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAgencyInitializer, TInitializer>();
        return services;
    }
}
