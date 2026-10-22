using CarRental.Application.Accounting.Chart;
using CarRental.Application.Accounting.Closing;
using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Periods;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Accounting;

/// <summary>
/// Accounting module (package 3B, ADR-0064 and ADR-0065): the chart of accounts and the mapping of the operation types, the
/// journals generated from the facts of the other modules (sales, purchases, bank, cash, miscellaneous; every entry
/// balanced), their exports (generic CSV here, Excel by the web host; TODO(spec): the format of the chartered accountant),
/// the reconciliations (cash, Clic to Pay with the merchant statement importers, Booking.com) and the monthly closing with
/// its hook <see cref="IAccountingPeriods"/>, which the write paths of Billing, Payments and Cash call. The Infrastructure
/// provides <see cref="IAccountingPeriodLock"/>; the web host the labels of the entries (<see cref="IJournalTexts"/>) and the
/// Excel exporter.
/// </summary>
public static class AccountingModule
{
    public static IServiceCollection AddAccountingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The closing hook, for the other modules.
        services.AddScoped<IAccountingPeriods, AccountingPeriods>();

        // Ports with their default implementations (the hosts add more: TryAddEnumerable keeps one of each).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJournalExporter, GenericCsvJournalExporter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMerchantStatementImporter, GenericCsvStatementImporter>());

        // Services (their constructors are internal: built here).
        services.AddScoped(sp => new ChartOfAccountsService(
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new JournalService(
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<IBillingQueries>(),
            sp.GetRequiredService<ICashAccountingQueries>(),
            sp.GetRequiredService<ICashQueries>(),
            sp.GetRequiredService<IPaymentsAccountingQueries>(),
            sp.GetRequiredService<IChannelPayouts>(),
            sp.GetRequiredService<IBookingNumbers>(),
            sp.GetRequiredService<IJournalTexts>(),
            sp.GetRequiredService<IEnumerable<IJournalExporter>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new ReconciliationService(
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<ICashQueries>(),
            sp.GetRequiredService<IPaymentsAccountingQueries>(),
            sp.GetRequiredService<IChannelPayouts>(),
            sp.GetRequiredService<IBookingNumbers>(),
            sp.GetRequiredService<IAccountingPeriods>(),
            sp.GetRequiredService<IEnumerable<IMerchantStatementImporter>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new MonthlyClosingService(
            sp.GetRequiredService<IAppDbContext>(),
            sp.GetRequiredService<IUnitOfWork>(),
            sp.GetRequiredService<IUserContext>(),
            sp.GetRequiredService<IAccountingPeriodLock>(),
            sp.GetRequiredService<IReferentialQueries>(),
            sp.GetRequiredService<ICashQueries>(),
            sp.GetRequiredService<IBillingQueries>(),
            sp.GetRequiredService<IBillingAccountingQueries>(),
            sp.GetRequiredService<JournalService>(),
            sp.GetRequiredService<ReconciliationService>(),
            sp.GetRequiredService<IAuditTrail>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddDataSeeder<AccountingChartSeeder>();
        return services;
    }
}
