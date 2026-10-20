using CarRental.Domain.Customers;

namespace CarRental.Domain.Tests.Customers;

/// <summary>Payers: an individual linked to a person, or a company with its tax identifier and billing address.</summary>
public sealed class CustomerTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static PostalAddress Address() => PostalAddress.Create("Immeuble Les Jasmins, rue du Lac Turkana", "Bureau 4", "1053", "Les Berges du Lac", "TN").Value;

    private static CompanyDetails Company() => new(
        LegalName: "  Société Tunisienne de Services SARL ",
        TaxIdentifier: "1234567/a/m/000",
        BillingAddress: Address(),
        ContactName: "Mme Amel Jaziri",
        Email: "Compta@Example.tn",
        Phone: "71 000 111");

    [Fact]
    public void An_individual_customer_is_linked_to_a_person()
    {
        var personId = Guid.CreateVersion7();

        var customer = Customer.ForPerson(personId, Now).Value;

        customer.Kind.ShouldBe(CustomerKind.Individual);
        customer.PersonId.ShouldBe(personId);
        customer.LegalName.ShouldBeNull();
        customer.CreatedAtUtc.ShouldBe(Now);
        customer.DomainEvents.ShouldHaveSingleItem().ShouldBe(new CustomerRegistered(customer.Id, CustomerKind.Individual));
        Customer.ForPerson(Guid.Empty, Now).Error.ShouldBe(CustomersErrors.PersonRequired);
    }

    [Fact]
    public void A_company_customer_carries_its_tax_identifier_and_billing_address()
    {
        var customer = Customer.ForCompany(Company(), Now).Value;

        customer.Kind.ShouldBe(CustomerKind.Company);
        customer.PersonId.ShouldBeNull();
        customer.LegalName.ShouldBe("Société Tunisienne de Services SARL");
        customer.TaxIdentifier.ShouldBe("1234567/A/M/000");
        customer.TaxIdentifierKey.ShouldBe("1234567AM000");
        customer.BillingAddress.ShouldBe(Address());
        customer.ContactName.ShouldBe("Mme Amel Jaziri");
        customer.Email.ShouldBe("compta@example.tn");
        customer.Phone.ShouldBe("+21671000111");
    }

    [Theory]
    [InlineData("1234567AAM000")]
    [InlineData("1234567 A M 000")]
    [InlineData("0000000/A/M/000")]
    [InlineData("FR12345678901")]
    public void Tax_identifiers_are_validated_loosely(string taxIdentifier)
    {
        Customer.ForCompany(Company() with { TaxIdentifier = taxIdentifier }, Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEFG")]
    [InlineData("123")]
    [InlineData("1234567#A")]
    [InlineData("123456789012345678901234567890123")]
    public void Malformed_tax_identifiers_are_rejected(string taxIdentifier)
    {
        Customer.ForCompany(Company() with { TaxIdentifier = taxIdentifier }, Now).Error.ShouldBe(CustomersErrors.TaxIdentifierInvalid);
    }

    [Fact]
    public void Company_details_are_validated()
    {
        Customer.ForCompany(Company() with { LegalName = " " }, Now).Error.ShouldBe(CustomersErrors.LegalNameInvalid);
        Customer.ForCompany(Company() with { LegalName = new string('a', Customer.LegalNameMaxLength + 1) }, Now).Error.ShouldBe(CustomersErrors.LegalNameInvalid);
        Customer.ForCompany(Company() with { BillingAddress = null! }, Now).Error.ShouldBe(CustomersErrors.BillingAddressRequired);
        Customer.ForCompany(Company() with { Email = "nope" }, Now).Error.ShouldBe(CustomersErrors.EmailInvalid);
        Customer.ForCompany(Company() with { Phone = "1" }, Now).Error.ShouldBe(CustomersErrors.PhoneInvalid);
        Customer.ForCompany(Company() with { ContactName = new string('a', Customer.ContactNameMaxLength + 1) }, Now).Error.ShouldBe(CustomersErrors.ContactNameInvalid);
    }

    [Fact]
    public void Only_a_company_can_have_its_company_details_updated()
    {
        var company = Customer.ForCompany(Company(), Now).Value;
        company.DequeueDomainEvents();

        company.UpdateCompany(Company() with { LegalName = "STS SA", ContactName = null }, Now.AddDays(1)).IsSuccess.ShouldBeTrue();
        company.LegalName.ShouldBe("STS SA");
        company.ContactName.ShouldBeNull();
        company.LastModifiedAtUtc.ShouldBe(Now.AddDays(1));
        company.DomainEvents.ShouldHaveSingleItem().ShouldBe(new CompanyCustomerUpdated(company.Id));

        Customer.ForPerson(Guid.CreateVersion7(), Now).Value.UpdateCompany(Company(), Now).Error.ShouldBe(CustomersErrors.NotACompany);
    }

    [Fact]
    public void Postal_addresses_require_a_street_a_city_and_a_country()
    {
        var address = PostalAddress.Create("  12  rue de Marseille ", " ", " 1000 ", "Tunis", "tn").Value;

        address.Line1.ShouldBe("12 rue de Marseille");
        address.Line2.ShouldBeNull();
        address.PostalCode.ShouldBe("1000");
        address.City.ShouldBe("Tunis");
        address.Country.ShouldBe("TN");
        address.ShouldBe(PostalAddress.Create("12 rue de Marseille", null, "1000", "Tunis", "TN").Value);

        PostalAddress.Create("", null, null, "Tunis", "TN").Error.ShouldBe(CustomersErrors.AddressInvalid);
        PostalAddress.Create("12 rue", null, null, " ", "TN").Error.ShouldBe(CustomersErrors.AddressInvalid);
        PostalAddress.Create("12 rue", null, null, "Tunis", "XX").Error.ShouldBe(CustomersErrors.CountryInvalid);
        PostalAddress.Create(new string('a', PostalAddress.LineMaxLength + 1), null, null, "Tunis", "TN").Error.ShouldBe(CustomersErrors.AddressInvalid);
        PostalAddress.Create("12 rue", null, new string('9', PostalAddress.PostalCodeMaxLength + 1), "Tunis", "TN").Error.ShouldBe(CustomersErrors.AddressInvalid);
    }
}
