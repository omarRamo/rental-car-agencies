using CarRental.Application.Billing.EInvoicing;
using CarRental.Application.Billing.Invoicing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CarRental.Infrastructure.Billing;

/// <summary>Options of the electronic invoicing (<c>Billing:Ttn</c>).</summary>
public sealed class TtnOptions
{
    public const string SectionName = "Billing:Ttn";

    /// <summary>
    /// TEST-ONLY: the fake TTN (accepts every document) instead of the real adapter; honoured in Development and Testing
    /// only, ignored elsewhere.
    /// </summary>
    public bool UseFake { get; set; }
}

/// <summary>
/// Composition of the Billing module's adapters (ADR-0057, ADR-0058): the billing lock on SQL Server and the TTN provider —
/// the real adapter (not configured until the TEIF specification, TODO(spec)), or the fake in Development and Testing
/// when <c>Billing:Ttn:UseFake</c> is set.
/// </summary>
public static class BillingSetup
{
    public const string TestingEnvironment = "Testing";

    public static IServiceCollection AddBillingInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddScoped<IBillingLock, SqlBillingLock>();

        var options = configuration.GetSection(TtnOptions.SectionName).Get<TtnOptions>() ?? new TtnOptions();
        if (options.UseFake && (environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment)))
        {
            services.TryAddSingleton<FakeTtnEInvoiceProvider>();
            services.AddSingleton<IEInvoiceProvider>(sp => sp.GetRequiredService<FakeTtnEInvoiceProvider>());
        }
        else
        {
            services.AddSingleton<IEInvoiceProvider, TtnEInvoiceProvider>();
        }

        return services;
    }
}
