using System.Globalization;
using CarRental.Application.Cash;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Payments;
using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Payments;

/// <summary>Registration of the Payments and Cash modules, and their contract enums.</summary>
public sealed class PaymentsModuleTests
{
    [Fact]
    public void The_payments_module_registers_its_contracts_and_back_office_queries()
    {
        var services = new ServiceCollection().AddPaymentsModule();

        services.ShouldContain(d => d.ServiceType == typeof(ICounterPayments) && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(IDeposits));
        services.ShouldContain(d => d.ServiceType == typeof(IPaymentsQueries));
        services.ShouldContain(d => d.ServiceType == typeof(PaymentAdminQueries));
    }

    [Fact]
    public void The_cash_module_registers_its_contracts_and_its_demo_seeder()
    {
        var services = new ServiceCollection().AddCashModule();

        services.ShouldContain(d => d.ServiceType == typeof(ICashSessions) && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(ICashDrawer));
        services.ShouldContain(d => d.ServiceType == typeof(ICashQueries));
        services.ShouldContain(d => d.ServiceType == typeof(IDataSeeder) && d.ImplementationType!.Name == "CashDemoSeeder");
    }

    [Theory]
    [InlineData(typeof(PaymentDirection), typeof(PaymentDirectionInfo))]
    [InlineData(typeof(PaymentMethod), typeof(PaymentMethodInfo))]
    [InlineData(typeof(PaymentStatus), typeof(PaymentStatusInfo))]
    [InlineData(typeof(DepositMode), typeof(DepositModeInfo))]
    [InlineData(typeof(DepositStatus), typeof(DepositStatusInfo))]
    [InlineData(typeof(CashSessionStatus), typeof(CashSessionStatusInfo))]
    [InlineData(typeof(CashMovementKind), typeof(CashMovementKindInfo))]
    [InlineData(typeof(CashPocket), typeof(CashPocketInfo))]
    [InlineData(typeof(CashFlowDirection), typeof(CashFlowDirectionInfo))]
    public void Contract_enums_mirror_the_domain_enums(Type domain, Type contract)
    {
        var domainValues = Enum.GetValues(domain).Cast<object>().Select(v => (Enum.GetName(domain, v), Convert.ToInt32(v, CultureInfo.InvariantCulture)));
        var contractValues = Enum.GetValues(contract).Cast<object>().Select(v => (Enum.GetName(contract, v), Convert.ToInt32(v, CultureInfo.InvariantCulture)));

        contractValues.ShouldBe(domainValues);
    }
}
