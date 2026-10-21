using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Files;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Bookings.Handover.HandoverHarness;

namespace CarRental.Application.Tests.Bookings.Handover;

/// <summary>
/// The departure (ADR-0041): blocked while the amount due is unpaid or no deposit is held unless a manager deferred
/// them; the contract PDF generated, stored, attached to the e-mail of the main driver; the rental started in the same
/// unit of work as the completed inspection, the contract and the vehicle mileage.
/// </summary>
public sealed class DepartureServiceTests
{
    [Fact]
    public async Task A_departure_is_blocked_while_the_amount_due_is_unpaid_or_no_deposit_is_held()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        await harness.ReadyDepartureAsync(booking);

        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(HandoverErrors.BalanceDue);
        harness.Settle(booking, paid: 100m, deposit: false);
        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(HandoverErrors.BalanceDue, "partly paid");
        harness.Settle(booking, deposit: false);
        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(HandoverErrors.DepositMissing);

        booking.Status.ShouldBe(BookingStatus.Confirmed);
        harness.Renderer.Documents.ShouldBeEmpty("no contract before the money is in order");
        harness.Storage.Files.Keys.ShouldNotContain(k => k.Container == FileContainers.Contracts);
        harness.Emails.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Finishing_generates_the_contract_starts_the_rental_and_emails_the_pdf_to_the_main_driver()
    {
        var harness = new HandoverHarness();
        var person = harness.Bookings.Customers.Persons[harness.Bookings.MainDriverId];
        harness.Bookings.Customers.Persons[person.Id] = person with { Email = "sami@example.com" };
        var booking = await harness.ConfirmedAsync();
        var inspectionId = await harness.ReadyDepartureAsync(booking);
        harness.Settle(booking);

        var outcome = Ok(await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id, Language: ContractLanguage.English), Ct));

        booking.Status.ShouldBe(BookingStatus.InProgress);
        booking.ActualPickupAt.ShouldBe(harness.Bookings.PickupAt);
        var inspection = harness.Bookings.Db.Rows<Inspection>().Single(i => i.Id == inspectionId);
        inspection.Status.ShouldBe(InspectionStatus.Completed);
        inspection.HandedOverAtUtc.ShouldBe(harness.Bookings.PickupAt);

        var contract = harness.Bookings.Db.Rows<RentalContract>().ShouldHaveSingleItem();
        (contract.Id, contract.Number, contract.Version, contract.Language, contract.InspectionId).ShouldBe((outcome.ContractId, booking.Number, 1, ContractLanguage.English, inspectionId));
        (contract.QuoteId, contract.QuoteVersion).ShouldBe((booking.CurrentQuoteId, booking.CurrentQuoteVersion));
        var pdf = harness.Storage.Files[(FileContainers.Contracts, contract.StoragePath)];
        pdf.ContentType.ShouldBe("application/pdf");
        System.Text.Encoding.ASCII.GetString(pdf.Content).ShouldContain(booking.Number);

        var document = harness.Renderer.Documents.ShouldHaveSingleItem();
        document.IsPreview.ShouldBeFalse();
        document.Signatures.Customer.ShouldNotBeNull().Png.ToArray().ShouldBe(Png, "the drawn signatures are printed");
        document.Signatures.Agent.ShouldNotBeNull().SignerName.ShouldBe("Agent Tests");
        document.Inspection.Mileage.ShouldBe(12_340);
        document.Price.TotalInclVat.ShouldBe(booking.TotalInclVat);
        document.Price.BalanceDue.ShouldBe(Money.Zero);
        document.Drivers.ShouldHaveSingleItem().Documents.ShouldAllBe(d => d.MaskedNumber.StartsWith('•'));
        document.Drivers[0].Documents.Single(d => d.Type == IdentityDocumentTypeInfo.NationalId).MaskedNumber.ShouldEndWith("3456");

        var email = harness.Emails.Messages.ShouldHaveSingleItem();
        email.To.ShouldBe("sami@example.com");
        var attachment = email.Attachments.ShouldHaveSingleItem();
        (attachment.Container, attachment.Path, attachment.FileName, attachment.ContentType)
            .ShouldBe((FileContainers.Contracts, contract.StoragePath, $"{booking.Number}-v1.pdf", "application/pdf"));
        (contract.SentTo, outcome.EmailedTo).ShouldBe(("sami@example.com", "sami@example.com"));
        harness.Mileage.Calls.ShouldBe([(harness.Bookings.VehicleA.Id, 12_340, booking.Id, booking.Number)]);
        harness.Bookings.Calls.ShouldContain("commit");
    }

    [Fact]
    public async Task Without_an_e_mail_address_the_contract_is_kept_but_not_sent()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        await harness.ReadyDepartureAsync(booking);
        harness.Settle(booking);

        var outcome = Ok(await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct));

        outcome.EmailedTo.ShouldBeNull();
        harness.Emails.Messages.ShouldBeEmpty();
        harness.Bookings.Db.Rows<RentalContract>().ShouldHaveSingleItem().SentTo.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_departure_keeps_no_contract_and_deletes_its_pdf()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        var inspectionId = await harness.ReadyDepartureAsync(booking);
        harness.Settle(booking);
        harness.Bookings.Customers.Blacklisted.Add(harness.Bookings.MainDriverId);

        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(BookingErrors.CustomerCheckFailed);

        booking.Status.ShouldBe(BookingStatus.Confirmed);
        harness.Bookings.Db.Rows<RentalContract>().ShouldBeEmpty();
        harness.Storage.Files.Keys.ShouldNotContain(k => k.Container == FileContainers.Contracts);
        harness.Bookings.Db.Rows<Inspection>().Single(i => i.Id == inspectionId).IsDraft.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failure_of_the_fleet_rolls_the_whole_departure_back()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        await harness.ReadyDepartureAsync(booking);
        harness.Settle(booking);
        harness.Mileage.FailWith = Error.Validation("fleet.vehicle.mileage_decrease", "Lower than the vehicle mileage.");

        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.Code.ShouldBe("fleet.vehicle.mileage_decrease");

        harness.Bookings.Calls.ShouldContain("rollback");
        harness.Bookings.Notifier.Changed.ShouldBeEmpty("nothing committed");
        harness.Storage.Files.Keys.ShouldNotContain(k => k.Container == FileContainers.Contracts);
    }

    [Fact]
    public async Task The_departure_needs_a_complete_inspection()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        harness.Settle(booking);
        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(HandoverErrors.InspectionNotFound);

        var inspectionId = await harness.ReadyDepartureAsync(booking);
        var boot = harness.Bookings.Db.Rows<Inspection>().Single(i => i.Id == inspectionId).Photos.Single(p => p.Slot == PhotoSlot.Boot);
        Ok(await harness.Inspections.RemovePhotoAsync(inspectionId, boot.Id, Ct));

        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(HandoverErrors.PhotosMissing);
        booking.Status.ShouldBe(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task A_manager_defers_the_balance_and_waives_the_deposit_with_a_reason()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        await harness.ReadyDepartureAsync(booking);

        (await harness.Departures.DeferPaymentAsync(booking.Id, new DeferPaymentRequest(true, true, "Société payée sur facture"), Ct))
            .Error.ShouldBe(BookingErrors.Forbidden, "an agent cannot defer");
        harness.As(Roles.Manager);
        (await harness.Departures.DeferPaymentAsync(booking.Id, new DeferPaymentRequest(true, true, " "), Ct)).Error.ShouldBe(HandoverErrors.ReasonRequired);
        Ok(await harness.Departures.DeferPaymentAsync(booking.Id, new DeferPaymentRequest(true, true, "Société payée sur facture"), Ct));

        var context = Ok(await harness.Departures.GetContextAsync(booking.Id, Ct));
        context.Inspection!.Deferral.ShouldNotBeNull().BalanceDeferred.ShouldBe(booking.TotalInclVat);
        context.Issues.ShouldNotContain(HandoverErrors.BalanceDue);
        context.Issues.ShouldNotContain(HandoverErrors.DepositMissing);

        var outcome = Ok(await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct));
        booking.Status.ShouldBe(BookingStatus.InProgress);
        harness.Renderer.Documents.Last().Price.BalanceDeferred.ShouldBeTrue();
        harness.Renderer.Documents.Last().Deposit.Waived.ShouldBeTrue();
        outcome.Version.ShouldBe(1);
    }

    [Fact]
    public async Task A_deferral_covers_the_amount_due_it_was_given_for_only()
    {
        var harness = new HandoverHarness().As(Roles.Manager);
        var booking = await harness.ConfirmedAsync();
        await harness.ReadyDepartureAsync(booking);
        harness.Settle(booking, paid: 200m);
        Ok(await harness.Departures.DeferPaymentAsync(booking.Id, new DeferPaymentRequest(DeferBalance: true, WaiveDeposit: false, "Solde réglé au retour"), Ct));

        // A refund meanwhile: the amount due is now larger than what was deferred.
        harness.Payments.NetPaid[booking.Id] = Money.Tnd(150m);

        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(HandoverErrors.BalanceDue);
        Ok(await harness.Departures.CancelDeferralAsync(booking.Id, Ct));
        Ok(await harness.Departures.GetContextAsync(booking.Id, Ct)).Inspection!.Deferral.ShouldBeNull();
    }

    [Fact]
    public async Task The_draft_is_shared_and_another_vehicle_restarts_its_vehicle_part()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        var inspectionId = await harness.ReadyDepartureAsync(booking);
        Ok(await harness.Departures.StartAsync(booking.Id, Ct)).ShouldBe(inspectionId, "resumable: the same draft");
        var photos = harness.Storage.Files.Keys.Count(k => k.Container == FileContainers.Inspections);
        photos.ShouldBe(PhotoSlots.Guided.Count * 2 + 2, "photos, thumbnails and signatures");

        harness.As(Roles.Agent);
        Ok(await harness.Bookings.Assignments.AssignAsync(booking.Id, harness.Bookings.VehicleB.Id, cancellationToken: Ct));
        var context = Ok(await harness.Departures.GetContextAsync(booking.Id, Ct));
        context.VehicleChanged.ShouldBeTrue();
        context.Issues.ShouldContain(HandoverErrors.VehicleChanged);
        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(HandoverErrors.VehicleChanged);

        Ok(await harness.Departures.ConfirmVehicleAsync(booking.Id, Ct));

        var inspection = harness.Bookings.Db.Rows<Inspection>().Single(i => i.Id == inspectionId);
        inspection.VehicleId.ShouldBe(harness.Bookings.VehicleB.Id);
        inspection.Photos.ShouldBeEmpty();
        harness.Storage.Files.Keys.ShouldNotContain(k => k.Container == FileContainers.Inspections, "the files of the other vehicle are deleted");
    }

    [Fact]
    public async Task The_damages_of_the_last_inspection_of_the_vehicle_are_pre_existing()
    {
        var harness = new HandoverHarness();
        var first = await harness.InProgressAsync();
        var back = Ok(await harness.Returns.StartAsync(first.Id, Ct));
        Ok(await harness.Inspections.AddDamageAsync(back, new DamageInput(DamageView.Rear, 30m, 60m, DamageType.Dent, DamageSeverity.Moderate, "Pare-chocs"), Ct));
        Ok(await harness.Inspections.RecordReadingsAsync(back, Readings(12_500, 8), Ct));
        harness.Bookings.Clock.Advance(TimeSpan.FromDays(3));
        var preview = Ok(await harness.Returns.PreviewChargesAsync(first.Id, harness.Bookings.Clock.GetUtcNow(), Ct));
        Ok(await harness.Returns.CompleteAsync(new CompleteReturnRequest(first.Id, harness.Bookings.Clock.GetUtcNow(), [], preview.ProposalTotal), Ct));

        // The next rental of the same vehicle: the dent is known.
        harness.Bookings.Clock.Advance(TimeSpan.FromHours(4));
        var next = await harness.Bookings.ConfirmedAsync(harness.Bookings.Quick() with
        {
            PickupAt = harness.Bookings.Clock.GetUtcNow().AddHours(1),
            ReturnAt = harness.Bookings.Clock.GetUtcNow().AddDays(2),
        });
        Ok(await harness.Bookings.Assignments.AssignAsync(next.Id, harness.Bookings.VehicleA.Id, cancellationToken: Ct));
        Ok(await harness.Departures.ConfirmVehicleAsync(next.Id, Ct));

        var context = Ok(await harness.Departures.GetContextAsync(next.Id, Ct));
        var known = context.Inspection!.Damages.ShouldHaveSingleItem();
        (known.View, known.Type, known.IsPreExisting).ShouldBe((DamageView.Rear, DamageType.Dent, true));
        context.Previous.ShouldNotBeNull().BookingId.ShouldBe(first.Id);
    }

    [Fact]
    public async Task A_preparer_does_the_inspection_steps_and_leaves_the_counter_steps_to_an_agent()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        harness.As(Roles.Preparer);

        var inspectionId = Ok(await harness.Departures.StartAsync(booking.Id, Ct));
        Ok(await harness.Departures.ConfirmVehicleAsync(booking.Id, Ct));
        Ok(await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(12_340, 8), Ct));
        foreach (var slot in PhotoSlots.Guided)
        {
            Ok(await harness.Inspections.UploadPhotoAsync(inspectionId, slot, new MemoryStream(Jpeg), null, cancellationToken: Ct));
        }

        var context = Ok(await harness.Departures.GetContextAsync(booking.Id, Ct));
        context.Can.Counter.ShouldBeFalse();
        context.Can.ManagePayments.ShouldBeFalse();
        context.Can.DeferPayment.ShouldBeFalse();
        context.Can.AssignVehicle.ShouldBeFalse();
        context.Inspection!.Id.ShouldBe(inspectionId);
        context.Inspection.MissingGuidedPhotos.ShouldBeEmpty();

        (await harness.Inspections.RecordOriginalsAsync(inspectionId, [new DriverOriginals(harness.Bookings.MainDriverId, true, true)], Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Inspections.SignAsync(inspectionId, SignatureRole.Customer, "Sami Trabelsi", new MemoryStream(Png), Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Inspections.SignOnPaperAsync(inspectionId, "Tablette en panne", Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Departures.DeferPaymentAsync(booking.Id, new DeferPaymentRequest(true, true, "Pas le droit"), Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Departures.PreviewContractAsync(booking.Id, cancellationToken: Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        harness.Settle(booking);
        (await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct)).Error.ShouldBe(BookingErrors.Forbidden);

        // The agent takes the same draft over at the counter.
        harness.As(Roles.Agent);
        Ok(await harness.Inspections.RecordOriginalsAsync(inspectionId, [new DriverOriginals(harness.Bookings.MainDriverId, true, true)], Ct));
        await harness.SignBothAsync(inspectionId);
        Ok(await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct));
        booking.Status.ShouldBe(BookingStatus.InProgress);
    }

    [Fact]
    public async Task The_context_lists_the_drivers_documents_masked_and_what_blocks_the_departure()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();

        var context = Ok(await harness.Departures.GetContextAsync(booking.Id, Ct));

        context.Booking.Number.ShouldBe(booking.Number);
        context.Booking.QuoteDays.ShouldBe(3);
        context.Vehicle.ShouldNotBeNull().PlateNumber.ShouldBe(harness.Bookings.VehicleA.PlateNumber);
        context.Vehicle.IsCompliant.ShouldBeTrue();
        context.Inspection.ShouldBeNull();
        context.Issues.ShouldContain(HandoverErrors.InspectionNotFound);
        context.AmountDue.ShouldBe(booking.TotalInclVat);
        var driver = context.Drivers.ShouldHaveSingleItem();
        driver.FullName.ShouldBe("Sami Trabelsi");
        driver.Documents.Count.ShouldBe(2);
        driver.Documents.ShouldAllBe(d => d.MaskedNumber.Contains('•') && d.IsValidUntilReturn);
        driver.Eligibility.ShouldNotBeNull().IsEligible.ShouldBeTrue();

        harness.As(Roles.Accountant);
        (await harness.Departures.GetContextAsync(booking.Id, Ct)).Error.ShouldBe(BookingErrors.Forbidden);
    }

    [Fact]
    public async Task The_contract_preview_has_no_signature()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        await harness.ReadyDepartureAsync(booking);

        var preview = Ok(await harness.Departures.PreviewContractAsync(booking.Id, ContractLanguage.French, Ct));

        preview.IsPreview.ShouldBeTrue();
        preview.Number.ShouldBe(booking.Number);
        preview.Signatures.Customer.ShouldBeNull();
        preview.Vehicle.PlateNumber.ShouldBe(harness.Bookings.VehicleA.PlateNumber);
        preview.Price.Lines.ShouldNotBeEmpty();
        preview.Deposit.Required.ShouldBe(booking.Deposit);
        preview.TermsReference.ShouldBe(RentalContractTerms.Reference);
    }
}
