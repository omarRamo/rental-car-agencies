using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Customers;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Customers;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Customers;

/// <summary>Registration of the module: settings, contracts, outbox handler, recurring job and demo seeder.</summary>
public sealed class CustomersModuleTests
{
    private static IServiceCollection Module() => new ServiceCollection().AddCustomersModule();

    [Fact]
    public void The_module_declares_its_settings_with_their_defaults()
    {
        using var provider = Module().BuildServiceProvider();
        var catalog = provider.GetRequiredService<ISettingDefinitionCatalog>();

        catalog.Find("drivers.minimum_age")!.DefaultValue.ShouldBe("21");
        catalog.Find("drivers.minimum_license_years")!.DefaultValue.ShouldBe("2");
        catalog.Find("customers.document_image_retention_enabled")!.DefaultValue.ShouldBe("false");
        catalog.Find("customers.document_image_retention_days")!.DefaultValue.ShouldBe("400");
        catalog.Find("drivers.young_driver_age_below").ShouldBeNull("owned by the Pricing module");
        CustomerSettings.All.Count.ShouldBe(4);
    }

    [Theory]
    [InlineData("15", false)]
    [InlineData("21", true)]
    [InlineData("100", false)]
    [InlineData("abc", false)]
    public void The_minimum_age_is_bounded(string value, bool valid)
    {
        CustomerSettings.DriverMinimumAge.Validate(value).IsSuccess.ShouldBe(valid);
    }

    [Theory]
    [InlineData("29", false)]
    [InlineData("400", true)]
    [InlineData("3651", false)]
    public void The_retention_period_is_bounded(string value, bool valid)
    {
        CustomerSettings.DocumentImageRetentionDays.Validate(value).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void Contracts_handlers_jobs_and_seeders_are_registered()
    {
        var services = Module();

        services.ShouldContain(d => d.ServiceType == typeof(ICustomersQueries));
        services.ShouldContain(d => d.ServiceType == typeof(ICustomerRegistration));
        services.ShouldContain(d => d.ServiceType == typeof(IOutboxHandler<DocumentImagesDiscarded>));
        services.ShouldContain(d => d.ServiceType == typeof(OutboxMessageTypeRegistration)
                                    && ((OutboxMessageTypeRegistration)d.ImplementationInstance!).MessageType == typeof(DocumentImagesDiscarded));
        services.ShouldContain(d => d.ServiceType == typeof(RecurringJobDescriptor)
                                    && ((RecurringJobDescriptor)d.ImplementationInstance!).JobId == "customers.purge_document_images");
        services.ShouldContain(d => d.ServiceType == typeof(IDataSeeder) && d.ImplementationType!.Name == "CustomersDemoSeeder");
    }

    [Theory]
    [InlineData(typeof(IdentityDocumentType), typeof(IdentityDocumentTypeInfo))]
    [InlineData(typeof(DocumentImageSide), typeof(DocumentImageSideInfo))]
    [InlineData(typeof(CustomerKind), typeof(CustomerKindInfo))]
    [InlineData(typeof(ConsentPurpose), typeof(ConsentPurposeInfo))]
    [InlineData(typeof(ConsentChannel), typeof(ConsentChannelInfo))]
    [InlineData(typeof(EligibilityIssueKind), typeof(EligibilityIssueKindInfo))]
    public void Contract_enums_mirror_the_domain_enums(Type domain, Type contract)
    {
        var domainValues = Enum.GetValues(domain).Cast<object>().Select(v => (Enum.GetName(domain, v), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));
        var contractValues = Enum.GetValues(contract).Cast<object>().Select(v => (Enum.GetName(contract, v), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));

        contractValues.ShouldBe(domainValues);
    }
}
