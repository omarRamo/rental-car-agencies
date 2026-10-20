using CarRental.Application.Common.Paging;
using CarRental.Application.Customers.Administration;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Domain.Customers;

namespace CarRental.Application.Tests.Customers;

/// <summary>Back office services of the person page and of the companies: permissions, rules and the data shown.</summary>
public sealed class PersonAdministrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_blacklists_and_lifts_with_a_reason_and_is_recorded_as_the_author()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();

        (await harness.Persons.BlacklistAsync(person.Id, "", Ct)).Error.ShouldBe(CustomersErrors.BlacklistReasonInvalid);
        (await harness.Persons.BlacklistAsync(person.Id, "Dommages non déclarés au retour", Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Persons.LiftBlacklistAsync(person.Id, "Franchise réglée", Ct)).IsSuccess.ShouldBeTrue();

        var entry = person.BlacklistEntries.ShouldHaveSingleItem();
        entry.AddedByUserId.ShouldBe(harness.UserId);
        entry.AddedByName.ShouldBe(harness.UserName);
        entry.LiftReason.ShouldBe("Franchise réglée");
    }

    [Theory]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Users_without_customers_manage_change_nothing(string role)
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        harness.As(role);
        using var image = SampleFiles.Stream(SampleFiles.Jpeg());

        (await harness.Persons.BlacklistAsync(person.Id, "Incident grave", Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.Persons.VerifyDocumentAsync(person.Id, cin.Id, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.Persons.RemoveDocumentAsync(person.Id, cin.Id, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.Persons.UpdateAsync(person.Id, new PersonDetailsForm("Sami", "Trabelsi"), Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.Persons.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Front, image, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.Persons.SetConsentAsync(person.Id, ConsentPurposeInfo.MarketingEmail, ConsentChannelInfo.Web, true, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.Companies.UpdateAsync(Guid.CreateVersion7(), Company(), Ct)).Error.ShouldBe(CustomersErrors.Forbidden);

        person.IsBlacklisted.ShouldBeFalse();
        cin.IsVerified.ShouldBeFalse();
        harness.UnitOfWork.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task Verifying_a_document_records_the_agent()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;

        (await harness.Persons.VerifyDocumentAsync(person.Id, cin.Id, Ct)).IsSuccess.ShouldBeTrue();

        cin.VerifiedByUserId.ShouldBe(harness.UserId);
        cin.VerifiedAtUtc.ShouldBe(CustomersHarness.Start);
    }

    [Fact]
    public async Task Details_are_updated_with_validation()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();

        (await harness.Persons.UpdateAsync(person.Id, new PersonDetailsForm("Sami", "Trabelsi", "FR", Phone: "+33 6 39 98 00 05"), Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Persons.UpdateAsync(person.Id, new PersonDetailsForm("Sami", "Trabelsi", Email: "nope"), Ct)).Error.ShouldBe(CustomersErrors.EmailInvalid);
        (await harness.Persons.UpdateAsync(Guid.CreateVersion7(), new PersonDetailsForm("Sami", "Trabelsi"), Ct)).Error.ShouldBe(CustomersErrors.PersonNotFound);

        person.Nationality.ShouldBe("FR");
        person.Phone.ShouldBe("+33639980005");
    }

    [Fact]
    public async Task A_correction_cannot_take_the_document_number_of_another_person()
    {
        var harness = new CustomersHarness();
        harness.AddTunisian("Sami", "Trabelsi", phone: "20 111 001", cin: "08123456");
        var amel = harness.AddTunisian("Amel", "Ben Salah", phone: "22 222 002", cin: "09234567");
        var cin = amel.CurrentDocument(IdentityDocumentType.NationalId)!;

        var result = await harness.Persons.CorrectDocumentAsync(
            amel.Id,
            cin.Id,
            new IdentityDocumentInput(IdentityDocumentTypeInfo.NationalId, "08123456", "TN", new DateOnly(2016, 3, 1)),
            Ct);

        result.Error.ShouldBe(CustomersErrors.DocumentNumberInUse);
    }

    [Fact]
    public async Task Documents_are_added_and_removed()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();

        var added = await harness.Persons.AddDocumentAsync(
            person.Id,
            new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, "07/123456", "TN", new DateOnly(2008, 6, 15), IsLatinScript: false),
            Ct);
        added.IsSuccess.ShouldBeTrue();
        person.FindDocument(added.Value)!.IsLatinScript.ShouldBeFalse();

        (await harness.Persons.RemoveDocumentAsync(person.Id, added.Value, Ct)).IsSuccess.ShouldBeTrue();
        person.FindDocument(added.Value).ShouldBeNull();
    }

    [Fact]
    public async Task The_person_page_shows_documents_consents_blacklist_and_resolved_author_names()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        harness.Identity.Names[harness.UserId] = "Agent Démo";
        (await harness.Persons.VerifyDocumentAsync(person.Id, cin.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Persons.BlacklistAsync(person.Id, "Incident de démonstration", Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Persons.SetConsentAsync(person.Id, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Counter, true, Ct)).IsSuccess.ShouldBeTrue();

        var file = (await harness.AdministrationQueries.GetPersonFileAsync(person.Id, Ct)).Value;

        file.FullName.ShouldBe("Sami Trabelsi");
        file.IsBlacklisted.ShouldBeTrue();
        file.Blacklist.ShouldHaveSingleItem().AddedBy.ShouldBe("Agent Démo");
        var document = file.CurrentDocuments.ShouldHaveSingleItem();
        document.Number.ShouldBe("08123456");
        document.VerifiedBy.ShouldBe("Agent Démo");
        document.Front.ShouldBeNull();
        file.Consents.ShouldHaveSingleItem().GivenThrough.ShouldBe(ConsentChannelInfo.Counter);
        file.Permissions.ShouldBe(new PersonFilePermissions(CanManage: true, CanViewDocumentImages: true, CanManagePrivacy: false));
    }

    [Fact]
    public async Task The_accountant_reads_customers_with_masked_document_numbers()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        harness.As(Roles.Accountant);

        var file = (await harness.AdministrationQueries.GetPersonFileAsync(person.Id, Ct)).Value;
        var list = (await harness.AdministrationQueries.SearchPersonsAsync(null, new PageRequest(), Ct)).Value;

        file.CurrentDocuments.Single().Number.ShouldBe("••••3456");
        file.Permissions.ShouldBe(new PersonFilePermissions(CanManage: false, CanViewDocumentImages: false, CanManagePrivacy: false));
        list.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_preparer_cannot_read_customers()
    {
        var harness = new CustomersHarness(Roles.Preparer);

        (await harness.AdministrationQueries.SearchPersonsAsync("x", new PageRequest(), Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.AdministrationQueries.GetPersonFileAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.AdministrationQueries.SearchCompaniesAsync(null, new PageRequest(), Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
    }

    [Fact]
    public async Task The_persons_list_is_paged_and_flags_the_blacklist()
    {
        var harness = new CustomersHarness();
        harness.AddTunisian("Amel", "Ben Salah", phone: "22 222 002", cin: "09234567");
        var sami = harness.AddTunisian("Sami", "Trabelsi", phone: "20 111 001", cin: "08123456");
        harness.AddTunisian("Walid", "Ferchichi", phone: "29 666 015", cin: "07890123");
        (await harness.Persons.BlacklistAsync(sami.Id, "Chèque sans provision", Ct)).IsSuccess.ShouldBeTrue();

        var page = (await harness.AdministrationQueries.SearchPersonsAsync(null, new PageRequest(1, 2), Ct)).Value;
        var second = (await harness.AdministrationQueries.SearchPersonsAsync(null, new PageRequest(2, 2), Ct)).Value;

        page.TotalCount.ShouldBe(3);
        page.Items.Select(p => p.LastName).ShouldBe(["Ben Salah", "Ferchichi"]);
        second.Items.ShouldHaveSingleItem().IsBlacklisted.ShouldBeTrue();
    }

    [Fact]
    public async Task Companies_are_created_listed_and_updated_with_a_unique_tax_identifier()
    {
        var harness = new CustomersHarness();
        var first = (await harness.Companies.CreateAsync(Company(), Ct)).Value;
        var second = (await harness.Companies.CreateAsync(Company() with { LegalName = "Atlas Conseil SA", TaxIdentifier = "7654321/B/P/000" }, Ct)).Value;

        (await harness.Companies.UpdateAsync(second, Company() with { LegalName = "Atlas Conseil SA", TaxIdentifier = "1234567/A/M/000" }, Ct)).Error
            .ShouldBe(CustomersErrors.CompanyAlreadyExists);
        (await harness.Companies.UpdateAsync(first, Company() with { ContactName = "Hela Ayari" }, Ct)).IsSuccess.ShouldBeTrue();

        var list = (await harness.AdministrationQueries.SearchCompaniesAsync("tourisme", new PageRequest(), Ct)).Value;
        list.Items.ShouldHaveSingleItem().City.ShouldBe("Tunis");
        var company = (await harness.AdministrationQueries.GetCompanyAsync(first, Ct)).Value;
        company.ContactName.ShouldBe("Hela Ayari");
        (await harness.AdministrationQueries.GetCompanyAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(CustomersErrors.CustomerNotFound);
    }

    private static CompanyCustomerRequest Company() => new(
        "Société Méditerranéenne de Tourisme SARL",
        "1234567/A/M/000",
        new PostalAddressInfo("Rue du Lac Biwa", null, "1053", "Tunis", "TN"));
}
