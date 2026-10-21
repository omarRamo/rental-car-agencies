using CarRental.Domain.Bookings.Handover;
using static CarRental.Domain.Tests.Bookings.Handover.HandoverTestData;

namespace CarRental.Domain.Tests.Bookings.Handover;

/// <summary>The rental contract (ADR-0041): number of the file, version, the PDF and when it was sent.</summary>
public sealed class RentalContractTests
{
    [Fact]
    public void A_contract_carries_the_number_of_its_file_and_its_version()
    {
        var pdf = Pdf();

        var contract = RentalContract.Create(AgencyId, BookingId, Guid.CreateVersion7(), "LOC-2027-000123", 1, Guid.CreateVersion7(), 2, ContractLanguage.French, pdf, Agent, Now).Value;

        (contract.Number, contract.Version, contract.QuoteVersion, contract.Language).ShouldBe(("LOC-2027-000123", 1, 2, ContractLanguage.French));
        (contract.StoragePath, contract.ContentHash, contract.Length).ShouldBe((pdf.Path, pdf.ContentHash, pdf.Length));
        contract.FileName.ShouldBe("LOC-2027-000123-v1.pdf");
        contract.GeneratedBy.ShouldBe(Agent.Name);
        contract.SentTo.ShouldBeNull();
        contract.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RentalContractGenerated>().Number.ShouldBe("LOC-2027-000123");
    }

    [Fact]
    public void Sending_a_contract_records_the_address_and_the_instant()
    {
        var contract = RentalContract.Create(AgencyId, BookingId, Guid.CreateVersion7(), "LOC-2027-000124", 1, Guid.CreateVersion7(), 1, ContractLanguage.English, Pdf(), Agent, Now).Value;

        contract.MarkSent(" sami@example.com ", Now.AddSeconds(1));

        (contract.SentTo, contract.SentAtUtc).ShouldBe(("sami@example.com", Now.AddSeconds(1)));
        contract.DomainEvents.OfType<RentalContractSent>().ShouldHaveSingleItem();
    }

    [Fact]
    public void A_contract_is_a_pdf_of_a_known_file()
    {
        Should.Throw<ArgumentException>(() => RentalContract.Create(AgencyId, BookingId, Guid.CreateVersion7(), "LOC-2027-000125", 1, Guid.CreateVersion7(), 1, ContractLanguage.French, Image(), Agent, Now));
        Should.Throw<ArgumentException>(() => RentalContract.Create(AgencyId, Guid.Empty, Guid.CreateVersion7(), "LOC-2027-000125", 1, Guid.CreateVersion7(), 1, ContractLanguage.French, Pdf(), Agent, Now));
        Should.Throw<ArgumentException>(() => RentalContract.Create(AgencyId, BookingId, Guid.CreateVersion7(), " ", 1, Guid.CreateVersion7(), 1, ContractLanguage.French, Pdf(), Agent, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => RentalContract.Create(AgencyId, BookingId, Guid.CreateVersion7(), "LOC-2027-000125", 0, Guid.CreateVersion7(), 1, ContractLanguage.French, Pdf(), Agent, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => RentalContract.Create(AgencyId, BookingId, Guid.CreateVersion7(), "LOC-2027-000125", 1, Guid.CreateVersion7(), 1, (ContractLanguage)9, Pdf(), Agent, Now));
    }

    [Fact]
    public void A_stored_file_reference_has_a_path_a_type_and_a_sha256()
    {
        Should.Throw<ArgumentException>(() => HandoverFile.Create("", HandoverFile.Png, new string('a', 64), 10));
        Should.Throw<ArgumentException>(() => HandoverFile.Create(new string('p', HandoverFile.PathMaxLength + 1), HandoverFile.Png, new string('a', 64), 10));
        Should.Throw<ArgumentException>(() => HandoverFile.Create("a/b.png", " ", new string('a', 64), 10));
        Should.Throw<ArgumentException>(() => HandoverFile.Create("a/b.png", HandoverFile.Png, new string('A', 64), 10));
        Should.Throw<ArgumentException>(() => HandoverFile.Create("a/b.png", HandoverFile.Png, "abc", 10));
        Should.Throw<ArgumentOutOfRangeException>(() => HandoverFile.Create("a/b.png", HandoverFile.Png, new string('a', 64), 0));

        var file = HandoverFile.Create("a/b.png", HandoverFile.Png, new string('a', 64), 10);
        file.IsImage.ShouldBeTrue();
        HandoverFile.Create("a/b.pdf", HandoverFile.Pdf, new string('a', 64), 10).IsImage.ShouldBeFalse();
    }
}
