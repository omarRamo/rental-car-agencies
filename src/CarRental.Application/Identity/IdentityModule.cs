using CarRental.Application.Identity.Audit;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Identity.Users;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Identity;

/// <summary>Identity and audit module: users, roles and permissions, audit log queries.</summary>
public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<UserAdministrationService>();
        services.AddScoped<IAgencyManagerInvitations, AgencyManagerInvitations>();
        services.AddScoped<SupportSessionService>();
        services.AddScoped<AuditLogQueries>();
        services.AddScoped<IIdentityQueries, IdentityQueries>();
        return services;
    }
}
