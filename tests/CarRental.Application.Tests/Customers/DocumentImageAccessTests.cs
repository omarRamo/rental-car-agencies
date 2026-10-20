using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Domain.Customers;

namespace CarRental.Application.Tests.Customers;

/// <summary>Invariant 9: identity document images are read through an authorized access only, and every access is logged.</summary>
public sealed class DocumentImageAccessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(CustomersHarness Harness, IdentityDocument Cin, byte[] Front)> PersonWithImageAsync()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        var front = SampleFiles.Jpeg(4096);
        using var content = SampleFiles.Stream(front);
        (await harness.Registration.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Front, content, Ct)).IsSuccess.ShouldBeTrue();
        return (harness, cin, front);
    }

    [Fact]
    public async Task Viewing_an_image_streams_it_and_logs_who_viewed_which_document_and_why()
    {
        var (harness, cin, front) = await PersonWithImageAsync();

        var opened = await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Front, purpose: null, Ct);

        opened.IsSuccess.ShouldBeTrue(opened.ToString());
        await using var image = opened.Value;
        image.ContentType.ShouldBe("image/jpeg");
        image.FileName.ShouldBe("identity_card-front.jpg");
        using var copy = new MemoryStream();
        await image.Content.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(front);
        var entry = harness.AccessLog.Entries.ShouldHaveSingleItem();
        entry.DocumentId.ShouldBe(cin.Id);
        entry.Kind.ShouldBe("identity_card.front");
        entry.Access.ShouldBe(DocumentImageAccess.ViewAccess);
        entry.Reason.ShouldBe(DocumentAccessPurposes.CustomerFile);
    }

    [Fact]
    public async Task Every_read_is_logged_with_its_purpose()
    {
        var (harness, cin, _) = await PersonWithImageAsync();

        foreach (var purpose in new[] { DocumentAccessPurposes.Verification, DocumentAccessPurposes.BookingCheck })
        {
            var opened = await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Front, purpose, Ct);
            await opened.Value.DisposeAsync();
        }

        harness.AccessLog.Entries.Select(e => e.Reason).ShouldBe([DocumentAccessPurposes.Verification, DocumentAccessPurposes.BookingCheck]);
    }

    [Fact]
    public async Task A_temporary_link_lasts_five_minutes_and_is_logged()
    {
        var (harness, cin, _) = await PersonWithImageAsync();

        var link = await harness.ImageAccess.CreateTemporaryLinkAsync(cin.Id, DocumentImageSideInfo.Front, DocumentAccessPurposes.Dispute, Ct);

        link.IsSuccess.ShouldBeTrue();
        link.Value.ExpiresAtUtc.ShouldBe(CustomersHarness.Start + TimeSpan.FromMinutes(5));
        harness.Storage.Links.ShouldHaveSingleItem().Validity.ShouldBe(TimeSpan.FromMinutes(5));
        var entry = harness.AccessLog.Entries.ShouldHaveSingleItem();
        entry.Access.ShouldBe(DocumentImageAccess.TemporaryLinkAccess);
        entry.Reason.ShouldBe(DocumentAccessPurposes.Dispute);
    }

    [Theory]
    [InlineData(Roles.Accountant)]
    [InlineData(Roles.Preparer)]
    public async Task Users_without_the_document_permission_can_neither_view_nor_link_and_nothing_is_logged(string role)
    {
        var (harness, cin, _) = await PersonWithImageAsync();
        harness.As(role);

        (await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Front, null, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        (await harness.ImageAccess.CreateTemporaryLinkAsync(cin.Id, DocumentImageSideInfo.Front, null, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);

        harness.AccessLog.Entries.ShouldBeEmpty();
        harness.Storage.Links.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_manager_may_view_images()
    {
        var (harness, cin, _) = await PersonWithImageAsync();
        harness.As(Roles.Manager);

        await using var image = (await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Front, null, Ct)).Value;

        harness.AccessLog.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Unknown_purposes_missing_sides_and_documents_are_errors_and_nothing_is_logged()
    {
        var (harness, cin, _) = await PersonWithImageAsync();

        (await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Front, "curiosity", Ct)).Error.ShouldBe(CustomersErrors.AccessPurposeInvalid);
        (await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Back, null, Ct)).Error.ShouldBe(CustomersErrors.ImageNotFound);
        (await harness.ImageAccess.OpenAsync(cin.Id, (DocumentImageSideInfo)5, null, Ct)).Error.ShouldBe(CustomersErrors.ImageSideInvalid);
        (await harness.ImageAccess.OpenAsync(Guid.CreateVersion7(), DocumentImageSideInfo.Front, null, Ct)).Error.ShouldBe(CustomersErrors.DocumentNotFound);

        harness.AccessLog.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_missing_from_the_storage_is_not_logged_as_viewed()
    {
        var (harness, cin, _) = await PersonWithImageAsync();
        harness.Storage.Files.Clear();

        (await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Front, null, Ct)).Error.ShouldBe(CustomersErrors.ImageNotFound);

        harness.AccessLog.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_images_of_an_anonymized_person_are_gone()
    {
        var (harness, cin, _) = await PersonWithImageAsync();
        harness.Db.Rows<Person>().Single().Anonymize(CustomersHarness.Start).IsSuccess.ShouldBeTrue();

        (await harness.ImageAccess.OpenAsync(cin.Id, DocumentImageSideInfo.Front, null, Ct)).Error.ShouldBe(CustomersErrors.DocumentNotFound);
    }

    [Theory]
    [InlineData(null, "customer_file")]
    [InlineData(" ", "customer_file")]
    [InlineData("verification", "verification")]
    [InlineData("data_subject_request", "data_subject_request")]
    [InlineData("Verification", null)]
    [InlineData("other", null)]
    public void Purposes_are_stable_codes(string? input, string? expected)
    {
        DocumentAccessPurposes.Normalize(input).ShouldBe(expected);
    }
}
