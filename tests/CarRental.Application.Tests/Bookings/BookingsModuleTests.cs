using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Jobs;
using CarRental.Application.Bookings.Seeding;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Bookings;

/// <summary>Registration of the module: settings with the defaults of docs/conception §1, contracts, services, jobs, seeder.</summary>
public sealed class BookingsModuleTests
{
    [Fact]
    public void The_module_declares_its_settings_with_their_defaults()
    {
        using var provider = new ServiceCollection().AddBookingsModule().BuildServiceProvider();
        var catalog = provider.GetRequiredService<ISettingDefinitionCatalog>();

        catalog.Find("bookings.information_request_extension")!.DefaultValue.ShouldBe("1.00:00:00");
        catalog.Find("bookings.option_default_duration")!.DefaultValue.ShouldBe("2.00:00:00");
        catalog.Find("bookings.payment_link_validity")!.DefaultValue.ShouldBe("1.00:00:00");
        catalog.Find("bookings.no_show_delay")!.DefaultValue.ShouldBe("02:00:00");
        catalog.Find("bookings.escalation_delay")!.DefaultValue.ShouldBe("02:00:00");
        catalog.Find("bookings.cancellation_free_until_days")!.DefaultValue.ShouldBe("3");
        catalog.Find("bookings.cancellation_fee_percent")!.DefaultValue.ShouldBe("0.20");
        catalog.Find("bookings.validation_payment_mode")!.DefaultValue.ShouldBe("CaptureAtValidation");
        catalog.Find("bookings.hold_duration").ShouldBeNull("a platform setting of the Referential module");
        BookingSettings.All.Count.ShouldBe(8);
    }

    [Theory]
    [InlineData("CaptureAtValidation", true)]
    [InlineData("PaymentLinkAfterValidation", true)]
    [InlineData("2", false)]
    [InlineData("captureatvalidation", false)]
    [InlineData("", false)]
    public void The_validation_payment_mode_is_stored_by_name(string value, bool valid)
    {
        BookingSettings.ValidationPaymentMode.Validate(value).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void Contracts_services_jobs_and_seeder_are_registered()
    {
        var services = new ServiceCollection().AddBookingsModule();

        foreach (var type in new[]
        {
            typeof(IBookingsQueries), typeof(IBookingRequests), typeof(IBookingPaymentStatus), typeof(QuickBookingService),
            typeof(BookingWorkflowService), typeof(BookingAmendmentService), typeof(VehicleAssignmentService), typeof(RentalLifecycleService),
            typeof(BookingAdministrationQueries),
        })
        {
            services.ShouldContain(d => d.ServiceType == type, type.Name);
        }

        services.Where(d => d.ServiceType == typeof(RecurringJobDescriptor))
            .Select(d => ((RecurringJobDescriptor)d.ImplementationInstance!).JobId)
            .ShouldBe(["bookings.expire", "bookings.escalate"], ignoreOrder: true);
        services.ShouldContain(d => d.ServiceType == typeof(BookingExpiryJob));
        services.ShouldContain(d => d.ServiceType == typeof(IDataSeeder) && d.ImplementationType == typeof(BookingsDemoSeeder));
    }
}
