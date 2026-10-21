using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Files;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using static CarRental.Application.Tests.Bookings.Handover.HandoverHarness;

namespace CarRental.Application.Tests.Bookings.Handover;

/// <summary>The steps of a draft inspection: photos and signatures checked and stored, files released after the commit.</summary>
public sealed class HandoverInspectionServiceTests
{
    private static async Task<(HandoverHarness Harness, Booking Booking, Guid InspectionId)> DraftAsync()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        var inspectionId = Ok(await harness.Departures.StartAsync(booking.Id, Ct));
        Ok(await harness.Departures.ConfirmVehicleAsync(booking.Id, Ct));
        return (harness, booking, inspectionId);
    }

    [Fact]
    public async Task A_photo_is_checked_from_its_content_and_stored_with_its_thumbnail()
    {
        var (harness, booking, inspectionId) = await DraftAsync();

        (await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Front, new MemoryStream("GIF89a…"u8.ToArray()), null, cancellationToken: Ct))
            .Error.ShouldBe(HandoverErrors.ImageUnsupported);
        (await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Front, new MemoryStream([]), null, cancellationToken: Ct))
            .Error.ShouldBe(HandoverErrors.ImageEmpty);
        (await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Front, new MemoryStream(new byte[HandoverMedia.MaxPhotoLength + 1]), null, cancellationToken: Ct))
            .Error.ShouldBe(HandoverErrors.ImageTooLarge);
        harness.Storage.Files.ShouldBeEmpty("nothing stored for a refused file");

        var photoId = Ok(await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Front, new MemoryStream(Jpeg), new MemoryStream(Png), cancellationToken: Ct));

        var photo = harness.Bookings.Db.Rows<Inspection>().Single().Photos.ShouldHaveSingleItem();
        (photo.Id, photo.ContentType, photo.ThumbnailContentType).ShouldBe((photoId, HandoverFile.Jpeg, HandoverFile.Png));
        photo.StoragePath.ShouldBe($"bookings/{booking.Id:D}/{inspectionId:D}/{photoId:D}.jpg");
        harness.Storage.Files[(FileContainers.Inspections, photo.StoragePath)].Content.ShouldBe(Jpeg);
        harness.Storage.Files[(FileContainers.Inspections, photo.ThumbnailPath!)].Content.ShouldBe(Png);
    }

    [Fact]
    public async Task A_retake_deletes_the_previous_files_once_committed()
    {
        var (harness, _, inspectionId) = await DraftAsync();
        Ok(await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Dashboard, new MemoryStream(Jpeg), new MemoryStream(Jpeg), cancellationToken: Ct));
        var first = harness.Bookings.Db.Rows<Inspection>().Single().Photos.Single();

        Ok(await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Dashboard, new MemoryStream(Jpeg), new MemoryStream(Jpeg), cancellationToken: Ct));

        harness.Storage.Files.Count.ShouldBe(2);
        harness.Storage.Files.Keys.ShouldNotContain((FileContainers.Inspections, first.StoragePath));
        harness.Storage.Files.Keys.ShouldNotContain((FileContainers.Inspections, first.ThumbnailPath!));
    }

    [Fact]
    public async Task A_refused_change_deletes_the_file_it_stored()
    {
        var (harness, _, inspectionId) = await DraftAsync();

        (await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Damage, new MemoryStream(Jpeg), new MemoryStream(Jpeg), Guid.CreateVersion7(), Ct))
            .Error.ShouldBe(HandoverErrors.DamageNotFound);

        harness.Storage.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signature_is_a_png_and_voided_signatures_are_deleted()
    {
        var (harness, _, inspectionId) = await DraftAsync();

        (await harness.Inspections.SignAsync(inspectionId, SignatureRole.Customer, "Sami", new MemoryStream(Jpeg), Ct)).Error.ShouldBe(HandoverErrors.ImageUnsupported);
        (await harness.Inspections.SignAsync(inspectionId, SignatureRole.Customer, " ", new MemoryStream(Png), Ct)).Error.ShouldBe(HandoverErrors.SignerNameRequired);
        harness.Storage.Files.ShouldBeEmpty();

        await harness.SignBothAsync(inspectionId);
        harness.Storage.Files.Count.ShouldBe(2);

        Ok(await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(12_100, 7), Ct));

        harness.Bookings.Db.Rows<Inspection>().Single().Signatures.ShouldBeEmpty("the readings changed after the signatures");
        harness.Storage.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_paper_signature_replaces_the_drawn_ones_with_a_reason()
    {
        var (harness, _, inspectionId) = await DraftAsync();
        await harness.SignBothAsync(inspectionId);

        (await harness.Inspections.SignOnPaperAsync(inspectionId, "", Ct)).Error.ShouldBe(HandoverErrors.PaperReasonRequired);
        Ok(await harness.Inspections.SignOnPaperAsync(inspectionId, "Lecteur d'écran : signature sur le contrat imprimé", Ct));

        var inspection = harness.Bookings.Db.Rows<Inspection>().Single();
        inspection.Signatures.ShouldBeEmpty();
        inspection.PaperSignature.ShouldNotBeNull().ByName.ShouldBe(harness.Bookings.User.UserName);
        harness.Storage.Files.ShouldBeEmpty();
        Ok(await harness.Inspections.CancelPaperSignatureAsync(inspectionId, Ct));
        harness.Bookings.Db.Rows<Inspection>().Single().PaperSignature.ShouldBeNull();
    }

    [Fact]
    public async Task Readings_originals_damages_and_notes_are_kept_on_the_draft()
    {
        var (harness, booking, inspectionId) = await DraftAsync();

        (await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(11_000, 8), Ct)).Error.ShouldBe(HandoverErrors.MileageBelowReference);
        (await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(12_000, 8, keys: 9), Ct)).Error.ShouldBe(HandoverErrors.AccessoriesInvalid);
        Ok(await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(12_050, 5), Ct));
        (await harness.Inspections.RecordOriginalsAsync(inspectionId, [new DriverOriginals(Guid.CreateVersion7(), true, true)], Ct)).Error.ShouldBe(HandoverErrors.OriginalsUnknownDriver);
        Ok(await harness.Inspections.RecordOriginalsAsync(inspectionId, [new DriverOriginals(harness.Bookings.MainDriverId, true, false)], Ct));
        var damage = Ok(await harness.Inspections.AddDamageAsync(inspectionId, new DamageInput(DamageView.Top, 50m, 50m, DamageType.Stain, DamageSeverity.Minor, "Toit"), Ct));
        Ok(await harness.Inspections.UpdateDamageAsync(inspectionId, damage, new DamageInput(DamageView.Top, 55m, 45m, DamageType.Scratch, DamageSeverity.Minor, "Toit, rayure"), Ct));
        Ok(await harness.Inspections.SetNotesAsync(inspectionId, "Siège enfant fourni", Ct));
        (await harness.Inspections.SetCleaningNeededAsync(inspectionId, true, Ct)).Error.ShouldBe(HandoverErrors.InspectionKindMismatch);

        var view = Ok(await harness.Departures.GetContextAsync(booking.Id, Ct)).Inspection.ShouldNotBeNull();
        (view.Mileage, view.FuelEighths, view.Notes).ShouldBe((12_050, 5, "Siège enfant fourni"));
        view.Originals.ShouldHaveSingleItem().IsComplete.ShouldBeFalse();
        var mark = view.Damages.ShouldHaveSingleItem();
        (mark.Number, mark.X, mark.Type, mark.Note).ShouldBe((1, 55m, DamageType.Scratch, "Toit, rayure"));
        Ok(await harness.Inspections.RemoveDamageAsync(inspectionId, damage, Ct));
    }

    [Fact]
    public async Task A_draft_changes_only_while_its_file_allows_it_and_with_the_hand_over_permission()
    {
        var (harness, booking, inspectionId) = await DraftAsync();

        harness.As(Roles.Accountant);
        (await harness.Inspections.SetNotesAsync(inspectionId, "x", Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Inspections.UploadPhotoAsync(inspectionId, PhotoSlot.Front, new MemoryStream(Jpeg), null, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.Forbidden);

        harness.As(Roles.Agent);
        Ok(await harness.Bookings.Workflow.CancelAsync(booking.Id, "Le client annule", false, Ct));
        (await harness.Inspections.SetNotesAsync(inspectionId, "x", Ct)).Error.ShouldBe(BookingErrors.InvalidTransition);
        (await harness.Inspections.SetNotesAsync(Guid.CreateVersion7(), "x", Ct)).Error.ShouldBe(HandoverErrors.InspectionNotFound);
    }
}
