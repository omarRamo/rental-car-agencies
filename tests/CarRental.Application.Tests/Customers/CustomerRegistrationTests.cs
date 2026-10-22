using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Customers;

namespace CarRental.Application.Tests.Customers;

/// <summary>Find-or-create without duplicates (ADR-0027), payers and documents through the registration contract.</summary>
public sealed class CustomerRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IdentityDocumentInput Cin(string number) =>
        new(IdentityDocumentTypeInfo.NationalId, number, "TN", new DateOnly(2016, 3, 1));

    [Fact]
    public async Task A_new_person_is_created_when_nothing_matches()
    {
        var harness = new CustomersHarness();

        var result = await harness.Registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest("Sami", "Trabelsi", "TN", new DateOnly(1988, 4, 12), "20 111 001", "Sami@Example.com", Document: Cin("08123456")),
            Ct);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        result.Value.Created.ShouldBeTrue();
        result.Value.SimilarPersonIds.ShouldBeEmpty();
        var person = harness.Db.Rows<Person>().ShouldHaveSingleItem();
        person.Id.ShouldBe(result.Value.PersonId);
        person.Phone.ShouldBe("+21620111001");
        person.Email.ShouldBe("sami@example.com");
        person.CurrentDocument(IdentityDocumentType.NationalId)!.NumberKey.ShouldBe("08123456");
        harness.UnitOfWork.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_phone_and_name_find_the_person_again_and_complete_its_details()
    {
        var harness = new CustomersHarness();
        var existing = harness.AddTunisian(phone: "20 111 001");

        var result = await harness.Registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest("SAMI", "trabelsi", Phone: "+216 20 111 001", Email: "sami@example.com"),
            Ct);

        result.Value.PersonId.ShouldBe(existing.Id);
        result.Value.Match.ShouldBe(PersonMatchKindInfo.Phone);
        harness.Db.Rows<Person>().ShouldHaveSingleItem();
        existing.Email.ShouldBe("sami@example.com");
        existing.FirstName.ShouldBe("Sami", "existing values are never overwritten");
    }

    [Fact]
    public async Task The_same_email_and_name_find_the_person_again()
    {
        var harness = new CustomersHarness();
        var existing = harness.AddTunisian(email: "sami@example.com");

        var result = await harness.Registration.FindOrCreatePersonAsync(new PersonRegistrationRequest("Sami", "Trabelsi", Email: " SAMI@example.com "), Ct);

        result.Value.PersonId.ShouldBe(existing.Id);
        result.Value.Match.ShouldBe(PersonMatchKindInfo.Email);
    }

    [Fact]
    public async Task A_relative_sharing_the_phone_is_a_new_person_reported_as_similar()
    {
        var harness = new CustomersHarness();
        var mother = harness.AddTunisian("Amel", "Trabelsi", phone: "20 111 001");

        var result = await harness.Registration.FindOrCreatePersonAsync(new PersonRegistrationRequest("Yasmine", "Trabelsi", Phone: "20111001"), Ct);

        result.Value.Created.ShouldBeTrue();
        result.Value.PersonId.ShouldNotBe(mother.Id);
        result.Value.SimilarPersonIds.ShouldBe([mother.Id]);
        harness.Db.Rows<Person>().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Father_and_son_with_the_same_name_and_phone_are_told_apart_by_their_national_id()
    {
        var harness = new CustomersHarness();
        var father = harness.AddTunisian("Mohamed", "Ben Ali", phone: "98 333 003", cin: "05345678");

        var result = await harness.Registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest("Mohamed", "Ben Ali", Phone: "98 333 003", Document: Cin("13456789")),
            Ct);

        result.Value.Created.ShouldBeTrue();
        result.Value.SimilarPersonIds.ShouldBe([father.Id]);
        father.CurrentDocument(IdentityDocumentType.NationalId)!.NumberKey.ShouldBe("05345678");
    }

    [Fact]
    public async Task The_same_document_finds_the_person_even_with_another_name()
    {
        var harness = new CustomersHarness();
        var existing = harness.AddTunisian("Mohamed", "Ben Ali", cin: "05345678");

        var result = await harness.Registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest("Mohammed", "Benali", Document: Cin("05 345 678")),
            Ct);

        result.Value.PersonId.ShouldBe(existing.Id);
        result.Value.Match.ShouldBe(PersonMatchKindInfo.Document);
        result.Value.NameDiffers.ShouldBeTrue();
        existing.FirstName.ShouldBe("Mohamed");
    }

    [Fact]
    public async Task A_person_found_by_phone_receives_the_document_it_did_not_have()
    {
        var harness = new CustomersHarness();
        var existing = Person.Register(TestUsers.AgencyId, new PersonDetails("Julien", "Moreau", "FR", Phone: "+33 6 39 98 00 07"), CustomersHarness.Start).Value;
        harness.Add(existing);

        var result = await harness.Registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(
                "Julien",
                "Moreau",
                Phone: "0033 6 39 98 00 07",
                Document: new IdentityDocumentInput(IdentityDocumentTypeInfo.Passport, "21AB12345", "FR", new DateOnly(2020, 1, 1), new DateOnly(2030, 1, 1))),
            Ct);

        result.Value.PersonId.ShouldBe(existing.Id);
        existing.CurrentDocument(IdentityDocumentType.Passport).ShouldNotBeNull();
    }

    [Fact]
    public async Task Anonymized_persons_are_never_found_again()
    {
        var harness = new CustomersHarness();
        var anonymized = harness.AddTunisian(phone: "20 111 001");
        anonymized.Anonymize(CustomersHarness.Start).IsSuccess.ShouldBeTrue();

        var result = await harness.Registration.FindOrCreatePersonAsync(new PersonRegistrationRequest("Sami", "Trabelsi", Phone: "20 111 001"), Ct);

        result.Value.Created.ShouldBeTrue();
        result.Value.SimilarPersonIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Registration_requires_the_customers_manage_permission()
    {
        var harness = new CustomersHarness(Roles.Accountant);

        var result = await harness.Registration.FindOrCreatePersonAsync(new PersonRegistrationRequest("Sami", "Trabelsi"), Ct);

        result.Error.ShouldBe(CustomersErrors.Forbidden);
        harness.Db.Rows<Person>().ShouldBeEmpty();
        harness.UnitOfWork.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_any_database_access()
    {
        var harness = new CustomersHarness();

        (await harness.Registration.FindOrCreatePersonAsync(new PersonRegistrationRequest("Sami", "Trabelsi", Phone: "123"), Ct)).Error
            .ShouldBe(CustomersErrors.PhoneInvalid);
        (await harness.Registration.FindOrCreatePersonAsync(new PersonRegistrationRequest("Sami", "Trabelsi", Document: Cin("1234567")), Ct)).Error
            .ShouldBe(CustomersErrors.NationalIdNumberInvalid);
        (await harness.Registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest("Sami", "Trabelsi", Address: new PostalAddressInfo("", null, null, "Tunis", "TN")),
            Ct)).Error.ShouldBe(CustomersErrors.AddressInvalid);

        harness.UnitOfWork.Executions.ShouldBe(0);
        harness.Db.Rows<Person>().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_individual_payer_of_a_person_is_created_once()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();

        var first = await harness.Registration.GetOrCreateIndividualCustomerAsync(person.Id, Ct);
        var second = await harness.Registration.GetOrCreateIndividualCustomerAsync(person.Id, Ct);

        first.Value.ShouldBe(second.Value);
        var customer = harness.Db.Rows<Customer>().ShouldHaveSingleItem();
        customer.PersonId.ShouldBe(person.Id);
        (await harness.Registration.GetOrCreateIndividualCustomerAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(CustomersErrors.PersonNotFound);
    }

    [Fact]
    public async Task An_anonymized_person_cannot_become_a_payer()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        person.Anonymize(CustomersHarness.Start).IsSuccess.ShouldBeTrue();

        (await harness.Registration.GetOrCreateIndividualCustomerAsync(person.Id, Ct)).Error.ShouldBe(CustomersErrors.PersonAnonymized);
    }

    [Fact]
    public async Task A_company_tax_identifier_is_unique()
    {
        var harness = new CustomersHarness();
        var request = new CompanyCustomerRequest(
            "Société Méditerranéenne de Tourisme SARL",
            "1234567/A/M/000",
            new PostalAddressInfo("Rue du Lac Biwa", null, "1053", "Tunis", "TN"));

        var created = await harness.Registration.CreateCompanyCustomerAsync(request, Ct);
        var duplicate = await harness.Registration.CreateCompanyCustomerAsync(request with { TaxIdentifier = "1234567 A M 000", LegalName = "SMT" }, Ct);

        created.IsSuccess.ShouldBeTrue();
        duplicate.Error.ShouldBe(CustomersErrors.CompanyAlreadyExists);
        harness.Db.Rows<Customer>().ShouldHaveSingleItem().Kind.ShouldBe(CustomerKind.Company);
        (await harness.Registration.CreateCompanyCustomerAsync(request with { BillingAddress = null! }, Ct)).Error.ShouldBe(CustomersErrors.BillingAddressRequired);
    }

    [Fact]
    public async Task A_document_is_added_with_its_images_checked_on_their_content()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        using var front = SampleFiles.Stream(SampleFiles.Jpeg());
        using var back = SampleFiles.Stream(SampleFiles.Pdf());

        var result = await harness.Registration.AddOrReplaceDocumentAsync(
            person.Id,
            new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, "07/123456", "TN", new DateOnly(2008, 6, 15)),
            front,
            back,
            Ct);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        var license = person.FindDocument(result.Value).ShouldNotBeNull();
        license.FrontImage!.ContentType.ShouldBe("image/jpeg");
        license.BackImage!.ContentType.ShouldBe("application/pdf");
        license.FrontImage.Path.ShouldStartWith($"persons/{person.Id:N}/");
        license.FrontImage.Path.ShouldNotContain("Trabelsi");
        harness.Storage.Holds(license.FrontImage.Path).ShouldBeTrue();
        harness.Storage.Holds(license.BackImage.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task A_document_number_of_another_person_is_refused_and_the_uploaded_files_are_deleted()
    {
        var harness = new CustomersHarness();
        harness.AddTunisian("Sami", "Trabelsi", phone: "20 111 001", cin: "08123456");
        var other = harness.AddTunisian("Amel", "Ben Salah", phone: "22 222 002", cin: "09234567");
        using var front = SampleFiles.Stream(SampleFiles.Png());

        var result = await harness.Registration.AddOrReplaceDocumentAsync(other.Id, Cin("08123456"), front, cancellationToken: Ct);

        result.Error.ShouldBe(CustomersErrors.DocumentNumberInUse);
        harness.Storage.Files.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("empty")]
    [InlineData("large")]
    public async Task Unsupported_empty_or_oversized_files_are_rejected_before_anything_is_stored(string kind)
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var documentId = person.CurrentDocument(IdentityDocumentType.NationalId)!.Id;
        var (content, expected) = kind switch
        {
            "gif" => (SampleFiles.Gif(), CustomersErrors.ImageUnsupportedType),
            "empty" => (Array.Empty<byte>(), CustomersErrors.ImageEmpty),
            _ => (SampleFiles.Jpeg((int)DocumentImage.MaxLength + 1), CustomersErrors.ImageTooLarge),
        };
        using var stream = SampleFiles.Stream(content);

        var result = await harness.Registration.UploadDocumentImageAsync(documentId, DocumentImageSideInfo.Front, stream, Ct);

        result.Error.ShouldBe(expected);
        harness.Storage.Files.ShouldBeEmpty();
        harness.UnitOfWork.Executions.ShouldBe(0);
    }

    [Fact]
    public async Task Replacing_an_image_stores_the_new_file_and_deletes_the_previous_one_after_the_commit()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        using (var first = SampleFiles.Stream(SampleFiles.Jpeg()))
        {
            (await harness.Registration.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Front, first, Ct)).IsSuccess.ShouldBeTrue();
        }

        var firstPath = cin.FrontImage!.Path;
        using (var second = SampleFiles.Stream(SampleFiles.Png()))
        {
            (await harness.Registration.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Front, second, Ct)).IsSuccess.ShouldBeTrue();
        }

        cin.FrontImage!.Path.ShouldNotBe(firstPath);
        harness.Storage.Holds(firstPath).ShouldBeTrue("the previous file is only deleted by the outbox, after the commit");
        await harness.DeliverDiscardedImagesAsync();
        harness.Storage.Holds(firstPath).ShouldBeFalse();
        harness.Storage.Holds(cin.FrontImage.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task An_image_for_an_unknown_document_is_not_stored()
    {
        var harness = new CustomersHarness();
        using var content = SampleFiles.Stream(SampleFiles.Jpeg());

        var result = await harness.Registration.UploadDocumentImageAsync(Guid.CreateVersion7(), DocumentImageSideInfo.Back, content, Ct);

        result.Error.ShouldBe(CustomersErrors.DocumentNotFound);
        harness.Storage.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_storage_outage_leaves_the_document_unchanged()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        harness.Storage.FailUploads = true;
        using var content = SampleFiles.Stream(SampleFiles.Jpeg());

        await Should.ThrowAsync<IOException>(() => harness.Registration.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Front, content, Ct));

        cin.FrontImage.ShouldBeNull();
    }

    [Fact]
    public async Task Consents_and_closed_rentals_are_recorded()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var closedAt = CustomersHarness.Start.AddDays(-3);

        (await harness.Registration.SetConsentAsync(person.Id, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Phone, given: true, Ct)).IsSuccess.ShouldBeTrue();
        (await harness.Registration.RecordRentalClosedAsync(person.Id, closedAt, Ct)).IsSuccess.ShouldBeTrue();

        person.HasConsent(ConsentPurpose.WhatsAppNotifications).ShouldBeTrue();
        person.LastRentalClosedAtUtc.ShouldBe(closedAt);
        (await harness.Registration.SetConsentAsync(person.Id, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Email, given: false, Ct)).IsSuccess.ShouldBeTrue();
        person.HasConsent(ConsentPurpose.WhatsAppNotifications).ShouldBeFalse();
        (await harness.Registration.RecordRentalClosedAsync(Guid.CreateVersion7(), closedAt, Ct)).Error.ShouldBe(CustomersErrors.PersonNotFound);
    }
}
