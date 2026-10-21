using CarRental.Domain.Billing;

namespace CarRental.Domain.Tests.Billing;

/// <summary>The seller and the buyer printed on a billing document: the mandatory parts of each.</summary>
public sealed class BillingPartiesTests
{
    [Fact]
    public void No_invoice_without_the_legal_name_the_tax_identifier_and_the_address_of_the_seller()
    {
        SellerIdentity.Create(null, "1234567/A/M/000", null, "Tunis").Error.ShouldBe(BillingErrors.SellerIdentityIncomplete);
        SellerIdentity.Create("Agence SARL", " ", null, "Tunis").Error.ShouldBe(BillingErrors.SellerIdentityIncomplete);
        SellerIdentity.Create("Agence SARL", "1234567/A/M/000", null, null).Error.ShouldBe(BillingErrors.SellerIdentityIncomplete);
        SellerIdentity.Create(new string('a', SellerIdentity.LegalNameMaxLength + 1), "1234567/A/M/000", null, "Tunis").Error.ShouldBe(BillingErrors.PartyFieldTooLong);

        var seller = SellerIdentity.Create("  Agence SARL ", "1234567/a/m/000", "  ", "Tunis").Value;
        (seller.LegalName, seller.TaxIdentifier, seller.TradeRegisterNumber).ShouldBe(("Agence SARL", "1234567/A/M/000", (string?)null));
    }

    [Fact]
    public void A_company_is_invoiced_with_its_tax_identifier_at_its_billing_address()
    {
        BuyerIdentity.Create(BuyerKind.Company, null, "Société SA", null, "Rue 1", null, null, "Tunis", "TN", null).Error.ShouldBe(BillingErrors.BuyerTaxIdentifierRequired);
        BuyerIdentity.Create(BuyerKind.Company, null, "Société SA", "7654321/B/A/000", null, null, null, "Tunis", "TN", null).Error.ShouldBe(BillingErrors.BuyerAddressRequired);
        BuyerIdentity.Create(BuyerKind.Individual, null, " ", null, null, null, null, null, null, null).Error.ShouldBe(BillingErrors.BuyerNameRequired);
        BuyerIdentity.Create(BuyerKind.Individual, null, "Sami", null, null, null, null, null, "TUN", null).Error.ShouldBe(BillingErrors.PartyFieldTooLong);
        BuyerIdentity.Create(BuyerKind.Individual, null, "Sami", null, null, null, null, null, null, "nobody").Error.ShouldBe(BillingErrors.EmailInvalid);

        var company = BuyerIdentity.Create(BuyerKind.Company, null, "Société SA", "7654321/b/a/000", "Rue 1", "Bloc B", "1002", "Tunis", "tn", "f@s.local").Value;
        company.TaxIdentifier.ShouldBe("7654321/B/A/000");
        company.AddressText.ShouldBe("Rue 1, Bloc B, 1002 Tunis, TN");

        var person = BuyerIdentity.Create(BuyerKind.Individual, null, "Sami", null, null, null, null, null, null, null).Value;
        person.AddressText.ShouldBeNull();
    }

    [Fact]
    public void The_actor_of_an_operation_keeps_a_bounded_name()
    {
        new BillingActor(null, null).Name.ShouldBe(BillingActor.SystemName);
        new BillingActor(Guid.CreateVersion7(), new string('x', 300)).Name.Length.ShouldBe(BillingActor.NameMaxLength);
    }
}
