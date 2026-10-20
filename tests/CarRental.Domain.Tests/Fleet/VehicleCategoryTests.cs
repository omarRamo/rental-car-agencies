using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

public sealed class VehicleCategoryTests
{
    [Fact]
    public void Create_derives_transmission_and_air_conditioning_from_the_acriss_code()
    {
        var category = VehicleCategory.Create(AgencyId, CategoryDetails(" edar ") with { Name = " Économique automatique " }).Value;

        category.Code.Value.ShouldBe("EDAR");
        category.Name.ShouldBe("Économique automatique");
        category.Transmission.ShouldBe(Transmission.Automatic);
        category.AirConditioning.ShouldBeTrue();
        category.IsActive.ShouldBeTrue();
        category.HasPhoto.ShouldBeFalse();
        category.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<VehicleCategoryCreated>().Code.Value.ShouldBe("EDAR");
    }

    [Fact]
    public void A_manual_code_without_air_conditioning_is_reflected()
    {
        var category = VehicleCategory.Create(AgencyId, CategoryDetails("MCMN")).Value;

        category.Transmission.ShouldBe(Transmission.Manual);
        category.AirConditioning.ShouldBeFalse();
    }

    [Theory]
    [InlineData("ECMD", FuelType.Petrol, false)]
    [InlineData("ECMD", FuelType.Diesel, true)]
    [InlineData("ECMV", FuelType.Petrol, true)]
    [InlineData("ECMH", FuelType.Hybrid, true)]
    [InlineData("ECME", FuelType.Electric, true)]
    [InlineData("ECML", FuelType.Lpg, true)]
    [InlineData("ECMR", FuelType.Diesel, true)]
    [InlineData("ECMR", FuelType.Electric, true)]
    public void The_fuel_must_match_the_code_when_the_code_states_one(string code, FuelType fuel, bool valid)
    {
        var result = VehicleCategory.Create(AgencyId, CategoryDetails(code, fuel));

        if (valid)
        {
            result.IsSuccess.ShouldBeTrue();
        }
        else
        {
            result.Error.ShouldBe(FleetErrors.CategoryFuelMismatch);
        }
    }

    [Fact]
    public void Invalid_details_are_rejected()
    {
        VehicleCategory.Create(Guid.Empty, CategoryDetails()).Error.ShouldBe(FleetErrors.AgencyRequired);
        VehicleCategory.Create(AgencyId, CategoryDetails("XX")).Error.ShouldBe(AcrissCodeErrors.InvalidLength);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Name = " " }).Error.ShouldBe(FleetErrors.CategoryNameInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Name = new string('a', VehicleCategory.NameMaxLength + 1) }).Error.ShouldBe(FleetErrors.CategoryNameInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { ExampleModel = "" }).Error.ShouldBe(FleetErrors.CategoryExampleModelInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Seats = 0 }).Error.ShouldBe(FleetErrors.CategoryCapacityInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Seats = 10 }).Error.ShouldBe(FleetErrors.CategoryCapacityInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Doors = 1 }).Error.ShouldBe(FleetErrors.CategoryCapacityInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { LargeBags = -1 }).Error.ShouldBe(FleetErrors.CategoryCapacityInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { SmallBags = 11 }).Error.ShouldBe(FleetErrors.CategoryCapacityInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Fuel = (FuelType)99 }).Error.ShouldBe(FleetErrors.CategoryFuelInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { DisplayOrder = -1 }).Error.ShouldBe(FleetErrors.CategoryDisplayOrderInvalid);
    }

    [Fact]
    public void The_reduced_deposit_never_exceeds_the_deposit()
    {
        VehicleCategory.Create(AgencyId, CategoryDetails() with { ReducedDeposit = Money.Tnd(600.001m) }).Error.ShouldBe(FleetErrors.CategoryDepositInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Deposit = Money.Tnd(-1m) }).Error.ShouldBe(FleetErrors.CategoryDepositInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { Deposit = Money.Of(600m, Currency.Eur), ReducedDeposit = Money.Of(1m, Currency.Eur) })
            .Error.ShouldBe(FleetErrors.CategoryDepositInvalid);
        VehicleCategory.Create(AgencyId, CategoryDetails() with { ReducedDeposit = Money.Tnd(600m) }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Update_replaces_the_details_and_raises_an_event()
    {
        var category = VehicleCategory.Create(AgencyId, CategoryDetails()).Value;
        category.DequeueDomainEvents();

        var result = category.Update(CategoryDetails("CDMR", FuelType.Diesel) with { Name = "Compacte", ExampleModel = "Volkswagen Golf", Deposit = Money.Tnd(800m), ReducedDeposit = Money.Tnd(400m) });

        result.IsSuccess.ShouldBeTrue();
        category.Code.Value.ShouldBe("CDMR");
        category.ExampleModel.ShouldBe("Volkswagen Golf");
        category.Deposit.ShouldBe(Money.Tnd(800m));
        category.ReducedDeposit.ShouldBe(Money.Tnd(400m));
        category.Fuel.ShouldBe(FuelType.Diesel);
        category.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<VehicleCategoryUpdated>();
        category.Update(CategoryDetails() with { Seats = 0 }).Error.ShouldBe(FleetErrors.CategoryCapacityInvalid);
        category.Code.Value.ShouldBe("CDMR");
    }

    [Fact]
    public void Activation_changes_raise_an_event_only_when_the_state_changes()
    {
        var category = VehicleCategory.Create(AgencyId, CategoryDetails()).Value;
        category.DequeueDomainEvents();

        category.Activate();
        category.Deactivate();
        category.Deactivate();
        category.Activate();

        category.IsActive.ShouldBeTrue();
        category.DomainEvents.OfType<VehicleCategoryActivationChanged>().Select(e => e.IsActive).ShouldBe([false, true]);
    }

    [Fact]
    public void A_photo_can_be_set_and_removed()
    {
        var category = VehicleCategory.Create(AgencyId, CategoryDetails()).Value;
        var photo = StoredPhoto.Create("categories/x/photo.jpg", StoredPhoto.Jpeg, new string('a', 64), 1234).Value;

        category.SetPhoto(photo);

        category.HasPhoto.ShouldBeTrue();
        category.PhotoContentType.ShouldBe(StoredPhoto.Jpeg);
        category.PhotoVersion.ShouldBe(new string('a', 16));
        category.RemovePhoto();
        category.HasPhoto.ShouldBeFalse();
        category.PhotoVersion.ShouldBeNull();
    }

    [Fact]
    public void Stored_photos_accept_jpeg_png_and_webp_up_to_ten_megabytes()
    {
        var hash = new string('0', 64);

        StoredPhoto.Create("a.png", StoredPhoto.Png, hash, StoredPhoto.MaxLength).IsSuccess.ShouldBeTrue();
        StoredPhoto.Create("a.webp", StoredPhoto.WebP, hash, 1).IsSuccess.ShouldBeTrue();
        StoredPhoto.Create("a.gif", "image/gif", hash, 1).Error.ShouldBe(FleetErrors.PhotoUnsupportedFormat);
        StoredPhoto.Create("a.jpg", StoredPhoto.Jpeg, hash, 0).Error.ShouldBe(FleetErrors.PhotoEmpty);
        StoredPhoto.Create("a.jpg", StoredPhoto.Jpeg, hash, StoredPhoto.MaxLength + 1).Error.ShouldBe(FleetErrors.PhotoTooLarge);
        Should.Throw<ArgumentException>(() => StoredPhoto.Create("", StoredPhoto.Jpeg, hash, 1));
        Should.Throw<ArgumentException>(() => StoredPhoto.Create("a.jpg", StoredPhoto.Jpeg, "ABC", 1));
        StoredPhoto.ExtensionOf(StoredPhoto.WebP).ShouldBe("webp");
        Should.Throw<ArgumentOutOfRangeException>(() => StoredPhoto.ExtensionOf("image/gif"));
    }
}
