using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Domain.Customers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarRental.Application.Tests.Customers;

/// <summary>Retention of document images: disabled by default (legal procedure not validated), purges old images when enabled.</summary>
public sealed class DocumentImageRetentionJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(CustomersHarness Harness, IdentityDocument Cin)> PersonWithImagesAsync()
    {
        var harness = new CustomersHarness();
        var person = harness.AddTunisian();
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        using (var front = SampleFiles.Stream(SampleFiles.Jpeg()))
        {
            (await harness.Registration.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Front, front, Ct)).IsSuccess.ShouldBeTrue();
        }

        harness.Clock.Advance(TimeSpan.FromDays(300));
        using (var back = SampleFiles.Stream(SampleFiles.Png()))
        {
            (await harness.Registration.UploadDocumentImageAsync(cin.Id, DocumentImageSideInfo.Back, back, Ct)).IsSuccess.ShouldBeTrue();
        }

        harness.Clock.Advance(TimeSpan.FromDays(150));
        return (harness, cin);
    }

    private static DocumentImageRetentionJob Job(CustomersHarness harness, FakeBookingsQueries? bookings = null) =>
        new(harness.Db, harness.Settings, harness.Audit, bookings ?? new FakeBookingsQueries(), harness.Clock, NullLogger<DocumentImageRetentionJob>.Instance);

    [Fact]
    public void The_job_runs_every_night_in_tunis()
    {
        DocumentImageRetentionJob.JobId.ShouldBe("customers.purge_document_images");
        DocumentImageRetentionJob.CronExpression.ShouldBe("30 3 * * *");
    }

    [Fact]
    public async Task Nothing_is_purged_while_the_retention_is_disabled_by_default()
    {
        var (harness, cin) = await PersonWithImagesAsync();

        await Job(harness).ExecuteAsync(Ct);

        cin.FrontImage.ShouldNotBeNull();
        cin.BackImage.ShouldNotBeNull();
        harness.Audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Images_older_than_the_retention_period_are_purged_and_their_files_deleted()
    {
        var (harness, cin) = await PersonWithImagesAsync();
        harness.Settings.Set(CustomerSettings.DocumentImageRetentionEnabled, true);
        var frontPath = cin.FrontImage!.Path;

        await Job(harness).ExecuteAsync(Ct);

        cin.FrontImage.ShouldBeNull("uploaded 450 days ago, more than the 400 days of retention");
        cin.BackImage.ShouldNotBeNull("uploaded 150 days ago");
        cin.Number.ShouldBe("08123456", "the document data is kept");
        var audit = harness.Audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(CustomerAuditActions.DocumentImagesPurged);
        audit.Details.ShouldBe("1 image(s) of 1 person(s) kept more than 400 days");

        await harness.DeliverDiscardedImagesAsync();
        harness.Storage.Holds(frontPath).ShouldBeFalse();
        harness.Storage.Holds(cin.BackImage!.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task A_recently_closed_rental_restarts_the_retention_period()
    {
        var (harness, cin) = await PersonWithImagesAsync();
        harness.Settings.Set(CustomerSettings.DocumentImageRetentionEnabled, true);
        harness.Db.Rows<Person>().Single().RecordRentalClosed(harness.Clock.GetUtcNow().AddDays(-20));

        await Job(harness).ExecuteAsync(Ct);

        cin.FrontImage.ShouldNotBeNull();
        harness.Audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_driver_of_an_open_rental_file_keeps_the_images_of_their_documents()
    {
        var (harness, cin) = await PersonWithImagesAsync();
        harness.Settings.Set(CustomerSettings.DocumentImageRetentionEnabled, true);
        var bookings = new FakeBookingsQueries();
        bookings.PersonsWithOpenBookings.Add(harness.Db.Rows<Person>().Single().Id);

        await Job(harness, bookings).ExecuteAsync(Ct);

        cin.FrontImage.ShouldNotBeNull("uploaded 450 days ago, but the person has an open booking");
        harness.Audit.Events.ShouldBeEmpty();

        bookings.PersonsWithOpenBookings.Clear();
        await Job(harness, bookings).ExecuteAsync(Ct);
        cin.FrontImage.ShouldBeNull("purged once the booking is closed");
    }

    [Fact]
    public async Task The_retention_period_comes_from_the_settings()
    {
        var (harness, cin) = await PersonWithImagesAsync();
        harness.Settings.Set(CustomerSettings.DocumentImageRetentionEnabled, true);
        harness.Settings.Set(CustomerSettings.DocumentImageRetentionDays, 100);

        await Job(harness).ExecuteAsync(Ct);

        cin.FrontImage.ShouldBeNull();
        cin.BackImage.ShouldBeNull();
    }
}
