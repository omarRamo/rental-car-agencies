using CarRental.Application.Fleet;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Tests.Fleet;

public sealed class VehicleCategoryServiceTests
{
    private readonly FleetTestBed _bed = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CategoryForm Form(string code = "SDAR") =>
        new(code, "Berline", "Hyundai Elantra", 5, 4, 3, 1, FuelType.Petrol, 900m, 450m, 6);

    [Theory]
    [InlineData(Roles.Agent)]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Only_fleet_managers_administer_categories(string role)
    {
        var category = _bed.AddCategory();
        var service = _bed.Categories(role);

        (await service.CreateAsync(Form(), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.UpdateAsync(category.Id, Form(), Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.SetActiveAsync(category.Id, false, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await service.DeleteAsync(category.Id, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        _bed.Db.Fake<VehicleCategory>().Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_category_is_created_in_the_agency_of_the_operation_with_its_deposits()
    {
        var result = await _bed.Categories().CreateAsync(Form(" sdar "), Ct);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        var category = _bed.Db.Fake<VehicleCategory>().Items.ShouldHaveSingleItem();
        category.AgencyId.ShouldBe(_bed.AgencyId);
        category.Code.Value.ShouldBe("SDAR");
        category.Transmission.ShouldBe(Transmission.Automatic);
        category.Deposit.ShouldBe(Money.Tnd(900m));
        category.ReducedDeposit.ShouldBe(Money.Tnd(450m));
    }

    [Fact]
    public async Task A_staff_member_creates_the_categories_of_their_own_agency_only()
    {
        var sousse = Guid.CreateVersion7();
        _bed.Referential.Agencies.Add(new AgencyInfo(sousse, "Agence Sousse", "Sousse", null, null, IsActive: true));

        (await _bed.Categories().CreateAsync(Form() with { AgencyId = sousse }, Ct)).Error.ShouldBe(FleetErrors.Forbidden);
        (await _bed.Categories().CreateAsync(Form() with { AgencyId = _bed.AgencyId }, Ct)).IsSuccess.ShouldBeTrue();
        _bed.Db.Fake<VehicleCategory>().Items.ShouldAllBe(c => c.AgencyId == _bed.AgencyId);
    }

    [Fact]
    public async Task The_support_outside_any_agency_names_the_agency_of_the_category()
    {
        var sousse = Guid.CreateVersion7();
        _bed.Referential.Agencies.Add(new AgencyInfo(sousse, "Agence Sousse", "Sousse", null, null, IsActive: true));
        var support = _bed.Categories(Roles.Support);

        (await support.CreateAsync(Form(), Ct)).Error.ShouldBe(FleetErrors.AgencyRequired);
        (await support.CreateAsync(Form() with { AgencyId = Guid.CreateVersion7() }, Ct)).Error.ShouldBe(FleetErrors.AgencyRequired);
        (await support.CreateAsync(Form() with { AgencyId = sousse }, Ct)).IsSuccess.ShouldBeTrue();
        _bed.Db.Fake<VehicleCategory>().Items.ShouldHaveSingleItem().AgencyId.ShouldBe(sousse);
    }

    [Fact]
    public async Task An_acriss_code_is_unique_within_the_agency()
    {
        var existing = _bed.AddCategory("ECMR");
        var other = _bed.AddCategory("CDMR", FuelType.Diesel);

        (await _bed.Categories().CreateAsync(Form("ECMR"), Ct)).Error.ShouldBe(FleetErrors.CategoryCodeTaken);
        (await _bed.Categories().UpdateAsync(other.Id, Form("ECMR"), Ct)).Error.ShouldBe(FleetErrors.CategoryCodeTaken);
        (await _bed.Categories().UpdateAsync(existing.Id, Form("ECMR") with { Name = "Économique+" }, Ct)).IsSuccess.ShouldBeTrue();
        existing.Name.ShouldBe("Économique+");
    }

    [Fact]
    public async Task Invalid_input_is_reported()
    {
        (await _bed.Categories().CreateAsync(Form("ZZZZ"), Ct)).Error.ShouldBe(FleetErrors.CategoryCodeInvalid);
        (await _bed.Categories().CreateAsync(Form() with { Deposit = 100m, ReducedDeposit = 200m }, Ct)).Error.ShouldBe(FleetErrors.CategoryDepositInvalid);
        (await _bed.Categories().CreateAsync(Form() with { Deposit = 100.0001m }, Ct)).Error.ShouldBe(FleetErrors.CategoryDepositInvalid);
        (await _bed.Categories().UpdateAsync(Guid.CreateVersion7(), Form(), Ct)).Error.ShouldBe(FleetErrors.CategoryNotFound);
    }

    [Fact]
    public async Task Deactivating_a_category_is_signalled_to_the_availability_cache()
    {
        var category = _bed.AddCategory();

        (await _bed.Categories().SetActiveAsync(category.Id, false, Ct)).IsSuccess.ShouldBeTrue();

        category.IsActive.ShouldBeFalse();
        _bed.Notifier.Changed.ShouldBe([category.Id]);
        (await _bed.Queries().GetCategoriesAsync(_bed.AgencyId, cancellationToken: Ct)).ShouldBeEmpty();
        (await _bed.Queries().GetCategoriesAsync(_bed.AgencyId, includeInactive: true, Ct)).ShouldHaveSingleItem().IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task A_category_with_vehicles_cannot_be_deleted()
    {
        var used = _bed.AddCategory("ECMR");
        var unused = _bed.AddCategory("CDMR", FuelType.Diesel);
        _bed.AddVehicle(used);

        (await _bed.Categories().DeleteAsync(used.Id, Ct)).Error.ShouldBe(FleetErrors.CategoryHasVehicles);
        (await _bed.Categories().DeleteAsync(unused.Id, Ct)).IsSuccess.ShouldBeTrue();

        _bed.Db.Fake<VehicleCategory>().Items.ShouldBe([used]);
    }
}
