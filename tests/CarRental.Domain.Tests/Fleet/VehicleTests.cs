using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

public sealed class VehicleTests
{
    [Fact]
    public void Register_normalizes_the_plate_starts_active_and_raises_the_capacity_event()
    {
        var stamp = Stamp(by: " agent@demo.local ");

        var vehicle = Vehicle.Register(AgencyId, CategoryId, Details("  215   tu 4512 "), 12_500, stamp).Value;

        vehicle.PlateNumber.ShouldBe("215 TU 4512");
        vehicle.Status.ShouldBe(VehicleStatus.Active);
        vehicle.Mileage.ShouldBe(12_500);
        vehicle.StatusChangedAtUtc.ShouldBe(Now);
        vehicle.CompliantUntil.ShouldBeNull();
        vehicle.Revision.ShouldBeGreaterThan(0);
        vehicle.DomainEvents.OfType<VehicleRegistered>().ShouldHaveSingleItem().PlateNumber.ShouldBe("215 TU 4512");
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem().ShouldBe(new VehicleAvailabilityChanged(vehicle.Id, AgencyId, CategoryId, null));
        var history = vehicle.History.ShouldHaveSingleItem();
        history.Kind.ShouldBe(VehicleHistoryKind.Registered);
        history.Actor.ShouldBe("agent@demo.local");
        history.NewValue.ShouldBe("215 TU 4512");
    }

    [Fact]
    public void Register_validates_the_details()
    {
        Vehicle.Register(Guid.Empty, CategoryId, Details(), 0, Stamp()).Error.ShouldBe(FleetErrors.AgencyRequired);
        Vehicle.Register(AgencyId, Guid.Empty, Details(), 0, Stamp()).Error.ShouldBe(FleetErrors.CategoryNotFound);
        Vehicle.Register(AgencyId, CategoryId, Details("?"), 0, Stamp()).Error.ShouldBe(FleetErrors.PlateNumberInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { Make = " " }, 0, Stamp()).Error.ShouldBe(FleetErrors.MakeInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { Model = new string('m', Vehicle.ModelMaxLength + 1) }, 0, Stamp()).Error.ShouldBe(FleetErrors.ModelInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { Year = 1989 }, 0, Stamp()).Error.ShouldBe(FleetErrors.YearInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { Color = new string('c', Vehicle.ColorMaxLength + 1) }, 0, Stamp()).Error.ShouldBe(FleetErrors.ColorInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { Transmission = (Transmission)7 }, 0, Stamp()).Error.ShouldBe(FleetErrors.TransmissionInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { Fuel = (FuelType)0 }, 0, Stamp()).Error.ShouldBe(FleetErrors.FuelInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { Seats = 0 }, 0, Stamp()).Error.ShouldBe(FleetErrors.VehicleCapacityInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { LargeBags = 11 }, 0, Stamp()).Error.ShouldBe(FleetErrors.VehicleCapacityInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details() with { FirstRegistrationOn = new DateOnly(2024, 3, 2) }, 0, Stamp()).Error.ShouldBe(FleetErrors.InServiceDateInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details(), -1, Stamp()).Error.ShouldBe(FleetErrors.MileageInvalid);
        Vehicle.Register(AgencyId, CategoryId, Details(), Vehicle.MaxMileage + 1, Stamp()).Error.ShouldBe(FleetErrors.MileageInvalid);
    }

    [Fact]
    public void Updating_the_in_service_date_changes_the_capacity_but_other_details_do_not()
    {
        var vehicle = NewVehicle();

        vehicle.UpdateDetails(Details("215 TU 4513") with { Color = "Rouge" }, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldBeEmpty();
        vehicle.PlateNumber.ShouldBe("215 TU 4513");
        vehicle.Color.ShouldBe("Rouge");

        vehicle.UpdateDetails(Details("215 TU 4513", inServiceOn: new DateOnly(2024, 4, 1)), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();
        vehicle.DomainEvents.OfType<VehicleDetailsUpdated>().Count().ShouldBe(2);
    }

    [Fact]
    public void Changing_the_category_raises_the_capacity_event_with_both_categories()
    {
        var vehicle = NewVehicle();
        var target = Guid.CreateVersion7();

        vehicle.ChangeCategory(target, Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.CategoryId.ShouldBe(target);
        vehicle.DomainEvents.OfType<VehicleCategoryChanged>().ShouldHaveSingleItem().ShouldBe(new VehicleCategoryChanged(vehicle.Id, CategoryId, target));
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem().PreviousCategoryId.ShouldBe(CategoryId);
        vehicle.ChangeCategory(target, Stamp()).Error.ShouldBe(FleetErrors.SameCategory);
        vehicle.ChangeCategory(Guid.Empty, Stamp()).Error.ShouldBe(FleetErrors.CategoryNotFound);
    }

    [Theory]
    [InlineData(VehicleStatus.Active, VehicleStatus.Maintenance)]
    [InlineData(VehicleStatus.Active, VehicleStatus.OutOfService)]
    [InlineData(VehicleStatus.Maintenance, VehicleStatus.Active)]
    [InlineData(VehicleStatus.Maintenance, VehicleStatus.OutOfService)]
    [InlineData(VehicleStatus.OutOfService, VehicleStatus.Active)]
    [InlineData(VehicleStatus.OutOfService, VehicleStatus.Maintenance)]
    public void Valid_status_transitions_record_the_reason_and_change_the_capacity(VehicleStatus from, VehicleStatus to)
    {
        var vehicle = NewVehicle();
        if (from != VehicleStatus.Active)
        {
            vehicle.ChangeStatus(from, "Préparation", Stamp()).IsSuccess.ShouldBeTrue();
            vehicle.DequeueDomainEvents();
        }

        var at = Now.AddHours(1);
        vehicle.ChangeStatus(to, "  Panne moteur  ", Stamp(at)).IsSuccess.ShouldBeTrue();

        vehicle.Status.ShouldBe(to);
        vehicle.StatusReason.ShouldBe("Panne moteur");
        vehicle.StatusChangedAtUtc.ShouldBe(at);
        vehicle.DomainEvents.OfType<VehicleStatusChanged>().ShouldHaveSingleItem().ShouldBe(new VehicleStatusChanged(vehicle.Id, CategoryId, from, to, "Panne moteur"));
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();
        var entry = vehicle.History[^1];
        entry.Kind.ShouldBe(VehicleHistoryKind.StatusChanged);
        (entry.PreviousValue, entry.NewValue, entry.Reason).ShouldBe((from.ToString(), to.ToString(), "Panne moteur"));
    }

    [Fact]
    public void Invalid_status_changes_are_refused()
    {
        var vehicle = NewVehicle();

        vehicle.ChangeStatus(VehicleStatus.Active, null, Stamp()).Error.ShouldBe(FleetErrors.InvalidStatusTransition);
        vehicle.ChangeStatus(VehicleStatus.Sold, "vente", Stamp()).Error.ShouldBe(FleetErrors.InvalidStatusTransition);
        vehicle.ChangeStatus((VehicleStatus)42, "x", Stamp()).Error.ShouldBe(FleetErrors.InvalidStatusTransition);
        vehicle.ChangeStatus(VehicleStatus.OutOfService, " ", Stamp()).Error.ShouldBe(FleetErrors.ReasonRequired);
        vehicle.ChangeStatus(VehicleStatus.Maintenance, null, Stamp()).Error.ShouldBe(FleetErrors.ReasonRequired);
        vehicle.ChangeStatus(VehicleStatus.Maintenance, new string('r', Vehicle.ReasonMaxLength + 1), Stamp()).Error.ShouldBe(FleetErrors.ReasonTooLong);
        vehicle.Status.ShouldBe(VehicleStatus.Active);
        vehicle.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Mileage_never_decreases_unless_corrected_with_a_reason()
    {
        var vehicle = NewVehicle(mileage: 20_000);

        vehicle.RecordMileage(20_150, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.RecordMileage(20_150, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.RecordMileage(20_100, Stamp()).Error.ShouldBe(FleetErrors.MileageDecrease);
        vehicle.RecordMileage(-5, Stamp()).Error.ShouldBe(FleetErrors.MileageInvalid);
        vehicle.CorrectMileage(20_015, null, Stamp()).Error.ShouldBe(FleetErrors.ReasonRequired);
        vehicle.CorrectMileage(20_150, "Erreur", Stamp()).Error.ShouldBe(FleetErrors.MileageUnchanged);
        vehicle.CorrectMileage(Vehicle.MaxMileage + 1, "Erreur", Stamp()).Error.ShouldBe(FleetErrors.MileageInvalid);
        vehicle.CorrectMileage(20_015, "Erreur de saisie au retour", Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.Mileage.ShouldBe(20_015);
        vehicle.DomainEvents.OfType<VehicleMileageRecorded>().Select(e => (e.PreviousMileage, e.Mileage, e.IsCorrection))
            .ShouldBe([(20_000, 20_150, false), (20_150, 20_015, true)]);
        var correction = vehicle.History[^1];
        correction.Kind.ShouldBe(VehicleHistoryKind.MileageCorrected);
        correction.Reason.ShouldBe("Erreur de saisie au retour");
        (correction.PreviousValue, correction.NewValue).ShouldBe(("20150", "20015"));
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldBeEmpty();
    }

    [Fact]
    public void The_guaranteed_model_supplement_is_optional_and_non_negative()
    {
        var vehicle = NewVehicle();

        vehicle.SetGuaranteedModelSupplement(Money.Tnd(25m), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.SetGuaranteedModelSupplement(Money.Tnd(25m), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.SetGuaranteedModelSupplement(Money.Tnd(-1m), Stamp()).Error.ShouldBe(FleetErrors.AmountInvalid);
        vehicle.GuaranteedModelDailySupplement.ShouldBe(Money.Tnd(25m));
        vehicle.SetGuaranteedModelSupplement(null, Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.GuaranteedModelDailySupplement.ShouldBeNull();
        vehicle.DomainEvents.OfType<GuaranteedModelSupplementChanged>().Select(e => e.DailySupplement).ShouldBe([Money.Tnd(25m), null]);
    }

    [Fact]
    public void A_sale_ends_the_life_of_the_vehicle_and_cancels_its_planned_maintenance()
    {
        var vehicle = NewVehicle();
        var planned = vehicle.ScheduleMaintenance(Maintenance(Now.AddDays(5), Now.AddDays(6)), Stamp()).Value;
        vehicle.DequeueDomainEvents();
        var soldAt = Now.AddHours(-2);

        vehicle.Sell(soldAt, Money.Tnd(32_000m), "  Garage Ben Ali  ", Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.Status.ShouldBe(VehicleStatus.Sold);
        vehicle.SoldAtUtc.ShouldBe(soldAt);
        vehicle.SalePrice.ShouldBe(Money.Tnd(32_000m));
        vehicle.Buyer.ShouldBe("Garage Ben Ali");
        planned.Status.ShouldBe(MaintenanceStatus.Cancelled);
        planned.CancellationReason.ShouldBe(Vehicle.CancelledBySaleReason);
        vehicle.DomainEvents.OfType<VehicleSold>().ShouldHaveSingleItem().SalePrice.ShouldBe(Money.Tnd(32_000m));
        vehicle.DomainEvents.OfType<MaintenanceCancelled>().ShouldHaveSingleItem();
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();
        vehicle.AvailabilityWindows(P(Now, Now.AddDays(10)), Now).ShouldBeEmpty();
    }

    [Fact]
    public void A_sold_vehicle_can_no_longer_be_changed_except_its_economics()
    {
        var vehicle = NewVehicle();
        vehicle.Sell(Now, Money.Tnd(30_000m), null, Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.Sell(Now, Money.Tnd(1m), null, Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.ChangeStatus(VehicleStatus.Active, null, Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.RecordMileage(30_000, Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.UpdateDetails(Details(), Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.ChangeCategory(Guid.CreateVersion7(), Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.RecordDocument(Document(VehicleDocumentType.Insurance, new DateOnly(2028, 1, 1)), Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.ScheduleMaintenance(Maintenance(Now.AddDays(1), Now.AddDays(2)), Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.SetGuaranteedModelSupplement(Money.Tnd(5m), Stamp()).Error.ShouldBe(FleetErrors.VehicleSold);
        vehicle.SetEconomics(new VehicleEconomicsDetails(Money.Tnd(60_000m), new DateOnly(2024, 3, 1), FinancingType.Cash, null, null, 5, Money.Tnd(20_000m)), Stamp())
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_sale_is_neither_in_the_future_nor_before_the_vehicle_was_put_in_service()
    {
        var vehicle = NewVehicle(inServiceOn: new DateOnly(2024, 3, 1));

        vehicle.Sell(Now.AddMinutes(1), Money.Tnd(1m), null, Stamp()).Error.ShouldBe(FleetErrors.SaleDateInvalid);
        vehicle.Sell(Tunis(2024, 2, 29, 23), Money.Tnd(1m), null, Stamp()).Error.ShouldBe(FleetErrors.SaleDateInvalid);
        vehicle.Sell(Now, Money.Tnd(-1m), null, Stamp()).Error.ShouldBe(FleetErrors.AmountInvalid);
        vehicle.Sell(Now, Money.Tnd(1m), new string('b', Vehicle.BuyerMaxLength + 1), Stamp()).Error.ShouldBe(FleetErrors.BuyerTooLong);
        vehicle.Status.ShouldBe(VehicleStatus.Active);
    }

    [Fact]
    public void Every_change_increments_the_revision()
    {
        var vehicle = NewVehicle();
        var revision = vehicle.Revision;

        vehicle.AddCost(new DateOnly(2026, 10, 1), "Traceur GPS", Money.Tnd(350m), Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.Revision.ShouldBe(revision + 1);
    }

    [Fact]
    public void Costs_have_a_label_and_a_positive_amount()
    {
        var vehicle = NewVehicle();

        var cost = vehicle.AddCost(new DateOnly(2026, 10, 1), "  Frais d'immatriculation ", Money.Tnd(120m), Stamp()).Value;

        cost.Label.ShouldBe("Frais d'immatriculation");
        vehicle.AddCost(new DateOnly(2026, 10, 1), " ", Money.Tnd(1m), Stamp()).Error.ShouldBe(FleetErrors.CostLabelInvalid);
        vehicle.AddCost(new DateOnly(2026, 10, 1), "Zéro", Money.Zero, Stamp()).Error.ShouldBe(FleetErrors.CostLabelInvalid);
        vehicle.RemoveCost(Guid.CreateVersion7(), Stamp()).Error.ShouldBe(FleetErrors.CostNotFound);
        vehicle.RemoveCost(cost.Id, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.Costs.ShouldBeEmpty();
        vehicle.History.Select(h => h.Kind).TakeLast(2).ShouldBe([VehicleHistoryKind.CostAdded, VehicleHistoryKind.CostRemoved]);
    }

    [Fact]
    public void History_lines_are_numbered_in_order_even_within_the_same_instant()
    {
        var stamp = Stamp();
        var vehicle = NewVehicle();

        vehicle.RecordMileage(30_000, stamp);
        vehicle.AddCost(new DateOnly(2026, 10, 1), "Lavage", Money.Tnd(20m), stamp);
        vehicle.ChangeStatus(VehicleStatus.Maintenance, "Contrôle", stamp);

        vehicle.History.Select(h => h.Sequence).ShouldBe([.. Enumerable.Range(1, vehicle.History.Count)]);
        vehicle.LastHistorySequence.ShouldBe(vehicle.History.Count);
        vehicle.History.TakeLast(3).Select(h => h.Kind).ShouldBe(
            [VehicleHistoryKind.MileageRecorded, VehicleHistoryKind.CostAdded, VehicleHistoryKind.StatusChanged]);
    }

    [Fact]
    public void Photos_are_limited_and_removable()
    {
        var vehicle = NewVehicle();
        var photo = StoredPhoto.Create("vehicles/v/p.jpg", StoredPhoto.Jpeg, new string('b', 64), 10).Value;
        var ids = new List<Guid>();
        for (var i = 0; i < Vehicle.MaxPhotos; i++)
        {
            var id = Guid.CreateVersion7();
            vehicle.AddPhoto(id, photo, Stamp()).IsSuccess.ShouldBeTrue();
            ids.Add(id);
        }

        vehicle.AddPhoto(Guid.CreateVersion7(), photo, Stamp()).Error.ShouldBe(FleetErrors.TooManyPhotos);
        vehicle.RemovePhoto(ids[0], Stamp()).Value.Id.ShouldBe(ids[0]);
        vehicle.RemovePhoto(ids[0], Stamp()).Error.ShouldBe(FleetErrors.PhotoNotFound);
        vehicle.Photos.Count.ShouldBe(Vehicle.MaxPhotos - 1);
    }

    [Fact]
    public void Change_stamps_are_utc_and_trim_the_author()
    {
        var stamp = new ChangeStamp(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(1)), "  ");

        stamp.At.Offset.ShouldBe(TimeSpan.Zero);
        stamp.By.ShouldBeNull();
        stamp.Today.ShouldBe(new DateOnly(2026, 10, 20));
        new ChangeStamp(Now, new string('u', 300)).By!.Length.ShouldBe(AuditActor.UserNameMaxLength);
    }
}
