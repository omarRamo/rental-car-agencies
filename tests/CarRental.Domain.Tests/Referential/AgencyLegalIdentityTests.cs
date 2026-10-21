using CarRental.Domain.Referential;

namespace CarRental.Domain.Tests.Referential;

/// <summary>The legal identity of an agency, printed as the seller on its invoices (package 3A).</summary>
public sealed class AgencyLegalIdentityTests
{
    [Fact]
    public void The_legal_identity_is_trimmed_upper_cases_the_tax_identifier_and_raises_an_event()
    {
        var agency = Agency.Register(new AgencyDetails("Agence", "Tunis")).Value;
        agency.DequeueDomainEvents();

        agency.UpdateLegalIdentity(new AgencyLegalIdentity(" Agence SARL ", "1234567/a/m/000", " B01234 ", "5 rue de Rome, 1000 Tunis", "RIB 01 234")).IsSuccess.ShouldBeTrue();

        (agency.LegalName, agency.TaxIdentifier, agency.TradeRegisterNumber, agency.LegalAddress, agency.InvoiceFooter)
            .ShouldBe(("Agence SARL", "1234567/A/M/000", "B01234", "5 rue de Rome, 1000 Tunis", "RIB 01 234"));
        agency.DomainEvents.ShouldHaveSingleItem().ShouldBe(new AgencyLegalIdentityUpdated(agency.Id));
    }

    [Fact]
    public void Blank_fields_are_cleared_and_too_long_ones_rejected()
    {
        var agency = Agency.Register(new AgencyDetails("Agence", "Tunis", LegalName: "Agence SARL", TaxIdentifier: "1234567/A/M/000")).Value;

        agency.UpdateLegalIdentity(new AgencyLegalIdentity("Agence SARL", " ", null, "", null)).IsSuccess.ShouldBeTrue();
        (agency.TaxIdentifier, agency.LegalAddress).ShouldBe(((string?)null, (string?)null));

        agency.UpdateLegalIdentity(new AgencyLegalIdentity("Agence SARL", null, null, null, new string('x', Agency.InvoiceFooterMaxLength + 1)))
            .Error.ShouldBe(ReferentialErrors.AgencyFieldTooLong);
        agency.UpdateLegalIdentity(new AgencyLegalIdentity("Agence SARL", null, new string('1', Agency.TradeRegisterNumberMaxLength + 1), null, null))
            .Error.ShouldBe(ReferentialErrors.AgencyFieldTooLong);
        agency.LegalName.ShouldBe("Agence SARL");
    }
}
