using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Domain.Tests.Customers;

public sealed class IdentityDocumentTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The agency of the customers of these tests.</summary>
    private static readonly Guid AgencyId = Guid.CreateVersion7();

    internal static IdentityDocumentDetails Cin(string number = "01234567") =>
        new(IdentityDocumentType.NationalId, number, "TN", IssuedOn: new DateOnly(2015, 5, 4));

    internal static IdentityDocumentDetails Passport(string number = "12AB34567", string country = "FR", DateOnly? expiresOn = null) =>
        new(IdentityDocumentType.Passport, number, country, new DateOnly(2020, 1, 15), expiresOn ?? new DateOnly(2030, 1, 14));

    internal static IdentityDocumentDetails License(DateOnly? issuedOn = null, bool latin = true, DateOnly? expiresOn = null, string country = "TN") =>
        new(IdentityDocumentType.DrivingLicense, "10/123456", country, issuedOn ?? new DateOnly(2012, 9, 1), expiresOn, latin);

    internal static IdentityDocumentDetails InternationalPermit(DateOnly? expiresOn = null) =>
        new(IdentityDocumentType.InternationalDrivingPermit, "IDP-778899", "LY", new DateOnly(2027, 1, 10), expiresOn ?? new DateOnly(2028, 1, 9));

    internal static DocumentImage Image(string name, DateTimeOffset? uploadedAt = null) =>
        DocumentImage.Create($"persons/{name}.jpg", "image/jpeg", 1234, uploadedAt ?? Now).Value;

    private static Person NewPerson() => Person.Register(AgencyId, new PersonDetails("Hédi", "Ben Salah", "TN"), Now).Value;

    [Fact]
    public void A_document_is_added_with_a_cleaned_number_and_a_search_key()
    {
        var person = NewPerson();

        var passport = person.AddDocument(Passport(" 12 ab-34567 "), Now).Value;

        passport.PersonId.ShouldBe(person.Id);
        passport.Type.ShouldBe(IdentityDocumentType.Passport);
        passport.Number.ShouldBe("12 AB-34567");
        passport.NumberKey.ShouldBe("12AB34567");
        passport.IssuingCountry.ShouldBe("FR");
        passport.IsCurrent.ShouldBeTrue();
        passport.IsVerified.ShouldBeFalse();
        passport.AddedAtUtc.ShouldBe(Now);
        person.CurrentDocument(IdentityDocumentType.Passport).ShouldBe(passport);
        person.DomainEvents.ShouldContain(new IdentityDocumentAdded(person.Id, passport.Id, IdentityDocumentType.Passport));
    }

    [Theory]
    [InlineData("0123456")]
    [InlineData("012345678")]
    [InlineData("0123456A")]
    public void A_tunisian_national_id_has_exactly_eight_digits(string number)
    {
        NewPerson().AddDocument(Cin(number), Now).Error.ShouldBe(CustomersErrors.NationalIdNumberInvalid);
    }

    [Fact]
    public void A_foreign_national_id_is_validated_loosely()
    {
        NewPerson().AddDocument(new IdentityDocumentDetails(IdentityDocumentType.NationalId, "DZ 1234 5678 9", "DZ"), Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("123456789012345678901")]
    [InlineData("12#34567")]
    [InlineData("ÀÉ123456")]
    public void Malformed_numbers_are_rejected(string number)
    {
        NewPerson().AddDocument(Passport(number), Now).Error.ShouldBe(CustomersErrors.DocumentNumberInvalid);
    }

    [Fact]
    public void Type_country_and_dates_are_validated()
    {
        var person = NewPerson();

        person.AddDocument(Passport() with { Type = (IdentityDocumentType)9 }, Now).Error.ShouldBe(CustomersErrors.DocumentTypeInvalid);
        person.AddDocument(Passport(country: "XX"), Now).Error.ShouldBe(CustomersErrors.CountryInvalid);
        person.AddDocument(Passport() with { ExpiresOn = null }, Now).Error.ShouldBe(CustomersErrors.DocumentExpiryDateRequired);
        person.AddDocument(InternationalPermit() with { ExpiresOn = null }, Now).Error.ShouldBe(CustomersErrors.DocumentExpiryDateRequired);
        person.AddDocument(License() with { IssuedOn = null }, Now).Error.ShouldBe(CustomersErrors.DocumentIssueDateRequired);
        person.AddDocument(Passport() with { IssuedOn = new DateOnly(2031, 1, 1) }, Now).Error.ShouldBe(CustomersErrors.DocumentDatesInvalid);
        person.AddDocument(Passport() with { IssuedOn = new DateOnly(2027, 6, 2) }, Now).Error.ShouldBe(CustomersErrors.DocumentDatesInvalid);
        person.AddDocument(Passport() with { IssuedOn = new DateOnly(1899, 12, 31) }, Now).Error.ShouldBe(CustomersErrors.DocumentDatesInvalid);
        person.Documents.ShouldBeEmpty();
    }

    [Fact]
    public void National_id_and_driving_license_may_have_no_expiry_date()
    {
        var person = NewPerson();

        var cin = person.AddDocument(Cin(), Now).Value;
        var license = person.AddDocument(License(), Now).Value;

        cin.ExpiresOn.ShouldBeNull();
        license.ExpiresOn.ShouldBeNull();
        cin.IsValidUntil(new DateOnly(2090, 1, 1)).ShouldBeTrue();
    }

    [Fact]
    public void A_document_is_valid_until_its_expiry_date_included()
    {
        var passport = NewPerson().AddDocument(Passport(expiresOn: new DateOnly(2027, 7, 3)), Now).Value;

        passport.IsValidUntil(new DateOnly(2027, 7, 2)).ShouldBeTrue();
        passport.IsValidUntil(new DateOnly(2027, 7, 3)).ShouldBeTrue();
        passport.IsValidUntil(new DateOnly(2027, 7, 4)).ShouldBeFalse();
    }

    [Fact]
    public void A_new_number_supersedes_the_current_document_of_the_same_type()
    {
        var person = NewPerson();
        var old = person.AddDocument(Passport("11AA11111"), Now).Value;
        person.AttachDocumentImage(old.Id, DocumentImageSide.Front, Image("old-front"), Now).IsSuccess.ShouldBeTrue();

        var renewed = person.AddDocument(Passport("22BB22222", expiresOn: new DateOnly(2037, 1, 1)), Now.AddDays(1)).Value;

        renewed.ShouldNotBe(old);
        old.IsCurrent.ShouldBeFalse();
        old.SupersededAtUtc.ShouldBe(Now.AddDays(1));
        old.FrontImage.ShouldNotBeNull("the images of a superseded document are kept until the retention purge");
        person.CurrentDocument(IdentityDocumentType.Passport).ShouldBe(renewed);
        person.CurrentDocuments.ShouldHaveSingleItem();
        person.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public void Adding_the_same_number_again_corrects_the_current_document_and_resets_its_verification()
    {
        var person = NewPerson();
        var passport = person.AddDocument(Passport("12AB34567"), Now).Value;
        person.VerifyDocument(passport.Id, new AuditActor(null, "agent", null, null), Now).IsSuccess.ShouldBeTrue();

        var corrected = person.AddDocument(Passport("12 AB 34567", expiresOn: new DateOnly(2031, 5, 5)), Now.AddDays(1)).Value;

        corrected.ShouldBe(passport);
        corrected.ExpiresOn.ShouldBe(new DateOnly(2031, 5, 5));
        corrected.IsVerified.ShouldBeFalse();
        person.Documents.ShouldHaveSingleItem();
    }

    [Fact]
    public void Documents_of_different_types_coexist()
    {
        var person = NewPerson();

        person.AddDocument(Cin(), Now).IsSuccess.ShouldBeTrue();
        person.AddDocument(Passport(), Now).IsSuccess.ShouldBeTrue();
        person.AddDocument(License(), Now).IsSuccess.ShouldBeTrue();
        person.AddDocument(InternationalPermit(), Now).IsSuccess.ShouldBeTrue();

        person.CurrentDocuments.Select(d => d.Type).ShouldBe(Enum.GetValues<IdentityDocumentType>(), ignoreOrder: true);
    }

    [Fact]
    public void Verification_records_who_and_when()
    {
        var person = NewPerson();
        var cin = person.AddDocument(Cin(), Now).Value;
        var agent = new AuditActor(Guid.CreateVersion7(), "agent@demo.local", null, null);

        person.VerifyDocument(cin.Id, agent, Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        cin.IsVerified.ShouldBeTrue();
        cin.VerifiedAtUtc.ShouldBe(Now.AddHours(2));
        cin.VerifiedByUserId.ShouldBe(agent.UserId);
        cin.VerifiedByName.ShouldBe("agent@demo.local");
        person.DomainEvents.ShouldContain(new IdentityDocumentVerified(person.Id, cin.Id));
        person.VerifyDocument(Guid.CreateVersion7(), agent, Now).Error.ShouldBe(CustomersErrors.DocumentNotFound);
    }

    [Fact]
    public void A_superseded_document_can_neither_be_verified_nor_receive_images()
    {
        var person = NewPerson();
        var old = person.AddDocument(Passport("11AA11111"), Now).Value;
        person.AddDocument(Passport("22BB22222"), Now).IsSuccess.ShouldBeTrue();

        person.VerifyDocument(old.Id, new AuditActor(null, "agent", null, null), Now).Error.ShouldBe(CustomersErrors.DocumentNotCurrent);
        person.AttachDocumentImage(old.Id, DocumentImageSide.Front, Image("x"), Now).Error.ShouldBe(CustomersErrors.DocumentNotCurrent);
    }

    [Fact]
    public void Replacing_an_image_discards_the_previous_file_and_resets_the_verification()
    {
        var person = NewPerson();
        var cin = person.AddDocument(Cin(), Now).Value;
        person.AttachDocumentImage(cin.Id, DocumentImageSide.Front, Image("first"), Now).IsSuccess.ShouldBeTrue();
        person.VerifyDocument(cin.Id, new AuditActor(null, "agent", null, null), Now).IsSuccess.ShouldBeTrue();
        person.DequeueDomainEvents();

        person.AttachDocumentImage(cin.Id, DocumentImageSide.Front, Image("second"), Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();

        cin.FrontImage!.Path.ShouldBe("persons/second.jpg");
        cin.BackImage.ShouldBeNull();
        cin.IsVerified.ShouldBeFalse();
        person.DomainEvents.OfType<DocumentImagesDiscarded>().ShouldHaveSingleItem()
            .ShouldBe(new DocumentImagesDiscarded(person.Id, cin.Id, ["persons/first.jpg"]), new DocumentImagesDiscardedComparer());
        person.AttachDocumentImage(cin.Id, (DocumentImageSide)7, Image("third"), Now).Error.ShouldBe(CustomersErrors.ImageSideInvalid);
    }

    [Fact]
    public void Correcting_a_document_validates_and_resets_the_verification()
    {
        var person = NewPerson();
        var license = person.AddDocument(License(), Now).Value;
        person.VerifyDocument(license.Id, new AuditActor(null, "agent", null, null), Now).IsSuccess.ShouldBeTrue();

        person.CorrectDocument(license.Id, License(new DateOnly(2013, 9, 1), latin: false), Now).IsSuccess.ShouldBeTrue();
        person.CorrectDocument(license.Id, License() with { IssuedOn = null }, Now).Error.ShouldBe(CustomersErrors.DocumentIssueDateRequired);
        person.CorrectDocument(license.Id, Passport(), Now).Error.ShouldBe(CustomersErrors.DocumentTypeInvalid);

        license.IssuedOn.ShouldBe(new DateOnly(2013, 9, 1));
        license.IsLatinScript.ShouldBeFalse();
        license.IsVerified.ShouldBeFalse();
    }

    [Fact]
    public void Removing_a_document_discards_its_images()
    {
        var person = NewPerson();
        var cin = person.AddDocument(Cin(), Now).Value;
        person.AttachDocumentImage(cin.Id, DocumentImageSide.Back, Image("back"), Now).IsSuccess.ShouldBeTrue();
        person.DequeueDomainEvents();

        person.RemoveDocument(cin.Id, Now).IsSuccess.ShouldBeTrue();

        person.Documents.ShouldBeEmpty();
        person.DomainEvents.OfType<DocumentImagesDiscarded>().ShouldHaveSingleItem().Paths.ShouldBe(["persons/back.jpg"]);
        person.RemoveDocument(cin.Id, Now).Error.ShouldBe(CustomersErrors.DocumentNotFound);
    }

    [Fact]
    public void Images_older_than_the_retention_cutoff_are_purged_unless_a_rental_closed_recently()
    {
        var person = NewPerson();
        var cin = person.AddDocument(Cin(), Now).Value;
        var license = person.AddDocument(License(), Now).Value;
        person.AttachDocumentImage(cin.Id, DocumentImageSide.Front, Image("cin-front", Now.AddDays(-500)), Now).IsSuccess.ShouldBeTrue();
        person.AttachDocumentImage(cin.Id, DocumentImageSide.Back, Image("cin-back", Now.AddDays(-10)), Now).IsSuccess.ShouldBeTrue();
        person.AttachDocumentImage(license.Id, DocumentImageSide.Front, Image("license-front", Now.AddDays(-450)), Now).IsSuccess.ShouldBeTrue();
        var cutoff = Now.AddDays(-400);

        person.RecordRentalClosed(Now.AddDays(-30));
        person.PurgeDocumentImages(cutoff, Now).ShouldBe(0);
        cin.FrontImage.ShouldNotBeNull();

        var stale = NewPerson();
        var staleCin = stale.AddDocument(Cin(), Now).Value;
        stale.AttachDocumentImage(staleCin.Id, DocumentImageSide.Front, Image("stale-front", Now.AddDays(-500)), Now).IsSuccess.ShouldBeTrue();
        stale.AttachDocumentImage(staleCin.Id, DocumentImageSide.Back, Image("stale-back", Now.AddDays(-10)), Now).IsSuccess.ShouldBeTrue();
        stale.RecordRentalClosed(Now.AddDays(-401));
        stale.DequeueDomainEvents();

        stale.PurgeDocumentImages(cutoff, Now).ShouldBe(1);

        staleCin.FrontImage.ShouldBeNull();
        staleCin.BackImage.ShouldNotBeNull();
        stale.DomainEvents.OfType<DocumentImagesDiscarded>().ShouldHaveSingleItem().Paths.ShouldBe(["persons/stale-front.jpg"]);
    }

    [Fact]
    public void Document_images_accept_jpeg_png_and_pdf_up_to_ten_megabytes()
    {
        DocumentImage.Create("persons/a.pdf", "application/pdf", DocumentImage.MaxLength, Now).IsSuccess.ShouldBeTrue();
        DocumentImage.Create("persons/a.png", "image/png", 1, Now).IsSuccess.ShouldBeTrue();
        DocumentImage.Create("persons/a.gif", "image/gif", 10, Now).Error.ShouldBe(CustomersErrors.ImageUnsupportedType);
        DocumentImage.Create("persons/a.jpg", "image/jpeg", DocumentImage.MaxLength + 1, Now).Error.ShouldBe(CustomersErrors.ImageTooLarge);
        DocumentImage.Create("persons/a.jpg", "image/jpeg", 0, Now).Error.ShouldBe(CustomersErrors.ImageEmpty);
        Should.Throw<ArgumentException>(() => DocumentImage.Create(" ", "image/jpeg", 10, Now));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }, DocumentFileFormat.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, DocumentFileFormat.Png)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }, DocumentFileFormat.Pdf)]
    public void File_formats_are_detected_from_their_magic_bytes(byte[] header, DocumentFileFormat expected)
    {
        DocumentFileFormats.Detect(header).ShouldBe(expected);
        DocumentFileFormats.ContentTypeOf(expected).ShouldNotBeNullOrEmpty();
        DocumentFileFormats.ExtensionOf(expected).ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[0])]
    public void Other_formats_or_truncated_headers_are_not_recognized(byte[] header)
    {
        DocumentFileFormats.Detect(header).ShouldBeNull();
    }

    private sealed class DocumentImagesDiscardedComparer : IEqualityComparer<DocumentImagesDiscarded>
    {
        public bool Equals(DocumentImagesDiscarded? x, DocumentImagesDiscarded? y) =>
            x is not null && y is not null && x.PersonId == y.PersonId && x.DocumentId == y.DocumentId && x.Paths.SequenceEqual(y.Paths);

        public int GetHashCode(DocumentImagesDiscarded obj) => obj.DocumentId.GetHashCode();
    }
}
