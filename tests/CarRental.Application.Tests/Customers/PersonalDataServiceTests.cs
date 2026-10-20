using System.Text.Json;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Application.Tests.Customers;

/// <summary>Data subject rights: export and anonymization, manager only and audited.</summary>
public sealed class PersonalDataServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(CustomersHarness Harness, Person Person, string FrontPath)> PersonWithDataAsync()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian(email: "sami@example.com");
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        using (var content = SampleFiles.Stream(SampleFiles.Jpeg()))
        {
            (await harness.Registration.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Front, content, Ct)).IsSuccess.ShouldBeTrue();
        }

        person.GiveConsent(ConsentPurpose.MarketingEmail, ConsentChannel.Web, CustomersHarness.Start).IsSuccess.ShouldBeTrue();
        person.Blacklist("Retard de restitution non signalé", new AuditActor(null, "agent", null, null), CustomersHarness.Start).IsSuccess.ShouldBeTrue();
        person.LiftBlacklist("Accord trouvé", new AuditActor(null, "gerant", null, null), CustomersHarness.Start).IsSuccess.ShouldBeTrue();
        (await harness.Registration.GetOrCreateIndividualCustomerAsync(person.Id, Ct)).IsSuccess.ShouldBeTrue();
        harness.As(Roles.Manager);
        return (harness, person, cin.FrontImage!.Path);
    }

    [Fact]
    public async Task The_export_holds_the_personal_data_without_images_and_is_audited()
    {
        var (harness, person, _) = await PersonWithDataAsync();

        var export = await harness.PersonalData.ExportAsync(person.Id, cancellationToken: Ct);

        export.IsSuccess.ShouldBeTrue(export.ToString());
        export.Value.ContentType.ShouldBe("application/json");
        export.Value.FileName.ShouldBe($"personal-data-{person.Id:N}-20270601.json");
        using var json = JsonDocument.Parse(export.Value.Content);
        var root = json.RootElement;
        root.GetProperty("person").GetProperty("lastName").GetString().ShouldBe("Trabelsi");
        root.GetProperty("person").GetProperty("phone").GetString().ShouldBe("+21620111001");
        var document = root.GetProperty("identityDocuments").EnumerateArray().ShouldHaveSingleItem();
        document.GetProperty("type").GetString().ShouldBe("NationalId");
        document.GetProperty("number").GetString().ShouldBe("08123456");
        document.GetProperty("hasFrontImage").GetBoolean().ShouldBeTrue();
        root.GetProperty("consents").EnumerateArray().ShouldHaveSingleItem().GetProperty("purpose").GetString().ShouldBe("MarketingEmail");
        root.GetProperty("payerAccounts").GetArrayLength().ShouldBe(1);
        root.GetProperty("internalRecords").ValueKind.ShouldBe(JsonValueKind.Null);
        export.Value.Content.ToArray().ShouldNotContain((byte)0xFF, "no image bytes in the export");

        var audit = harness.Audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(CustomerAuditActions.PersonExported);
        audit.EntityType.ShouldBe("Person");
        audit.EntityId.ShouldBe(person.Id.ToString());
    }

    [Fact]
    public async Task The_blacklist_is_exported_only_on_request()
    {
        var (harness, person, _) = await PersonWithDataAsync();

        var export = await harness.PersonalData.ExportAsync(person.Id, includeInternalRecords: true, Ct);

        using var json = JsonDocument.Parse(export.Value.Content);
        json.RootElement.GetProperty("internalRecords").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("reason").GetString().ShouldBe("Retard de restitution non signalé");
        harness.Audit.Events.ShouldHaveSingleItem().Details.ShouldBe("with internal records");
    }

    [Fact]
    public async Task Anonymization_scrubs_the_person_deletes_its_images_keeps_its_payer_and_is_audited()
    {
        var (harness, person, frontPath) = await PersonWithDataAsync();
        var customer = harness.Db.Rows<Customer>().Single();

        var result = await harness.PersonalData.AnonymizeAsync(person.Id, "Demande écrite du 01/06/2027", Ct);

        result.IsSuccess.ShouldBeTrue(result.ToString());
        person.IsAnonymized.ShouldBeTrue();
        person.LastName.ShouldBeEmpty();
        person.Phone.ShouldBeNull();
        person.Email.ShouldBeNull();
        person.Documents.ShouldBeEmpty();
        person.Consents.ShouldBeEmpty();
        harness.Db.Rows<Customer>().ShouldHaveSingleItem().ShouldBe(customer);
        var audit = harness.Audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(CustomerAuditActions.PersonAnonymized);
        audit.Details.ShouldBe("Demande écrite du 01/06/2027");

        harness.Storage.Holds(frontPath).ShouldBeTrue("the files are deleted by the outbox once committed");
        await harness.DeliverDiscardedImagesAsync();
        harness.Storage.Holds(frontPath).ShouldBeFalse();
        harness.Storage.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task Anonymizing_again_changes_nothing()
    {
        var (harness, person, _) = await PersonWithDataAsync();
        (await harness.PersonalData.AnonymizeAsync(person.Id, "Demande", Ct)).IsSuccess.ShouldBeTrue();

        (await harness.PersonalData.AnonymizeAsync(person.Id, "Demande", Ct)).IsSuccess.ShouldBeTrue();

        harness.Audit.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_blacklisted_person_is_not_anonymized()
    {
        var (harness, person, _) = await PersonWithDataAsync();
        person.Blacklist("Nouvel incident", new AuditActor(null, "agent", null, null), CustomersHarness.Start).IsSuccess.ShouldBeTrue();

        (await harness.PersonalData.AnonymizeAsync(person.Id, "Demande", Ct)).Error.ShouldBe(CustomersErrors.BlacklistedPersonCannotBeAnonymized);

        person.IsAnonymized.ShouldBeFalse();
        harness.Audit.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_reason_of_an_anonymization_is_required(string reason)
    {
        var (harness, person, _) = await PersonWithDataAsync();

        (await harness.PersonalData.AnonymizeAsync(person.Id, reason, Ct)).Error.ShouldBe(CustomersErrors.PrivacyReasonRequired);
        (await harness.PersonalData.AnonymizeAsync(person.Id, new string('x', 501), Ct)).Error.ShouldBe(CustomersErrors.PrivacyReasonRequired);
    }

    [Theory]
    [InlineData(Roles.Agent)]
    [InlineData(Roles.Accountant)]
    public async Task Only_the_manager_exports_or_anonymizes(string role)
    {
        var (harness, person, _) = await PersonWithDataAsync();
        harness.As(role);

        (await harness.PersonalData.ExportAsync(person.Id, cancellationToken: Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.PersonalData.AnonymizeAsync(person.Id, "Demande", Ct)).Error.ShouldBe(CustomersErrors.Forbidden);

        person.IsAnonymized.ShouldBeFalse();
        harness.Audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_persons_are_not_found()
    {
        var harness = new CustomersHarness(Roles.Manager);

        (await harness.PersonalData.ExportAsync(Guid.CreateVersion7(), cancellationToken: Ct)).Error.ShouldBe(CustomersErrors.PersonNotFound);
        (await harness.PersonalData.AnonymizeAsync(Guid.CreateVersion7(), "Demande", Ct)).Error.ShouldBe(CustomersErrors.PersonNotFound);
    }
}
