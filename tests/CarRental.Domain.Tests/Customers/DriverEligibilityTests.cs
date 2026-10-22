using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Customers;

/// <summary>Driver eligibility (SPEC §5): age and licence seniority from the settings, identity document by nationality,
/// documents valid until the return date, international permit for a non-latin licence.</summary>
public sealed class DriverEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The agency of the customers of these tests.</summary>
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly EligibilityRules Rules = new(MinimumAge: 21, MinimumLicenseYears: 2);

    // Pick-up 28/06/2027 10:00, return 03/07/2027 10:00, Africa/Tunis.
    private static readonly Period Rental = Period.Create(
        TunisTime.FromLocal(new DateTime(2027, 6, 28, 10, 0, 0)),
        TunisTime.FromLocal(new DateTime(2027, 7, 3, 10, 0, 0))).Value;

    private static Person Driver(string? nationality = "TN", DateOnly? birthDate = null, bool withBirthDate = true)
    {
        var details = new PersonDetails("Sami", "Trabelsi", nationality, withBirthDate ? birthDate ?? new DateOnly(1990, 3, 14) : null);
        return Person.Register(AgencyId, details, Now).Value;
    }

    private static Person EligibleTunisian()
    {
        var driver = Driver();
        driver.AddDocument(IdentityDocumentTests.Cin(), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(), Now).IsSuccess.ShouldBeTrue();
        return driver;
    }

    private static Person EligibleForeigner(string nationality = "FR")
    {
        var driver = Driver(nationality);
        driver.AddDocument(IdentityDocumentTests.Passport(country: nationality), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(country: nationality), Now).IsSuccess.ShouldBeTrue();
        return driver;
    }

    private static IReadOnlyList<EligibilityIssue> Issues(Person driver, Period? rental = null, EligibilityRules? rules = null) =>
        DriverEligibility.Evaluate(driver, rental ?? Rental, rules ?? Rules).Issues;

    [Fact]
    public void A_tunisian_with_a_national_id_and_a_latin_licence_is_eligible()
    {
        var report = DriverEligibility.Evaluate(EligibleTunisian(), Rental, Rules);

        report.IsEligible.ShouldBeTrue();
        report.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void A_foreigner_with_a_passport_and_a_latin_licence_is_eligible()
    {
        DriverEligibility.Evaluate(EligibleForeigner("DE"), Rental, Rules).IsEligible.ShouldBeTrue();
    }

    [Theory]
    [InlineData(2006, 6, 28, true)]
    [InlineData(2006, 6, 29, false)]
    [InlineData(2006, 6, 27, true)]
    [InlineData(2010, 1, 1, false)]
    public void The_driver_must_have_reached_the_minimum_age_on_the_tunis_pick_up_date(int year, int month, int day, bool eligible)
    {
        var driver = Driver(birthDate: new DateOnly(year, month, day));
        driver.AddDocument(IdentityDocumentTests.Cin(), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(new DateOnly(2020, 1, 1)), Now).IsSuccess.ShouldBeTrue();

        var issues = Issues(driver);

        if (eligible)
        {
            issues.ShouldBeEmpty();
        }
        else
        {
            var issue = issues.ShouldHaveSingleItem();
            issue.Kind.ShouldBe(EligibilityIssueKind.UnderMinimumAge);
            issue.Required.ShouldBe(21);
            issue.Actual.ShouldBe(DriverEligibility.CompletedYears(new DateOnly(year, month, day), new DateOnly(2027, 6, 28)));
        }
    }

    [Fact]
    public void Age_is_computed_on_the_local_date_even_just_after_midnight_in_tunis()
    {
        // 23:30 UTC on 27/06 is already 28/06 00:30 in Tunis: the 21st birthday is reached.
        var lateNight = Period.Create(new DateTimeOffset(2027, 6, 27, 23, 30, 0, TimeSpan.Zero), new DateTimeOffset(2027, 6, 30, 9, 0, 0, TimeSpan.Zero)).Value;
        var driver = Driver(birthDate: new DateOnly(2006, 6, 28));
        driver.AddDocument(IdentityDocumentTests.Cin(), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(new DateOnly(2024, 1, 1)), Now).IsSuccess.ShouldBeTrue();

        Issues(driver, lateNight).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2004, 2, 29, 2025, 2, 28, 21)]
    [InlineData(2004, 2, 29, 2025, 2, 27, 20)]
    [InlineData(2004, 2, 29, 2028, 2, 29, 24)]
    [InlineData(2000, 1, 1, 2000, 1, 1, 0)]
    [InlineData(1990, 12, 31, 2027, 12, 30, 36)]
    public void Completed_years_follow_the_calendar(int fromYear, int fromMonth, int fromDay, int onYear, int onMonth, int onDay, int expected)
    {
        DriverEligibility.CompletedYears(new DateOnly(fromYear, fromMonth, fromDay), new DateOnly(onYear, onMonth, onDay)).ShouldBe(expected);
    }

    [Property(MaxTest = 1000)]
    public Property Completed_years_grow_by_one_on_each_anniversary()
    {
        var dates = Gen.Choose(0, 36_000).Select(days => new DateOnly(1930, 1, 1).AddDays(days)).ToArbitrary();
        var ages = Gen.Choose(0, 90).ToArbitrary();
        return Prop.ForAll(dates, ages, (birth, age) =>
            DriverEligibility.CompletedYears(birth, birth.AddYears(age)) == age
            && DriverEligibility.CompletedYears(birth, birth.AddYears(age + 1).AddDays(-1)) == age);
    }

    [Theory]
    [InlineData(2025, 6, 28, true)]
    [InlineData(2025, 6, 29, false)]
    [InlineData(2026, 12, 1, false)]
    public void The_licence_must_have_been_held_for_the_minimum_years_on_the_pick_up_date(int year, int month, int day, bool eligible)
    {
        var driver = Driver();
        driver.AddDocument(IdentityDocumentTests.Cin(), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(new DateOnly(year, month, day)), Now).IsSuccess.ShouldBeTrue();

        var issues = Issues(driver);

        if (eligible)
        {
            issues.ShouldBeEmpty();
        }
        else
        {
            var issue = issues.ShouldHaveSingleItem();
            issue.Kind.ShouldBe(EligibilityIssueKind.LicenseHeldTooShort);
            issue.Required.ShouldBe(2);
        }
    }

    [Fact]
    public void Minimum_age_and_seniority_come_from_the_rules()
    {
        var driver = EligibleTunisian();

        var issues = Issues(driver, rules: new EligibilityRules(MinimumAge: 40, MinimumLicenseYears: 20));

        issues.Select(i => i.Kind).ShouldBe([EligibilityIssueKind.UnderMinimumAge, EligibilityIssueKind.LicenseHeldTooShort]);
        issues[0].Required.ShouldBe(40);
        issues[0].Actual.ShouldBe(37);
        issues[1].Required.ShouldBe(20);
        issues[1].Actual.ShouldBe(14);
    }

    [Fact]
    public void A_tunisian_needs_a_national_id_even_with_a_passport()
    {
        var driver = Driver();
        driver.AddDocument(IdentityDocumentTests.Passport(country: "TN"), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(), Now).IsSuccess.ShouldBeTrue();

        var issue = Issues(driver).ShouldHaveSingleItem();

        issue.Kind.ShouldBe(EligibilityIssueKind.IdentityDocumentMissing);
        issue.DocumentType.ShouldBe(IdentityDocumentType.NationalId);
    }

    [Fact]
    public void A_foreigner_needs_a_passport_even_with_a_national_id()
    {
        var driver = Driver("DZ");
        driver.AddDocument(new IdentityDocumentDetails(IdentityDocumentType.NationalId, "DZ123456789", "DZ"), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(country: "DZ"), Now).IsSuccess.ShouldBeTrue();

        var issue = Issues(driver).ShouldHaveSingleItem();

        issue.Kind.ShouldBe(EligibilityIssueKind.IdentityDocumentMissing);
        issue.DocumentType.ShouldBe(IdentityDocumentType.Passport);
    }

    [Fact]
    public void Unknown_birth_date_and_nationality_are_reported()
    {
        var driver = Driver(nationality: null, withBirthDate: false);
        driver.AddDocument(IdentityDocumentTests.License(), Now).IsSuccess.ShouldBeTrue();

        Issues(driver).Select(i => i.Kind).ShouldBe([EligibilityIssueKind.BirthDateMissing, EligibilityIssueKind.NationalityMissing]);
    }

    [Fact]
    public void A_missing_licence_is_reported_once()
    {
        var driver = Driver();
        driver.AddDocument(IdentityDocumentTests.Cin(), Now).IsSuccess.ShouldBeTrue();

        Issues(driver).ShouldHaveSingleItem().Kind.ShouldBe(EligibilityIssueKind.DrivingLicenseMissing);
    }

    [Theory]
    [InlineData(2027, 7, 3, true)]
    [InlineData(2027, 7, 2, false)]
    [InlineData(2027, 6, 1, false)]
    public void The_identity_document_must_be_valid_until_the_return_date(int year, int month, int day, bool eligible)
    {
        var driver = Driver("IT");
        driver.AddDocument(IdentityDocumentTests.Passport(country: "IT", expiresOn: new DateOnly(year, month, day)), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(country: "IT"), Now).IsSuccess.ShouldBeTrue();

        var issues = Issues(driver);

        if (eligible)
        {
            issues.ShouldBeEmpty();
        }
        else
        {
            var issue = issues.ShouldHaveSingleItem();
            issue.Kind.ShouldBe(EligibilityIssueKind.DocumentExpiresBeforeReturn);
            issue.DocumentType.ShouldBe(IdentityDocumentType.Passport);
            issue.Date.ShouldBe(new DateOnly(year, month, day));
        }
    }

    [Fact]
    public void An_expired_licence_is_reported()
    {
        var driver = Driver();
        driver.AddDocument(IdentityDocumentTests.Cin(), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(expiresOn: new DateOnly(2026, 12, 31)), Now).IsSuccess.ShouldBeTrue();

        var issue = Issues(driver).ShouldHaveSingleItem();

        issue.Kind.ShouldBe(EligibilityIssueKind.DocumentExpiresBeforeReturn);
        issue.DocumentType.ShouldBe(IdentityDocumentType.DrivingLicense);
        issue.Date.ShouldBe(new DateOnly(2026, 12, 31));
    }

    [Fact]
    public void A_non_latin_licence_requires_an_international_permit_valid_until_the_return()
    {
        var driver = Driver("LY");
        driver.AddDocument(IdentityDocumentTests.Passport("LY1234567", "LY"), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(latin: false, country: "LY"), Now).IsSuccess.ShouldBeTrue();

        Issues(driver).ShouldHaveSingleItem().Kind.ShouldBe(EligibilityIssueKind.InternationalPermitMissing);

        driver.AddDocument(IdentityDocumentTests.InternationalPermit(new DateOnly(2027, 7, 1)), Now).IsSuccess.ShouldBeTrue();
        var expiring = Issues(driver).ShouldHaveSingleItem();
        expiring.Kind.ShouldBe(EligibilityIssueKind.DocumentExpiresBeforeReturn);
        expiring.DocumentType.ShouldBe(IdentityDocumentType.InternationalDrivingPermit);

        driver.AddDocument(IdentityDocumentTests.InternationalPermit(new DateOnly(2028, 1, 9)) with { Number = "IDP-990011" }, Now).IsSuccess.ShouldBeTrue();
        Issues(driver).ShouldBeEmpty();
    }

    [Fact]
    public void A_latin_licence_needs_no_international_permit()
    {
        var driver = EligibleForeigner("DE");

        Issues(driver).ShouldNotContain(i => i.Kind == EligibilityIssueKind.InternationalPermitMissing);
    }

    [Fact]
    public void Superseded_documents_are_ignored()
    {
        var driver = Driver("FR");
        driver.AddDocument(IdentityDocumentTests.Passport("11AA11111", "FR", new DateOnly(2030, 1, 1)), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.Passport("22BB22222", "FR", new DateOnly(2027, 6, 30)), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(country: "FR"), Now).IsSuccess.ShouldBeTrue();

        var issue = Issues(driver).ShouldHaveSingleItem();

        issue.Kind.ShouldBe(EligibilityIssueKind.DocumentExpiresBeforeReturn);
        issue.Date.ShouldBe(new DateOnly(2027, 6, 30));
    }

    [Fact]
    public void Every_issue_is_listed_in_a_stable_order()
    {
        var driver = Driver(birthDate: new DateOnly(2010, 5, 5));
        driver.AddDocument(IdentityDocumentTests.Passport(country: "TN", expiresOn: new DateOnly(2027, 1, 1)), Now).IsSuccess.ShouldBeTrue();
        driver.AddDocument(IdentityDocumentTests.License(new DateOnly(2027, 1, 1), latin: false, expiresOn: new DateOnly(2027, 6, 30)), Now).IsSuccess.ShouldBeTrue();

        Issues(driver).Select(i => i.Kind).ShouldBe(
        [
            EligibilityIssueKind.UnderMinimumAge,
            EligibilityIssueKind.IdentityDocumentMissing,
            EligibilityIssueKind.LicenseHeldTooShort,
            EligibilityIssueKind.DocumentExpiresBeforeReturn,
            EligibilityIssueKind.InternationalPermitMissing,
        ]);
    }

    [Fact]
    public void Rules_cannot_be_negative()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new EligibilityRules(-1, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => new EligibilityRules(21, -2));
    }
}
