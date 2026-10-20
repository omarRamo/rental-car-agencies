using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

public sealed class VehicleDocumentTests
{
    [Fact]
    public void A_vehicle_becomes_compliant_once_its_three_mandatory_documents_are_recorded()
    {
        var vehicle = NewVehicle(compliant: false);

        vehicle.RecordDocument(Document(VehicleDocumentType.Insurance, new DateOnly(2027, 6, 30)), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.RecordDocument(Document(VehicleDocumentType.RoadTax, new DateOnly(2027, 3, 31)), Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.CompliantUntil.ShouldBeNull();
        vehicle.RecordDocument(Document(VehicleDocumentType.TechnicalInspection, new DateOnly(2027, 9, 1)), Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.CompliantUntil.ShouldBe(VehicleCompliance.EndOfTunisDay(new DateOnly(2027, 3, 31)));
        vehicle.DomainEvents.OfType<VehicleDocumentRecorded>().Count().ShouldBe(3);
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_renewal_supersedes_the_current_document_and_keeps_the_history()
    {
        var vehicle = NewVehicle();
        var first = vehicle.CurrentDocument(VehicleDocumentType.Insurance)!;
        var renewedAt = Now.AddDays(3);

        var renewal = vehicle.RecordDocument(Document(VehicleDocumentType.Insurance, new DateOnly(2028, 6, 30), "POL-2028"), Stamp(renewedAt)).Value;

        vehicle.CurrentDocument(VehicleDocumentType.Insurance).ShouldBe(renewal);
        first.IsCurrent.ShouldBeFalse();
        first.SupersededAtUtc.ShouldBe(renewedAt);
        renewal.IsCurrent.ShouldBeTrue();
        renewal.RecordedAtUtc.ShouldBe(renewedAt);
        renewal.ValidUntil.ShouldBe(VehicleCompliance.EndOfTunisDay(new DateOnly(2028, 6, 30)));
        vehicle.Documents.Count(d => d.Type == VehicleDocumentType.Insurance).ShouldBe(2);
        var history = vehicle.History[^1];
        history.Kind.ShouldBe(VehicleHistoryKind.DocumentRecorded);
        (history.Subject, history.PreviousValue, history.NewValue).ShouldBe(("Insurance", "2027-06-30", "2028-06-30"));
    }

    [Fact]
    public void The_registration_card_has_no_expiry_and_does_not_change_the_capacity()
    {
        var vehicle = NewVehicle();

        var card = vehicle.RecordDocument(new VehicleDocumentDetails(VehicleDocumentType.RegistrationCard, "CG-123456", new DateOnly(2024, 2, 15), null), Stamp()).Value;

        card.ExpiresOn.ShouldBeNull();
        card.ValidUntil.ShouldBeNull();
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldBeEmpty();
    }

    [Fact]
    public void Document_details_are_validated()
    {
        var vehicle = NewVehicle();
        var valid = Document(VehicleDocumentType.Insurance, new DateOnly(2027, 1, 1));

        vehicle.RecordDocument(valid with { Type = (VehicleDocumentType)9 }, Stamp()).Error.ShouldBe(FleetErrors.DocumentTypeInvalid);
        vehicle.RecordDocument(valid with { Number = "  " }, Stamp()).Error.ShouldBe(FleetErrors.DocumentNumberInvalid);
        vehicle.RecordDocument(valid with { Number = new string('n', VehicleDocument.NumberMaxLength + 1) }, Stamp()).Error.ShouldBe(FleetErrors.DocumentNumberInvalid);
        vehicle.RecordDocument(valid with { ExpiresOn = null }, Stamp()).Error.ShouldBe(FleetErrors.DocumentExpiryRequired);
        vehicle.RecordDocument(valid with { IssuedOn = new DateOnly(2027, 1, 2) }, Stamp()).Error.ShouldBe(FleetErrors.DocumentDatesInvalid);
        vehicle.RecordDocument(valid with { Provider = new string('p', VehicleDocument.ProviderMaxLength + 1) }, Stamp()).Error.ShouldBe(FleetErrors.DocumentTextTooLong);
        vehicle.RecordDocument(valid with { Notes = new string('n', VehicleDocument.NotesMaxLength + 1) }, Stamp()).Error.ShouldBe(FleetErrors.DocumentTextTooLong);
        vehicle.RecordDocument(valid with { Cost = Money.Tnd(-1m) }, Stamp()).Error.ShouldBe(FleetErrors.AmountInvalid);
        vehicle.RecordDocument(valid with { Number = " POL-1 ", Provider = " ", Notes = " Note " }, Stamp()).Value
            .ShouldSatisfyAllConditions(
                d => d.Number.ShouldBe("POL-1"),
                d => d.Provider.ShouldBeNull(),
                d => d.Notes.ShouldBe("Note"));
    }

    [Fact]
    public void Only_the_current_document_can_be_corrected_and_its_type_stays()
    {
        var vehicle = NewVehicle();
        var first = vehicle.CurrentDocument(VehicleDocumentType.RoadTax)!;
        var fixedDetails = Document(VehicleDocumentType.RoadTax, new DateOnly(2027, 2, 28), "VIG-FIXED");

        vehicle.CorrectDocument(first.Id, fixedDetails, Stamp()).IsSuccess.ShouldBeTrue();

        first.Number.ShouldBe("VIG-FIXED");
        first.ExpiresOn.ShouldBe(new DateOnly(2027, 2, 28));
        vehicle.CompliantUntil.ShouldBe(VehicleCompliance.EndOfTunisDay(new DateOnly(2027, 2, 28)));
        vehicle.DomainEvents.OfType<VehicleDocumentCorrected>().ShouldHaveSingleItem();
        vehicle.DomainEvents.OfType<VehicleAvailabilityChanged>().ShouldHaveSingleItem();

        vehicle.CorrectDocument(first.Id, fixedDetails with { Type = VehicleDocumentType.Insurance }, Stamp()).Error.ShouldBe(FleetErrors.DocumentTypeInvalid);
        vehicle.CorrectDocument(Guid.CreateVersion7(), fixedDetails, Stamp()).Error.ShouldBe(FleetErrors.DocumentNotFound);
        vehicle.RecordDocument(fixedDetails with { ExpiresOn = new DateOnly(2028, 2, 28) }, Stamp()).IsSuccess.ShouldBeTrue();
        vehicle.CorrectDocument(first.Id, fixedDetails, Stamp()).Error.ShouldBe(FleetErrors.DocumentNotCurrent);
        vehicle.CorrectDocument(vehicle.CurrentDocument(VehicleDocumentType.RoadTax)!.Id, fixedDetails with { ExpiresOn = null }, Stamp())
            .Error.ShouldBe(FleetErrors.DocumentExpiryRequired);
    }

    [Fact]
    public void An_expired_mandatory_document_makes_the_vehicle_unavailable_after_its_last_day()
    {
        var vehicle = NewVehicle(compliant: false);
        AddMandatoryDocuments(vehicle, new DateOnly(2027, 6, 30));
        vehicle.RecordDocument(Document(VehicleDocumentType.TechnicalInspection, new DateOnly(2026, 10, 17)), Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.AvailabilityWindows(P(Now, Now.AddDays(30)), Now).ShouldBeEmpty();
        vehicle.AvailabilityWindows(P(Tunis(2026, 10, 1), Tunis(2026, 10, 30)), Now).ShouldBe([P(Tunis(2026, 10, 1), Tunis(2026, 10, 18))]);
    }
}
