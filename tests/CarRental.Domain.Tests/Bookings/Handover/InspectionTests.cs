using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.Handover.HandoverTestData;

namespace CarRental.Domain.Tests.Bookings.Handover;

/// <summary>
/// The inspection rules (SPEC §6 "Départ" and "Retour", ADR-0041): vehicle, mileage that never goes back, fuel in
/// eighths, accessories, originals of every driver, the eight guided photos, damage marks on the schematic, signatures
/// (or the paper fallback with a reason), and what the completion requires.
/// </summary>
public sealed class InspectionTests
{
    [Fact]
    public void A_departure_starts_as_a_draft_without_vehicle_until_the_vehicle_step_confirms_it()
    {
        var inspection = Inspection.StartDeparture(AgencyId, BookingId, Agent, Now);

        inspection.Status.ShouldBe(InspectionStatus.Draft);
        inspection.Kind.ShouldBe(InspectionKind.Departure);
        inspection.VehicleId.ShouldBeNull();
        inspection.StartedBy.ShouldBe(Agent.Name);
        inspection.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<InspectionStarted>();
        inspection.RecordReadings(12_000, 8, FullKit).Error.ShouldBe(HandoverErrors.VehicleRequired);
        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Front, Image(), null, null, Agent, Now).Error.ShouldBe(HandoverErrors.VehicleRequired);

        inspection.ConfirmVehicle(VehicleId, 12_000, [], Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.VehicleId.ShouldBe(VehicleId);
        inspection.ReferenceMileage.ShouldBe(12_000);
    }

    [Fact]
    public void Confirming_the_vehicle_copies_the_damages_of_its_last_inspection_as_pre_existing()
    {
        var previous = CompletedDeparture();
        var known = previous.Damages.ShouldHaveSingleItem();

        var inspection = Departure(previous.Damages);

        var copied = inspection.Damages.ShouldHaveSingleItem();
        copied.IsPreExisting.ShouldBeTrue();
        copied.SourceMarkId.ShouldBe(known.Id);
        copied.Id.ShouldNotBe(known.Id);
        (copied.View, copied.X, copied.Y, copied.Type, copied.Severity, copied.Note).ShouldBe((known.View, known.X, known.Y, known.Type, known.Severity, known.Note));
    }

    [Fact]
    public void Another_vehicle_starts_the_vehicle_part_of_the_draft_again()
    {
        var inspection = ReadyDeparture();
        var photoPaths = inspection.Photos.SelectMany(p => new[] { p.StoragePath, p.ThumbnailPath! }).ToList();
        var signaturePaths = inspection.Signatures.Select(s => s.StoragePath).ToList();
        inspection.DequeueReleasedFiles();

        inspection.ConfirmVehicle(OtherVehicleId, 30_000, [], Agent, Now).IsSuccess.ShouldBeTrue();

        inspection.VehicleId.ShouldBe(OtherVehicleId);
        inspection.ReferenceMileage.ShouldBe(30_000);
        inspection.Photos.ShouldBeEmpty();
        inspection.Mileage.ShouldBeNull();
        inspection.FuelEighths.ShouldBeNull();
        inspection.Signatures.ShouldBeEmpty();
        inspection.ReleasedFiles.ShouldBe([.. photoPaths, .. signaturePaths], ignoreOrder: true);
        inspection.Originals.ShouldHaveSingleItem().IsComplete.ShouldBeTrue("the originals are about the drivers, not the vehicle");
    }

    [Fact]
    public void The_mileage_never_goes_below_the_last_known_mileage_and_the_fuel_is_counted_in_eighths()
    {
        var inspection = Departure(lastKnownMileage: 12_000);

        inspection.RecordReadings(11_999, 8, FullKit).Error.ShouldBe(HandoverErrors.MileageBelowReference);
        inspection.RecordReadings(-1, 8, FullKit).Error.ShouldBe(HandoverErrors.MileageInvalid);
        inspection.RecordReadings(Inspection.MaxMileage + 1, 8, FullKit).Error.ShouldBe(HandoverErrors.MileageInvalid);
        inspection.RecordReadings(12_000, 9, FullKit).Error.ShouldBe(HandoverErrors.FuelInvalid);
        inspection.RecordReadings(12_000, -1, FullKit).Error.ShouldBe(HandoverErrors.FuelInvalid);

        inspection.RecordReadings(12_000, 0, FullKit).IsSuccess.ShouldBeTrue();
        inspection.RecordReadings(12_450, Inspection.FullTank, FullKit).IsSuccess.ShouldBeTrue();
        (inspection.Mileage, inspection.FuelEighths, inspection.Accessories).ShouldBe((12_450, 8, FullKit));
    }

    [Fact]
    public void The_return_mileage_is_at_least_the_departure_mileage()
    {
        var departure = CompletedDeparture();
        var inspection = Return(departure);

        inspection.ReferenceMileage.ShouldBe(departure.Mileage);
        inspection.RecordReadings(departure.Mileage!.Value - 1, 6, FullKit).Error.ShouldBe(HandoverErrors.MileageBelowReference);
        inspection.RecordReadings(departure.Mileage.Value, 6, FullKit).IsSuccess.ShouldBeTrue();
        inspection.RecordReadings(departure.Mileage.Value + 950, 6, FullKit).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Accessories_count_keys_and_child_seats_within_bounds_and_tell_what_is_missing_at_the_return()
    {
        AccessoryChecklist.Create(true, true, true, true, -1, 0, false, true).Error.ShouldBe(HandoverErrors.AccessoriesInvalid);
        AccessoryChecklist.Create(true, true, true, true, AccessoryChecklist.MaxKeys + 1, 0, false, true).Error.ShouldBe(HandoverErrors.AccessoriesInvalid);
        AccessoryChecklist.Create(true, true, true, true, 2, AccessoryChecklist.MaxChildSeats + 1, false, true).Error.ShouldBe(HandoverErrors.AccessoriesInvalid);

        var departure = AccessoryChecklist.Create(true, true, true, true, 2, 1, true, true).Value;
        var back = AccessoryChecklist.Create(true, false, true, false, 1, 1, true, true).Value;

        back.MissingComparedWith(departure).ShouldBe([AccessoryItem.Jack, AccessoryItem.SafetyVest, AccessoryItem.Keys]);
        departure.MissingComparedWith(back).ShouldBeEmpty("more than at the departure is never missing");
    }

    [Fact]
    public void The_originals_are_checked_for_the_drivers_of_the_file_only()
    {
        var inspection = Departure();
        IReadOnlyCollection<Guid> drivers = [MainDriverId, SecondDriverId];

        inspection.RecordOriginals([new DriverOriginals(Guid.CreateVersion7(), true, true)], drivers).Error.ShouldBe(HandoverErrors.OriginalsUnknownDriver);
        inspection.RecordOriginals([new DriverOriginals(MainDriverId, true, true), new DriverOriginals(MainDriverId, true, false)], drivers)
            .Error.ShouldBe(HandoverErrors.OriginalsUnknownDriver);

        inspection.RecordOriginals([new DriverOriginals(SecondDriverId, true, false), new DriverOriginals(MainDriverId, true, true)], drivers).IsSuccess.ShouldBeTrue();

        inspection.Originals.Count.ShouldBe(2);
        inspection.Originals.Single(o => o.PersonId == MainDriverId).IsComplete.ShouldBeTrue();
        inspection.Originals.Single(o => o.PersonId == SecondDriverId).IsComplete.ShouldBeFalse();
        inspection.DepartureIssues(drivers).ShouldContain(HandoverErrors.OriginalsIncomplete);
    }

    [Fact]
    public void Stored_originals_read_back_and_ignore_damaged_parts()
    {
        var originals = new[] { new DriverOriginals(MainDriverId, true, false), new DriverOriginals(SecondDriverId, false, true) };

        var stored = DriverOriginals.Format(originals);

        DriverOriginals.Parse(stored).ShouldBe(originals.OrderBy(o => o.PersonId), ignoreOrder: false);
        DriverOriginals.Parse($"{stored};not-a-guid:11;{Guid.CreateVersion7()}:1").Count.ShouldBe(2);
        DriverOriginals.Parse(null).ShouldBeEmpty();
    }

    [Fact]
    public void A_guided_view_holds_one_photo_and_a_retake_releases_the_previous_files()
    {
        var inspection = Departure();
        var first = inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Front, Image(), Image(), null, Agent, Now).Value;

        var retake = inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Front, Image(), Image(), null, Agent, Now).Value;

        inspection.Photos.ShouldHaveSingleItem().ShouldBe(retake);
        inspection.ReleasedFiles.ShouldBe([first.StoragePath, first.ThumbnailPath!]);
        inspection.MissingGuidedPhotos.ShouldNotContain(PhotoSlot.Front);
        inspection.MissingGuidedPhotos.Count.ShouldBe(PhotoSlots.Guided.Count - 1);

        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Other, Image(), null, null, Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Other, Image(), null, null, Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.Photos.Count.ShouldBe(3, "other photos add up");
    }

    [Fact]
    public void Photos_are_images_of_a_known_view_up_to_the_maximum()
    {
        var inspection = Departure();

        inspection.AddPhoto(Guid.CreateVersion7(), (PhotoSlot)99, Image(), null, null, Agent, Now).Error.ShouldBe(HandoverErrors.PhotoSlotInvalid);
        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Front, Pdf(), null, null, Agent, Now).Error.ShouldBe(HandoverErrors.ImageUnsupported);
        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Damage, Image(), null, Guid.CreateVersion7(), Agent, Now).Error.ShouldBe(HandoverErrors.DamageNotFound);
        for (var i = 0; i < Inspection.MaxPhotos; i++)
        {
            inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Other, Image(), null, null, Agent, Now).IsSuccess.ShouldBeTrue();
        }

        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Other, Image(), null, null, Agent, Now).Error.ShouldBe(HandoverErrors.TooManyPhotos);
        inspection.RemovePhoto(Guid.CreateVersion7()).Error.ShouldBe(HandoverErrors.PhotoNotFound);
        inspection.RemovePhoto(inspection.Photos[0].Id).IsSuccess.ShouldBeTrue();
        inspection.Photos.Count.ShouldBe(Inspection.MaxPhotos - 1);
    }

    [Theory]
    [InlineData(-0.01, 50)]
    [InlineData(100.01, 50)]
    [InlineData(50, -1)]
    [InlineData(50, 101)]
    public void A_damage_mark_lies_on_its_view(double x, double y)
    {
        var inspection = Departure();

        inspection.AddDamage(Scratch(x: (decimal)x, y: (decimal)y), Agent, Now).Error.ShouldBe(HandoverErrors.DamageInvalid);
    }

    [Fact]
    public void A_damage_has_a_known_view_type_and_severity_and_a_short_note()
    {
        var inspection = Departure();

        inspection.AddDamage(Scratch() with { View = (DamageView)42 }, Agent, Now).Error.ShouldBe(HandoverErrors.DamageInvalid);
        inspection.AddDamage(Scratch() with { Type = (DamageType)42 }, Agent, Now).Error.ShouldBe(HandoverErrors.DamageInvalid);
        inspection.AddDamage(Scratch() with { Severity = (DamageSeverity)42 }, Agent, Now).Error.ShouldBe(HandoverErrors.DamageInvalid);
        inspection.AddDamage(Scratch() with { Note = new string('a', DamageMark.NoteMaxLength + 1) }, Agent, Now).Error.ShouldBe(HandoverErrors.DamageNoteTooLong);

        var corner = inspection.AddDamage(Scratch(x: 0m, y: 100m) with { Note = "  " }, Agent, Now).Value;
        (corner.X, corner.Y, corner.Note).ShouldBe((0m, 100m, null));
        inspection.AddDamage(Scratch(x: 33.335m, y: 66.664m), Agent, Now).Value.X.ShouldBe(33.34m, "positions are kept with two decimals");
    }

    [Fact]
    public void Damages_are_pre_existing_at_the_departure_and_new_at_the_return_unless_said_otherwise()
    {
        var departure = Departure();
        departure.AddDamage(Scratch(preExisting: false), Agent, Now).Value.IsPreExisting.ShouldBeTrue();

        var inspection = Return();
        var known = inspection.Damages.ShouldHaveSingleItem();
        known.IsPreExisting.ShouldBeTrue();

        var dent = inspection.AddDamage(Scratch(DamageView.Rear) with { Type = DamageType.Dent, Severity = DamageSeverity.Moderate }, Agent, Now).Value;
        var missed = inspection.AddDamage(Scratch(DamageView.Top, preExisting: true), Agent, Now).Value;

        dent.IsPreExisting.ShouldBeFalse();
        missed.IsPreExisting.ShouldBeTrue();
        inspection.NewDamageIds.ShouldBe([dent.Id]);
    }

    [Fact]
    public void Removing_a_damage_removes_its_close_ups()
    {
        var inspection = Departure();
        var mark = inspection.AddDamage(Scratch(), Agent, Now).Value;
        var closeUp = inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Damage, Image(), Image(), mark.Id, Agent, Now).Value;
        closeUp.DamageMarkId.ShouldBe(mark.Id);

        inspection.RemoveDamage(mark.Id).IsSuccess.ShouldBeTrue();

        inspection.Damages.ShouldBeEmpty();
        inspection.Photos.ShouldBeEmpty();
        inspection.ReleasedFiles.ShouldContain(closeUp.StoragePath);
        inspection.RemoveDamage(mark.Id).Error.ShouldBe(HandoverErrors.DamageNotFound);
        inspection.UpdateDamage(mark.Id, Scratch()).Error.ShouldBe(HandoverErrors.DamageNotFound);
    }

    [Fact]
    public void A_signature_is_a_png_with_the_name_of_its_signer_and_replaces_the_previous_one()
    {
        var inspection = Departure();

        inspection.Sign(Guid.CreateVersion7(), SignatureRole.Customer, "Sami", Image(), Agent, Now).Error.ShouldBe(HandoverErrors.ImageUnsupported);
        inspection.Sign(Guid.CreateVersion7(), SignatureRole.Customer, " ", Png(), Agent, Now).Error.ShouldBe(HandoverErrors.SignerNameRequired);
        inspection.Sign(Guid.CreateVersion7(), SignatureRole.Customer, new string('n', InspectionSignature.SignerNameMaxLength + 1), Png(), Agent, Now)
            .Error.ShouldBe(HandoverErrors.SignerNameRequired);

        var first = inspection.Sign(Guid.CreateVersion7(), SignatureRole.Customer, "Sami Trabelsi", Png(), Agent, Now).Value;
        var again = inspection.Sign(Guid.CreateVersion7(), SignatureRole.Customer, "Sami Trabelsi", Png(), Agent, Now).Value;

        inspection.Signatures.ShouldHaveSingleItem().ShouldBe(again);
        inspection.ReleasedFiles.ShouldContain(first.StoragePath);
        inspection.IsSigned.ShouldBeFalse("the agent did not sign yet");
        inspection.Sign(Guid.CreateVersion7(), SignatureRole.Agent, "Agent", Png(), Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.IsSigned.ShouldBeTrue();
    }

    [Fact]
    public void Changing_what_was_signed_voids_the_signatures()
    {
        var inspection = ReadyDeparture();
        inspection.IsSigned.ShouldBeTrue();
        var signatures = inspection.Signatures.Select(s => s.StoragePath).ToList();

        inspection.RecordReadings(12_400, 8, FullKit).IsSuccess.ShouldBeTrue();

        inspection.Signatures.ShouldBeEmpty();
        inspection.IsSigned.ShouldBeFalse();
        inspection.ReleasedFiles.ShouldContain(signatures[0]);

        SignBoth(inspection);
        inspection.RecordReadings(12_400, 8, FullKit).IsSuccess.ShouldBeTrue();
        inspection.IsSigned.ShouldBeTrue("the same readings change nothing");
        inspection.AddDamage(Scratch(), Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.IsSigned.ShouldBeFalse();
        SignBoth(inspection);
        inspection.RecordOriginals([new DriverOriginals(MainDriverId, true, true)], Drivers).IsSuccess.ShouldBeTrue();
        inspection.IsSigned.ShouldBeTrue("the originals are not part of what the customer signs");
    }

    [Fact]
    public void The_paper_signature_is_the_accessible_fallback_and_needs_a_reason()
    {
        var inspection = Departure();
        SignBoth(inspection);

        inspection.SignOnPaper(" ok ", Agent, Now).Error.ShouldBe(HandoverErrors.PaperReasonRequired);
        inspection.SignOnPaper(new string('r', Inspection.ReasonMaxLength + 1), Agent, Now).Error.ShouldBe(HandoverErrors.PaperReasonRequired);
        inspection.SignOnPaper("Le client ne peut pas signer sur la tablette", Agent, Now).IsSuccess.ShouldBeTrue();

        inspection.Signatures.ShouldBeEmpty("the drawn signatures are dropped");
        inspection.PaperSignature.ShouldNotBeNull().Reason.ShouldBe("Le client ne peut pas signer sur la tablette");
        inspection.PaperSignature.ByName.ShouldBe(Agent.Name);
        inspection.IsSigned.ShouldBeTrue();

        inspection.CancelPaperSignature().IsSuccess.ShouldBeTrue();
        inspection.IsSigned.ShouldBeFalse();
        inspection.SignOnPaper("Tablette en panne", Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.Sign(Guid.CreateVersion7(), SignatureRole.Customer, "Sami", Png(), Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.PaperSignature.ShouldBeNull("signing on the tablet again ends the paper fallback");
    }

    [Fact]
    public void A_departure_needs_originals_readings_the_eight_guided_photos_and_signatures()
    {
        var inspection = Departure();

        inspection.DepartureIssues(Drivers).ShouldBe(
        [
            HandoverErrors.OriginalsIncomplete,
            HandoverErrors.ReadingsMissing,
            HandoverErrors.PhotosMissing,
            HandoverErrors.SignaturesMissing,
        ]);
        inspection.CompleteDeparture(VehicleId, Drivers, Now, Agent, Now).Error.ShouldBe(HandoverErrors.OriginalsIncomplete);

        var ready = ReadyDeparture();
        ready.RemovePhoto(ready.Photos.First(p => p.Slot == PhotoSlot.Boot).Id).IsSuccess.ShouldBeTrue();
        SignBoth(ready);
        ready.MissingGuidedPhotos.ShouldBe([PhotoSlot.Boot]);
        ready.CompleteDeparture(VehicleId, Drivers, Now, Agent, Now).Error.ShouldBe(HandoverErrors.PhotosMissing, "eight photos minimum at the departure");
        ready.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Other, Image(), null, null, Agent, Now).IsSuccess.ShouldBeTrue();
        SignBoth(ready);
        ready.CompleteDeparture(VehicleId, Drivers, Now, Agent, Now).Error.ShouldBe(HandoverErrors.PhotosMissing, "the guided views, not any eight photos");
    }

    [Fact]
    public void A_completed_departure_is_frozen()
    {
        var inspection = ReadyDeparture();
        var handedOverAt = Now.AddMinutes(-5);

        inspection.CompleteDeparture(OtherVehicleId, Drivers, handedOverAt, Agent, Now).Error.ShouldBe(HandoverErrors.VehicleChanged);
        inspection.CompleteDeparture(VehicleId, Drivers, handedOverAt, Agent, Now).IsSuccess.ShouldBeTrue();

        inspection.Status.ShouldBe(InspectionStatus.Completed);
        inspection.HandedOverAtUtc.ShouldBe(handedOverAt);
        inspection.CompletedBy.ShouldBe(Agent.Name);
        inspection.DomainEvents.OfType<InspectionCompleted>().ShouldHaveSingleItem().Mileage.ShouldBe(12_340);
        inspection.RecordReadings(12_500, 8, FullKit).Error.ShouldBe(HandoverErrors.InspectionCompleted);
        inspection.AddDamage(Scratch(), Agent, Now).Error.ShouldBe(HandoverErrors.InspectionCompleted);
        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Front, Image(), null, null, Agent, Now).Error.ShouldBe(HandoverErrors.InspectionCompleted);
        inspection.SignOnPaper("Trop tard", Agent, Now).Error.ShouldBe(HandoverErrors.InspectionCompleted);
        inspection.CompleteDeparture(VehicleId, Drivers, Now, Agent, Now).Error.ShouldBe(HandoverErrors.InspectionCompleted);
    }

    [Fact]
    public void Steps_of_one_kind_of_inspection_are_refused_on_the_other()
    {
        var departure = Departure();
        var back = Return();

        departure.SetCleaningNeeded(true).Error.ShouldBe(HandoverErrors.InspectionKindMismatch);
        back.RecordOriginals([], Drivers).Error.ShouldBe(HandoverErrors.InspectionKindMismatch);
        back.ConfirmVehicle(VehicleId, 1, [], Agent, Now).Error.ShouldBe(HandoverErrors.InspectionKindMismatch);
        back.DeferPayment(Money.Tnd(10m), true, false, "Société", Manager, Now).Error.ShouldBe(HandoverErrors.InspectionKindMismatch);
        back.DepartureIssues(Drivers).ShouldBe([HandoverErrors.InspectionKindMismatch]);
        departure.ReviseCharges([], Agent, Now).Error.ShouldBe(HandoverErrors.InspectionKindMismatch);
    }

    [Fact]
    public void A_manager_defers_the_balance_or_waives_the_deposit_with_a_reason()
    {
        var inspection = Departure();

        inspection.DeferPayment(Money.Tnd(120m), deferBalance: false, waiveDeposit: false, "Société", Manager, Now).Error.ShouldBe(HandoverErrors.NothingToDefer);
        inspection.DeferPayment(Money.Zero, deferBalance: true, waiveDeposit: false, "Société", Manager, Now).Error.ShouldBe(HandoverErrors.NothingToDefer);
        inspection.DeferPayment(Money.Tnd(120m), deferBalance: true, waiveDeposit: false, "  ", Manager, Now).Error.ShouldBe(HandoverErrors.ReasonRequired);

        inspection.DeferPayment(Money.Tnd(120m), deferBalance: true, waiveDeposit: true, "Société payée sur facture", Manager, Now).IsSuccess.ShouldBeTrue();

        var deferral = inspection.Deferral.ShouldNotBeNull();
        (deferral.BalanceDeferred, deferral.DepositWaived, deferral.ByName, deferral.Reason).ShouldBe((Money.Tnd(120m), true, Manager.Name, "Société payée sur facture"));
        inspection.DomainEvents.OfType<DeparturePaymentDeferred>().ShouldHaveSingleItem().DepositWaived.ShouldBeTrue();

        inspection.DeferPayment(Money.Tnd(120m), deferBalance: false, waiveDeposit: true, "Caution prise par la société", Manager, Now).IsSuccess.ShouldBeTrue();
        inspection.Deferral!.BalanceDeferred.ShouldBe(Money.Zero, "only the deposit is waived now");
        inspection.CancelDeferral().IsSuccess.ShouldBeTrue();
        inspection.Deferral.ShouldBeNull();
    }

    [Fact]
    public void A_return_is_completed_with_its_readings_its_charges_and_the_franchise()
    {
        var inspection = Return();
        var charges = ReturnCharges.Propose(new ReturnFacts(12_340, 12_900, 8, 6, 3, false, null), Policy(includedKmPerDay: 0));

        inspection.CompleteReturn(charges, Money.Tnd(600m), false, Now.AddDays(3), Agent, Now.AddDays(3)).Error.ShouldBe(HandoverErrors.ReadingsMissing);
        inspection.RecordReadings(12_900, 6, FullKit).IsSuccess.ShouldBeTrue();
        inspection.SetCleaningNeeded(true).IsSuccess.ShouldBeTrue();
        inspection.CompleteReturn(charges, Money.Tnd(600m), franchiseReduced: false, Now.AddDays(3), Agent, Now.AddDays(3)).IsSuccess.ShouldBeTrue();

        inspection.Status.ShouldBe(InspectionStatus.Completed);
        inspection.NeedsCleaning.ShouldBeTrue();
        inspection.Franchise.ShouldBe(Money.Tnd(600m));
        var fuel = inspection.Charges.ShouldHaveSingleItem();
        (fuel.Kind, fuel.Quantity, fuel.InclVat, fuel.Position, fuel.RecordedBy).ShouldBe((ReturnChargeKind.Fuel, 2, Money.Tnd(30m), 1, Agent.Name));
        inspection.ChargesTotal.ShouldBe(Money.Tnd(30m));
        inspection.DomainEvents.OfType<ReturnChargesRecorded>().ShouldHaveSingleItem().IsRevision.ShouldBeFalse();
    }

    [Fact]
    public void The_charges_of_a_completed_return_can_be_revised_but_not_those_of_a_draft()
    {
        var inspection = Return();
        var waived = ReturnChargeLine.FromInclVat(ReturnChargeKind.Cleaning, 1, Money.Zero, 0.19m, [], reason: "Geste commercial", proposedInclVat: Money.Tnd(30m));
        inspection.ReviseCharges([waived], Agent, Now).Error.ShouldBe(HandoverErrors.ChargesNotRevisable);
        inspection.RecordReadings(12_340, 8, FullKit).IsSuccess.ShouldBeTrue();
        inspection.CompleteReturn([], Money.Tnd(600m), false, Now, Agent, Now).IsSuccess.ShouldBeTrue();

        inspection.ReviseCharges([waived], Manager, Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        var charge = inspection.Charges.ShouldHaveSingleItem();
        charge.IsWaived.ShouldBeTrue();
        charge.RecordedBy.ShouldBe(Manager.Name);
        charge.RecordedAtUtc.ShouldBe(Now.AddHours(2));
        inspection.DomainEvents.OfType<ReturnChargesRecorded>().Last().IsRevision.ShouldBeTrue();
    }

    [Fact]
    public void Notes_are_short_and_trimmed()
    {
        var inspection = Departure();

        inspection.SetNotes(new string('x', Inspection.NotesMaxLength + 1)).Error.ShouldBe(HandoverErrors.NotesTooLong);
        inspection.SetNotes("  Rayure déjà signalée  ").IsSuccess.ShouldBeTrue();
        inspection.Notes.ShouldBe("Rayure déjà signalée");
    }

    [Fact]
    public void Every_change_bumps_the_revision_so_that_the_root_guards_its_children()
    {
        var inspection = Departure();
        var revision = inspection.Revision;

        inspection.AddPhoto(Guid.CreateVersion7(), PhotoSlot.Front, Image(), null, null, Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.Revision.ShouldBeGreaterThan(revision);
        revision = inspection.Revision;
        inspection.AddDamage(Scratch(), Agent, Now).IsSuccess.ShouldBeTrue();
        inspection.Revision.ShouldBeGreaterThan(revision);
    }
}
