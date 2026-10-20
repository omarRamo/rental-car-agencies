using CarRental.Application.Pricing;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Pricing;

/// <summary>Settings, enumerations and error codes of the Pricing contracts.</summary>
public sealed class PricingContractTests
{
    public static TheoryData<Type, Type> MirroredEnums() => new()
    {
        { typeof(PriceLineKind), typeof(QuoteLineKind) },
        { typeof(PromoCodeStatus), typeof(PromoCodeOutcome) },
        { typeof(PromotionKind), typeof(PromotionKindInfo) },
        { typeof(RatePeriodKind), typeof(RatePeriodKindInfo) },
        { typeof(ChannelPricingMode), typeof(ChannelPricingModeInfo) },
        { typeof(ExtraKind), typeof(ExtraKindInfo) },
        { typeof(ExtraPricingMode), typeof(ExtraPricingModeInfo) },
        { typeof(SeasonLevel), typeof(SeasonLevelInfo) },
    };

    [Theory]
    [MemberData(nameof(MirroredEnums))]
    public void Contract_enums_have_the_names_and_values_of_the_domain_enums(Type domain, Type contract)
    {
        var domainMembers = Enum.GetValues(domain).Cast<object>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));
        var contractMembers = Enum.GetValues(contract).Cast<object>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));

        contractMembers.ShouldBe(domainMembers);
    }

    [Fact]
    public void The_module_declares_its_settings_in_the_catalogue()
    {
        using var provider = new ServiceCollection().AddReferentialModule().AddPricingModule().BuildServiceProvider();

        var catalog = provider.GetRequiredService<ISettingDefinitionCatalog>();

        catalog.Find("pricing.round_public_prices_to_dinar").ShouldBeSameAs(PricingSettings.RoundPublicPricesToDinar);
        catalog.Find("drivers.young_driver_age_below").ShouldBeSameAs(PricingSettings.YoungDriverAgeBelow);
        catalog.Find("pricing.opening_hours").ShouldBeSameAs(PricingSettings.AgencyOpeningHours);
        catalog.Find("drivers.minimum_age").ShouldBeNull();
        catalog.Find("drivers.minimum_license_years").ShouldBeNull();
    }

    [Fact]
    public void Settings_have_the_defaults_of_the_phase_1_design()
    {
        PricingSettings.RoundPublicPricesToDinar.DefaultValue.ShouldBe("true");
        PricingSettings.YoungDriverAgeBelow.Default.ShouldBe(25);
        PricingSettings.AgencyOpeningHours.DefaultValue.ShouldBe("08:00-20:00");
        PricingSettings.YoungDriverAgeBelow.Validate("17").IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("08:00-20:00", true)]
    [InlineData(" 07:30 - 22:15 ", true)]
    [InlineData("20:00-08:00", false)]
    [InlineData("08:00-08:00", false)]
    [InlineData("8h-20h", false)]
    [InlineData("08:00", false)]
    [InlineData("", false)]
    public void Opening_hours_are_stored_as_a_time_range(string value, bool valid)
    {
        PricingSettings.AgencyOpeningHours.Validate(value).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void Opening_hours_round_trip()
    {
        var hours = PricingSettings.AgencyOpeningHours.Parse(" 07:30 - 22:15 ").Value;

        hours.ShouldBe(new OpeningHours(new TimeOnly(7, 30), new TimeOnly(22, 15)));
        PricingSettings.AgencyOpeningHours.Format(hours).ShouldBe("07:30-22:15");
    }

    [Fact]
    public void Public_error_codes_match_the_domain_errors()
    {
        PricingErrorCodes.MinimumDurationNotMet.ShouldBe(PricingErrors.MinimumDurationNotMetCode);
        PricingErrorCodes.NoPublishedPlan.ShouldBe(PricingErrors.NoPublishedPlan.Code);
        PricingErrorCodes.ChannelPlanMissing.ShouldBe(PricingErrors.ChannelPlanMissing.Code);
        PricingErrorCodes.CategoryNotFound.ShouldBe(PricingErrors.CategoryNotFound.Code);
        PricingErrorCodes.CategoryNotPriced.ShouldBe(PricingErrors.CategoryNotPriced.Code);
        PricingErrorCodes.PeriodInvalid.ShouldBe(PricingErrors.PeriodInvalid.Code);
        PricingErrorCodes.ExtraUnknown.ShouldBe(PricingErrors.ExtraUnknown.Code);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(14)]
    public void The_required_minimum_is_read_back_from_the_error(int days)
    {
        var error = PricingErrors.MinimumDurationNotMet(days);

        PricingErrorCodes.TryGetRequiredMinimumDays(error, out var contract).ShouldBeTrue();
        PricingErrors.TryGetRequiredMinimumDays(error, out var domain).ShouldBeTrue();
        contract.ShouldBe(days);
        domain.ShouldBe(days);
        PricingErrorCodes.TryGetRequiredMinimumDays(PricingErrors.PeriodInvalid, out _).ShouldBeFalse();
    }

    [Fact]
    public void Services_are_registered_with_the_module()
    {
        var services = new ServiceCollection().AddPricingModule();

        services.ShouldContain(d => d.ServiceType == typeof(IPricingService) && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(IPricingQueries) && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(RatePlanAdministrationService) && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(QuoteCalculator));
    }
}
