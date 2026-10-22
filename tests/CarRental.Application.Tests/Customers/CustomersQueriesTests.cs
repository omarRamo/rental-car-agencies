using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Application.Tests.Customers;

/// <summary>The read contract of the module: search without duplicates, masked numbers, blacklist, eligibility.</summary>
public sealed class CustomersQueriesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CustomersHarness WithPersons()
    {
        var harness = new CustomersHarness();
        harness.AddTunisian("Hédi", "Ben Salah", phone: "98 123 456", email: "hedi.bensalah@example.com", cin: "08123456");
        harness.AddTunisian("Amel", "Ben Salah", phone: "22 222 002", cin: "09234567");
        var julien = Person.Register(TestUsers.AgencyId, new PersonDetails("Julien", "Moreau", "FR", new DateOnly(1983, 6, 5), "+33 6 39 98 00 07", "julien.moreau@example.org"), CustomersHarness.Start).Value;
        julien.AddDocument(
            new IdentityDocumentDetails(IdentityDocumentType.Passport, "21AB12345", "FR", new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1)),
            CustomersHarness.Start).IsSuccess.ShouldBeTrue();
        harness.Add(julien);
        return harness;
    }

    [Theory]
    [InlineData("ben salah", new[] { "Amel", "Hédi" })]
    [InlineData("BEN-SALAH hedi", new[] { "Hédi" })]
    [InlineData("Hédi", new[] { "Hédi" })]
    [InlineData("+216 98 123 456", new[] { "Hédi" })]
    [InlineData("98123456", new[] { "Hédi" })]
    [InlineData("0033639980007", new[] { "Julien" })]
    [InlineData("39 98 00 07", new[] { "Julien" })]
    [InlineData("08123456", new[] { "Hédi" })]
    [InlineData("21ab", new[] { "Julien" })]
    [InlineData("julien.moreau@", new[] { "Julien" })]
    [InlineData("Hedi.BenSalah@example.com", new[] { "Hédi" })]
    [InlineData("nobody", new string[0])]
    public async Task Persons_are_found_by_name_phone_document_or_email(string term, string[] expectedFirstNames)
    {
        var harness = WithPersons();

        var found = await harness.Queries.SearchPersonsAsync(term, cancellationToken: Ct);

        found.Select(p => p.FirstName).ShouldBe(expectedFirstNames, ignoreOrder: true);
    }

    [Fact]
    public async Task A_person_matching_several_criteria_is_listed_once()
    {
        var harness = new CustomersHarness();
        // The name, the phone digits and the CIN all contain "98123456".
        harness.AddTunisian("Hédi", "Ben Salah", phone: "98 123 456", cin: "98123456");

        var found = await harness.Queries.SearchPersonsAsync("98123456", cancellationToken: Ct);

        found.ShouldHaveSingleItem().CurrentDocumentCount.ShouldBe(1);
    }

    [Fact]
    public async Task Anonymized_persons_are_not_searchable_but_still_readable_by_id()
    {
        var harness = WithPersons();
        var hedi = harness.Db.Rows<Person>().First(p => p.FirstName == "Hédi");
        hedi.Anonymize(CustomersHarness.Start).IsSuccess.ShouldBeTrue();

        (await harness.Queries.SearchPersonsAsync("98 123 456", cancellationToken: Ct)).ShouldBeEmpty();
        var info = (await harness.Queries.GetPersonAsync(hedi.Id, Ct)).ShouldNotBeNull();
        info.IsAnonymized.ShouldBeTrue();
        info.FullName.ShouldBeEmpty();
        info.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Blank_terms_find_nothing_and_the_limit_is_applied()
    {
        var harness = WithPersons();

        (await harness.Queries.SearchPersonsAsync("  ", cancellationToken: Ct)).ShouldBeEmpty();
        (await harness.Queries.SearchPersonsAsync("ben salah", limit: 1, Ct)).ShouldHaveSingleItem().FirstName.ShouldBe("Amel");
    }

    [Fact]
    public async Task Document_numbers_are_masked_for_users_who_may_not_read_identity_documents()
    {
        var harness = WithPersons();
        var hedi = harness.Db.Rows<Person>().First(p => p.FirstName == "Hédi");

        var forAgent = await harness.Queries.GetPersonAsync(hedi.Id, Ct);
        harness.As(Roles.Accountant);
        var forAccountant = await harness.Queries.GetPersonAsync(hedi.Id, Ct);

        forAgent!.Documents.ShouldHaveSingleItem().Number.ShouldBe("08123456");
        forAccountant!.Documents.ShouldHaveSingleItem().Number.ShouldBe("••••3456");
        forAccountant.Phone.ShouldBe("+21698123456");
        forAccountant.Documents[0].HasFrontImage.ShouldBeFalse();
    }

    [Fact]
    public async Task Customers_are_read_with_their_display_name()
    {
        var harness = WithPersons();
        var julien = harness.Db.Rows<Person>().First(p => p.FirstName == "Julien");
        var individualId = (await harness.Registration.GetOrCreateIndividualCustomerAsync(julien.Id, Ct)).Value;
        var companyId = (await harness.Registration.CreateCompanyCustomerAsync(
            new CompanyCustomerRequest("Atlas Conseil SA", "7654321/B/P/000", new PostalAddressInfo("45 avenue de la Liberté", null, "1002", "Tunis", "TN")),
            Ct)).Value;

        var individual = await harness.Queries.GetCustomerAsync(individualId, Ct);
        var company = await harness.Queries.GetCustomerAsync(companyId, Ct);

        individual!.Kind.ShouldBe(CustomerKindInfo.Individual);
        individual.DisplayName.ShouldBe("Julien Moreau");
        individual.PersonId.ShouldBe(julien.Id);
        company!.Kind.ShouldBe(CustomerKindInfo.Company);
        company.DisplayName.ShouldBe("Atlas Conseil SA");
        company.TaxIdentifier.ShouldBe("7654321/B/P/000");
        company.BillingAddress!.City.ShouldBe("Tunis");
        (await harness.Queries.FindIndividualCustomerAsync(julien.Id, Ct))!.Id.ShouldBe(individualId);
        (await harness.Queries.GetPersonAsync(julien.Id, Ct))!.IndividualCustomerId.ShouldBe(individualId);
        (await harness.Queries.SearchCompaniesAsync("atlas", cancellationToken: Ct)).ShouldHaveSingleItem().Id.ShouldBe(companyId);
        (await harness.Queries.SearchCompaniesAsync("7654321", cancellationToken: Ct)).ShouldHaveSingleItem();
        (await harness.Queries.SearchCompaniesAsync("conseil liberté", cancellationToken: Ct)).ShouldBeEmpty();
        (await harness.Queries.GetCustomerAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_blacklist_is_answered_for_internal_use()
    {
        var harness = WithPersons();
        var amel = harness.Db.Rows<Person>().First(p => p.FirstName == "Amel");
        amel.Blacklist("Chèque de caution sans provision", new AuditActor(null, "agent", null, null), CustomersHarness.Start).IsSuccess.ShouldBeTrue();

        (await harness.Queries.IsBlacklistedAsync(amel.Id, Ct)).ShouldBeTrue();
        (await harness.Queries.IsBlacklistedAsync(harness.Db.Rows<Person>().First(p => p.FirstName == "Julien").Id, Ct)).ShouldBeFalse();
        (await harness.Queries.IsBlacklistedAsync(Guid.CreateVersion7(), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Eligibility_uses_the_settings_of_the_agency()
    {
        var harness = WithPersons();
        var julien = harness.Db.Rows<Person>().First(p => p.FirstName == "Julien");
        julien.AddDocument(new IdentityDocumentDetails(IdentityDocumentType.DrivingLicense, "100769300457", "FR", new DateOnly(2005, 1, 1)), CustomersHarness.Start)
            .IsSuccess.ShouldBeTrue();
        var pickup = new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero);
        var agencyId = Guid.CreateVersion7();

        var eligible = await harness.Queries.CheckDriverEligibilityAsync(julien.Id, pickup, pickup.AddDays(4), agencyId, Ct);
        harness.Settings.Set(CustomerSettings.DriverMinimumAge, 50);
        var tooYoung = await harness.Queries.CheckDriverEligibilityAsync(julien.Id, pickup, pickup.AddDays(4), agencyId, Ct);

        eligible.Value.IsEligible.ShouldBeTrue();
        eligible.Value.MinimumAge.ShouldBe(21);
        eligible.Value.MinimumLicenseYears.ShouldBe(2);
        tooYoung.Value.IsEligible.ShouldBeFalse();
        var issue = tooYoung.Value.Issues.ShouldHaveSingleItem();
        issue.Kind.ShouldBe(EligibilityIssueKindInfo.UnderMinimumAge);
        issue.Required.ShouldBe(50);
        issue.Actual.ShouldBe(44);
        harness.Settings.RequestedAgencies.ShouldAllBe(id => id == agencyId);
    }

    [Fact]
    public async Task Eligibility_reports_missing_documents_as_contract_issues()
    {
        var harness = WithPersons();
        var amel = harness.Db.Rows<Person>().First(p => p.FirstName == "Amel");
        var pickup = new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero);

        var result = await harness.Queries.CheckDriverEligibilityAsync(amel.Id, pickup, pickup.AddDays(2), cancellationToken: Ct);

        var issue = result.Value.Issues.ShouldHaveSingleItem();
        issue.Kind.ShouldBe(EligibilityIssueKindInfo.DrivingLicenseMissing);
        issue.DocumentType.ShouldBe(IdentityDocumentTypeInfo.DrivingLicense);
    }

    [Fact]
    public async Task Eligibility_errors_for_an_unknown_person_or_an_inverted_period()
    {
        var harness = WithPersons();
        var amel = harness.Db.Rows<Person>().First(p => p.FirstName == "Amel");
        var pickup = new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero);

        (await harness.Queries.CheckDriverEligibilityAsync(Guid.CreateVersion7(), pickup, pickup.AddDays(1), cancellationToken: Ct)).Error
            .ShouldBe(CustomersErrors.PersonNotFound);
        (await harness.Queries.CheckDriverEligibilityAsync(amel.Id, pickup, pickup, cancellationToken: Ct)).Error
            .ShouldBe(CustomersErrors.RentalPeriodInvalid);
    }
}
