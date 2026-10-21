using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.BookingTestData;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>Amendments, extensions and vehicle assignments of a rental file.</summary>
public sealed class BookingAmendmentTests
{
    [Fact]
    public void An_amendment_of_the_dates_attaches_the_new_quote_version_and_keeps_the_status()
    {
        var booking = Confirmed();
        var changes = booking.CurrentChanges with { PickupAt = PickupAt.AddDays(1), ReturnAt = ReturnAt.AddDays(2) };

        BookingTestData.Expect(booking.Amend(changes, QuoteRef(2, 610m), reason: null, manualDiscount: false, Agent, Now.AddHours(1)));

        booking.Status.ShouldBe(BookingStatus.Confirmed);
        booking.PickupAt.ShouldBe(PickupAt.AddDays(1));
        booking.ReturnAt.ShouldBe(ReturnAt.AddDays(2));
        booking.CurrentQuoteVersion.ShouldBe(2);
        booking.TotalInclVat.ShouldBe(Money.Tnd(610m));
        var row = booking.Events[^1];
        row.Type.ShouldBe(BookingEventType.Amended);
        row.StatusBefore.ShouldBe(BookingStatus.Confirmed);
        row.StatusAfter.ShouldBe(BookingStatus.Confirmed);
        row.Facts["pickupAt"].ShouldBe("2027-06-11T09:00:00Z → 2027-06-12T09:00:00Z");
        row.Facts["quoteVersion"].ShouldBe("2");
        var amended = booking.DomainEvents[^1].ShouldBeOfType<BookingAmended>();
        amended.QuoteVersion.ShouldBe(2);
        amended.IsExtension.ShouldBeFalse();
        amended.PreviousCategoryId.ShouldBeNull();
    }

    [Fact]
    public void A_change_of_a_price_input_needs_a_new_quote_version_and_quote_versions_only_go_forward()
    {
        var booking = Confirmed();
        var changes = booking.CurrentChanges with { ReturnAt = ReturnAt.AddDays(1) };

        Should.Throw<ArgumentException>(() => booking.Amend(changes, null, null, false, Agent, Now));
        Should.Throw<ArgumentException>(() => booking.Amend(changes, QuoteRef(1), null, false, Agent, Now));
        booking.Amend(booking.CurrentChanges, null, null, false, Agent, Now).Error.ShouldBe(BookingErrors.AmendmentNoChange);
    }

    [Fact]
    public void The_payer_and_the_notes_change_without_a_new_price()
    {
        var booking = Confirmed();
        var otherPayer = Guid.CreateVersion7();
        var changes = booking.CurrentChanges with { Parties = Parties with { PayerCustomerId = otherPayer }, Notes = "Société : facture au nom de l'employeur" };

        BookingTestData.Expect(booking.Amend(changes, null, null, false, Agent, Now));

        booking.PayerCustomerId.ShouldBe(otherPayer);
        booking.CurrentQuoteVersion.ShouldBe(1);
        booking.Events[^1].Facts["payer"].ShouldBe("changed");
        booking.Events[^1].Facts["notes"].ShouldBe("changed");
    }

    [Fact]
    public void A_manual_discount_needs_a_reason()
    {
        var booking = Confirmed();

        booking.Amend(booking.CurrentChanges, QuoteRef(2, 400m), null, manualDiscount: true, Manager, Now).Error.ShouldBe(BookingErrors.ReasonRequired);
        BookingTestData.Expect(booking.Amend(booking.CurrentChanges, QuoteRef(2, 400m), "Geste commercial : client fidèle", manualDiscount: true, Manager, Now));

        booking.TotalInclVat.ShouldBe(Money.Tnd(400m));
        booking.Events[^1].Reason.ShouldBe("Geste commercial : client fidèle");
    }

    [Fact]
    public void A_moved_pick_up_cannot_be_in_the_past()
    {
        var booking = Confirmed();
        var changes = booking.CurrentChanges with { PickupAt = Now.AddHours(-1) };

        booking.Amend(changes, QuoteRef(2), null, false, Agent, Now).Error.ShouldBe(BookingErrors.PickupInPast);
        booking.Amend(booking.CurrentChanges with { ReturnAt = PickupAt }, QuoteRef(2), null, false, Agent, Now).Error.ShouldBe(BookingErrors.PeriodInvalid);
    }

    [Fact]
    public void A_rental_in_progress_can_only_be_extended()
    {
        var booking = InStatus(BookingStatus.InProgress);
        var during = PickupAt.AddDays(1);

        booking.Amend(booking.CurrentChanges with { CategoryId = OtherCategoryId }, QuoteRef(2), null, false, Agent, during).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
        booking.Amend(booking.CurrentChanges with { ReturnAt = ReturnAt.AddHours(-2) }, QuoteRef(2), null, false, Agent, during).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
        booking.Amend(booking.CurrentChanges with { ReturnLocationId = AirportId, ReturnAt = ReturnAt.AddDays(1) }, QuoteRef(2), null, false, Agent, during).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
        booking.Amend(booking.CurrentChanges, QuoteRef(2, 300m), "Geste", manualDiscount: true, Manager, during).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
        booking.Amend(booking.CurrentChanges with { ReturnAt = ReturnAt.AddDays(1) }, QuoteRef(2), null, false, Agent, ReturnAt.AddDays(2)).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);

        BookingTestData.Expect(booking.Amend(booking.CurrentChanges with { ReturnAt = ReturnAt.AddDays(2) }, QuoteRef(2, 700m), null, false, Agent, during));

        booking.Status.ShouldBe(BookingStatus.InProgress);
        booking.ReturnAt.ShouldBe(ReturnAt.AddDays(2));
        booking.Events[^1].Type.ShouldBe(BookingEventType.Extended);
        booking.DomainEvents[^1].ShouldBeOfType<BookingAmended>().IsExtension.ShouldBeTrue();
        booking.AssignedVehicleId.ShouldBe(VehicleA);
    }

    [Fact]
    public void A_category_change_releases_the_vehicle_and_its_waiver()
    {
        var booking = Confirmed();
        BookingTestData.Expect(booking.AssignVehicle(VehicleA, "Visite technique renouvelée demain", null, Manager, Now));
        booking.IsComplianceWaived.ShouldBeTrue();

        BookingTestData.Expect(booking.Amend(booking.CurrentChanges with { CategoryId = OtherCategoryId }, QuoteRef(2), null, false, Agent, Now));

        booking.CategoryId.ShouldBe(OtherCategoryId);
        booking.AssignedVehicleId.ShouldBeNull();
        booking.ComplianceWaiver.ShouldBeNull();
        booking.DomainEvents[^1].ShouldBeOfType<BookingAmended>().PreviousCategoryId.ShouldBe(CategoryId);
    }

    [Fact]
    public void The_vehicle_can_be_released_with_the_amendment_but_not_a_guaranteed_one()
    {
        var booking = Confirmed();
        BookingTestData.Expect(booking.AssignVehicle(VehicleA, null, null, Agent, Now));

        BookingTestData.Expect(booking.Amend(booking.CurrentChanges with { ReturnAt = ReturnAt.AddDays(1), ReleaseVehicle = true }, QuoteRef(2), null, false, Agent, Now));
        booking.AssignedVehicleId.ShouldBeNull();
        booking.Events[^1].Facts["vehicleReleased"].ShouldBe(VehicleA.ToString());

        var guaranteed = Confirmed(guaranteedVehicleId: VehicleA);
        guaranteed.Amend(guaranteed.CurrentChanges with { ReturnAt = ReturnAt.AddDays(1), ReleaseVehicle = true }, QuoteRef(2), null, false, Agent, Now)
            .Error.ShouldBe(BookingErrors.VehicleGuaranteedModel);

        BookingTestData.Expect(guaranteed.Amend(guaranteed.CurrentChanges with { GuaranteedVehicleId = VehicleB }, QuoteRef(2), null, false, Agent, Now));
        guaranteed.AssignedVehicleId.ShouldBe(VehicleB);
        guaranteed.GuaranteedVehicleId.ShouldBe(VehicleB);

        BookingTestData.Expect(guaranteed.Amend(guaranteed.CurrentChanges with { GuaranteedVehicleId = null }, QuoteRef(3), null, false, Agent, Now));
        guaranteed.GuaranteedVehicleId.ShouldBeNull();
        guaranteed.AssignedVehicleId.ShouldBe(VehicleB, "removing the guarantee keeps the vehicle");
    }

    [Fact]
    public void A_waiver_records_its_author_and_applies_to_its_vehicle_only()
    {
        var booking = Confirmed();

        booking.AssignVehicle(VehicleA, "  ", null, Manager, Now).Error.ShouldBe(BookingErrors.ReasonRequired);
        BookingTestData.Expect(booking.AssignVehicle(VehicleA, "Assurance renouvelée, attestation attendue", null, Manager, Now.AddMinutes(5)));

        var waiver = booking.ComplianceWaiver.ShouldNotBeNull();
        waiver.VehicleId.ShouldBe(VehicleA);
        waiver.ByUserId.ShouldBe(Manager.UserId);
        waiver.ByName.ShouldBe(Manager.Name);
        waiver.AtUtc.ShouldBe(Now.AddMinutes(5));
        waiver.Reason.ShouldBe("Assurance renouvelée, attestation attendue");
        booking.Events[^1].Facts["waived"].ShouldBe("true");
        booking.DomainEvents[^1].ShouldBeOfType<BookingVehicleAssigned>().ComplianceWaived.ShouldBeTrue();

        BookingTestData.Expect(booking.AssignVehicle(VehicleB, null, null, Agent, Now.AddMinutes(10)));
        booking.ComplianceWaiver.ShouldBeNull();
        booking.IsComplianceWaived.ShouldBeFalse();
        booking.Events[^1].Facts["previousVehicle"].ShouldBe(VehicleA.ToString());
    }

    [Fact]
    public void The_same_vehicle_is_not_assigned_twice_and_unassigning_needs_a_vehicle()
    {
        var booking = Confirmed();

        booking.UnassignVehicle(Agent, Now).Error.ShouldBe(BookingErrors.VehicleNotAssigned);
        BookingTestData.Expect(booking.AssignVehicle(VehicleA, null, null, Agent, Now));
        booking.AssignVehicle(VehicleA, null, null, Agent, Now).Error.ShouldBe(BookingErrors.VehicleAlreadyAssigned);
        BookingTestData.Expect(booking.UnassignVehicle(Agent, Now));
        booking.AssignedVehicleId.ShouldBeNull();
        booking.Events[^1].Type.ShouldBe(BookingEventType.VehicleUnassigned);
        Should.Throw<ArgumentException>(() => booking.AssignVehicle(Guid.Empty, null, null, Agent, Now));
    }

    [Fact]
    public void During_the_rental_only_a_swap_with_a_reason_is_allowed()
    {
        var booking = InStatus(BookingStatus.InProgress);
        var during = PickupAt.AddDays(1);

        booking.AssignVehicle(VehicleB, null, null, Agent, during).Error.ShouldBe(BookingErrors.ReasonRequired);
        booking.AssignVehicle(VehicleA, null, "Panne", Agent, during).Error.ShouldBe(BookingErrors.VehicleAlreadyAssigned);
        booking.UnassignVehicle(Agent, during).Error.ShouldBe(BookingErrors.InvalidTransition);
        BookingTestData.Expect(booking.AssignVehicle(VehicleB, null, "Crevaison, véhicule remplacé à Hammamet", Agent, during));

        booking.AssignedVehicleId.ShouldBe(VehicleB);
        var row = booking.Events[^1];
        row.Type.ShouldBe(BookingEventType.VehicleSwapped);
        row.Reason.ShouldBe("Crevaison, véhicule remplacé à Hammamet");
        row.Facts["previousVehicle"].ShouldBe(VehicleA.ToString());
    }
}
