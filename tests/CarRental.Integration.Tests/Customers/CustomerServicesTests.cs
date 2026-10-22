using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Customers.Administration;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Customers;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Customers;

/// <summary>The Customers services on SQL Server: deduplication, search (collation-independent keys), demo data, retention.</summary>
public sealed class CustomerServicesTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_person_is_found_again_by_phone_and_name_or_by_document_without_duplicate()
    {
        var agent = await CustomerTestData.AgentAsync(host);
        var first = CustomerTestData.Name();
        var last = CustomerTestData.Name();
        var phone = CustomerTestData.TunisianPhone();
        var cin = CustomerTestData.Cin();
        await using var scope = host.CreateScope(agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();

        var created = await registration.FindOrCreatePersonAsync(new PersonRegistrationRequest(first, last, "TN", Phone: phone, Document: CustomerTestData.CinInput(cin)), Ct);
        var byPhone = await registration.FindOrCreatePersonAsync(new PersonRegistrationRequest(first.ToUpperInvariant(), last, Phone: $"+216 {phone[..2]} {phone[2..5]} {phone[5..]}"), Ct);
        var byDocument = await registration.FindOrCreatePersonAsync(new PersonRegistrationRequest(CustomerTestData.Name(), last, Document: CustomerTestData.CinInput(cin)), Ct);
        var relative = await registration.FindOrCreatePersonAsync(new PersonRegistrationRequest(CustomerTestData.Name(), last, Phone: phone), Ct);

        created.Value.Created.ShouldBeTrue();
        byPhone.Value.PersonId.ShouldBe(created.Value.PersonId);
        byPhone.Value.Match.ShouldBe(PersonMatchKindInfo.Phone);
        byDocument.Value.PersonId.ShouldBe(created.Value.PersonId);
        byDocument.Value.NameDiffers.ShouldBeTrue();
        relative.Value.Created.ShouldBeTrue();
        relative.Value.SimilarPersonIds.ShouldContain(created.Value.PersonId);
    }

    [Fact]
    public async Task The_search_ignores_accents_case_and_formats_on_sql_server()
    {
        var agent = await CustomerTestData.AgentAsync(host);
        var last = CustomerTestData.Name();
        var phone = CustomerTestData.TunisianPhone();
        var cin = CustomerTestData.Cin();
        await using var scope = host.CreateScope(agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var id = (await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest("Hédi", $"Ben-{last}", "TN", Phone: phone, Email: $"{last.ToLowerInvariant()}@example.com", Document: CustomerTestData.CinInput(cin)),
            Ct)).Value.PersonId;
        var queries = scope.ServiceProvider.GetRequiredService<ICustomersQueries>();

        foreach (var term in new[] { $"hedi ben {last.ToLowerInvariant()}", $"BEN {last.ToUpperInvariant()}", $"00216{phone}", phone[2..], cin[..5], $"{last.ToLowerInvariant()}@" })
        {
            (await queries.SearchPersonsAsync(term, cancellationToken: Ct)).ShouldContain(p => p.Id == id, term);
        }

        var page = await scope.ServiceProvider.GetRequiredService<CustomerAdministrationQueries>().SearchPersonsAsync($"hédi {last}", new PageRequest(), Ct);
        page.Value.Items.ShouldHaveSingleItem().Id.ShouldBe(id);
    }

    [Fact]
    public async Task Eligibility_is_checked_with_the_seeded_settings()
    {
        var agent = await CustomerTestData.AgentAsync(host);
        await using var scope = host.CreateScope(agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var id = (await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(CustomerTestData.Name(), CustomerTestData.Name(), "TN", new DateOnly(1990, 1, 1), CustomerTestData.TunisianPhone(), Document: CustomerTestData.CinInput(CustomerTestData.Cin())),
            Ct)).Value.PersonId;
        (await registration.AddOrReplaceDocumentAsync(id, new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, CustomerTestData.Cin(), "TN", new DateOnly(2010, 1, 1)), cancellationToken: Ct))
            .IsSuccess.ShouldBeTrue();
        var pickup = DateTimeOffset.UtcNow.AddDays(10);

        var result = await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().CheckDriverEligibilityAsync(id, pickup, pickup.AddDays(3), cancellationToken: Ct);

        result.Value.IsEligible.ShouldBeTrue();
        result.Value.MinimumAge.ShouldBe(21);
        result.Value.MinimumLicenseYears.ShouldBe(2);
    }

    [Fact]
    public async Task Demo_customers_cover_the_typical_profiles_and_the_seed_is_idempotent()
    {
        var manager = await CustomerTestData.ManagerAsync(host);
        await using var scope = host.CreateScope(manager);
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var queries = scope.ServiceProvider.GetRequiredService<ICustomersQueries>();
        var demoEmails = new[] { "sami.trabelsi@example.com", "walid.ferchichi@example.com", "ahmed.alfitouri@example.net", "hans.mueller@example.net", "nour.jaziri@example.com" };
        var persons = await db.Set<Person>().AsNoTracking().Where(p => demoEmails.Contains(p.Email!)).ToListAsync(Ct);
        var demoCount = await db.Set<Person>().CountAsync(p => p.Email != null && (p.Email.EndsWith("@example.com") || p.Email.EndsWith("@example.org") || p.Email.EndsWith("@example.net")), Ct);

        persons.Count.ShouldBe(5);
        demoCount.ShouldBeGreaterThanOrEqualTo(15);
        (await db.Set<Customer>().CountAsync(c => c.Kind == CustomerKind.Company && (c.TaxIdentifierKey == "1234567AM000" || c.TaxIdentifierKey == "7654321BP000"), Ct)).ShouldBe(2);

        var sami = persons.Single(p => p.Email == "sami.trabelsi@example.com");
        var cin = sami.CurrentDocument(IdentityDocumentType.NationalId)!;
        cin.IsVerified.ShouldBeTrue();
        cin.FrontImage.ShouldNotBeNull();
        (await scope.ServiceProvider.GetRequiredService<IFileStorage>().ExistsAsync(FileContainers.Documents, cin.FrontImage.Path, Ct)).ShouldBeTrue();
        (await queries.IsBlacklistedAsync(persons.Single(p => p.Email == "walid.ferchichi@example.com").Id, Ct)).ShouldBeTrue();

        var pickup = DateTimeOffset.UtcNow.AddDays(7);
        var libyan = await queries.CheckDriverEligibilityAsync(persons.Single(p => p.Email == "ahmed.alfitouri@example.net").Id, pickup, pickup.AddDays(4), cancellationToken: Ct);
        var expiredLicense = await queries.CheckDriverEligibilityAsync(persons.Single(p => p.Email == "hans.mueller@example.net").Id, pickup, pickup.AddDays(4), cancellationToken: Ct);
        var recentLicense = await queries.CheckDriverEligibilityAsync(persons.Single(p => p.Email == "nour.jaziri@example.com").Id, pickup, pickup.AddDays(4), cancellationToken: Ct);
        libyan.Value.IsEligible.ShouldBeTrue("non-latin licence with a valid international permit");
        expiredLicense.Value.Issues.ShouldContain(i => i.Kind == EligibilityIssueKindInfo.DocumentExpiresBeforeReturn && i.DocumentType == IdentityDocumentTypeInfo.DrivingLicense);
        recentLicense.Value.Issues.ShouldContain(i => i.Kind == EligibilityIssueKindInfo.LicenseHeldTooShort);

        var seeder = scope.ServiceProvider.GetServices<IDataSeeder>().Single(s => s.GetType().Name == "CustomersDemoSeeder");
        seeder.Stage.ShouldBe(SeedStage.Demo);
        seeder.Order.ShouldBe(600);
        await seeder.SeedAsync(Ct);
        (await db.Set<Person>().CountAsync(p => demoEmails.Contains(p.Email), Ct)).ShouldBe(5);
    }

    [Fact]
    public async Task Payers_and_companies_are_read_and_searched_on_sql_server()
    {
        var (personId, _) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        var agent = await CustomerTestData.AgentAsync(host);
        await using var scope = host.CreateScope(agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var queries = scope.ServiceProvider.GetRequiredService<ICustomersQueries>();
        var customerId = (await registration.GetOrCreateIndividualCustomerAsync(personId, Ct)).Value;

        var individual = await queries.GetCustomerAsync(customerId, Ct);
        var byPerson = await queries.FindIndividualCustomerAsync(personId, Ct);
        var companies = await queries.SearchCompaniesAsync("atlas", cancellationToken: Ct);
        var byTaxIdentifier = await queries.SearchCompaniesAsync("1234567", cancellationToken: Ct);
        var listed = await scope.ServiceProvider.GetRequiredService<CustomerAdministrationQueries>().SearchCompaniesAsync("tourisme", new PageRequest(), Ct);

        individual!.Kind.ShouldBe(CustomerKindInfo.Individual);
        individual.PersonId.ShouldBe(personId);
        individual.DisplayName.ShouldNotBeNullOrWhiteSpace();
        byPerson!.Id.ShouldBe(customerId);
        companies.ShouldContain(c => c.LegalName == "Atlas Conseil SA" && c.BillingAddress!.City == "Tunis");
        byTaxIdentifier.ShouldContain(c => c.TaxIdentifier == "1234567/A/M/000");
        listed.Value.Items.ShouldContain(c => c.LegalName == "Société Méditerranéenne de Tourisme SARL");
    }

    [Fact]
    public async Task When_enabled_the_retention_job_purges_old_images_and_deletes_their_files()
    {
        // An image uploaded 500 days ago, more than the 400 days of retention.
        var now = DateTimeOffset.UtcNow;
        var person = Person.Register(await TestStaff.DemoAgencyIdAsync(host), new PersonDetails(CustomerTestData.Name(), CustomerTestData.Name(), "TN"), now).Value;
        var cin = person.AddDocument(new IdentityDocumentDetails(IdentityDocumentType.NationalId, CustomerTestData.Cin(), "TN", new DateOnly(2016, 3, 1)), now).Value;
        var path = $"persons/{person.Id:N}/{Guid.CreateVersion7():N}.png";
        var storage = host.Factory.Services.GetRequiredService<IFileStorage>();
        await using (var content = new MemoryStream(CustomerTestData.Png()))
        {
            await storage.UploadAsync(FileContainers.Documents, path, content, "image/png", Ct);
        }

        person.AttachDocumentImage(cin.Id, DocumentImageSide.Front, DocumentImage.Create(path, "image/png", 68, now.AddDays(-500)).Value, now).IsSuccess.ShouldBeTrue();
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            db.Add(person);
            await db.SaveChangesAsync(Ct);
        }

        await SetRetentionAsync(enabled: true);
        try
        {
            await using var scope = host.CreateScope();
            var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == "customers.purge_document_images");
            await ((IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType)).ExecuteAsync(Ct);
        }
        finally
        {
            await SetRetentionAsync(enabled: false);
        }

        await using (var check = host.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var reloaded = await db.Set<Person>().AsNoTracking().SingleAsync(p => p.Id == person.Id, Ct);
            reloaded.FindDocument(cin.Id)!.FrontImage.ShouldBeNull();
            reloaded.FindDocument(cin.Id)!.NumberKey.ShouldBe(cin.NumberKey, "the document data is kept");
            (await db.Set<AuditLog>().AnyAsync(a => a.Action == CustomerAuditActions.DocumentImagesPurged && a.OccurredAtUtc >= now, Ct)).ShouldBeTrue();
        }

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (await storage.ExistsAsync(FileContainers.Documents, path, Ct))
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the purged file should be deleted by the outbox");
            await Task.Delay(250, Ct);
        }
    }

    [Fact]
    public async Task The_retention_job_purges_nothing_while_disabled_by_default()
    {
        var (personId, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        await using var scope = host.CreateScope();
        var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == "customers.purge_document_images");
        var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType);

        await job.ExecuteAsync(Ct);

        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var person = await db.Set<Person>().AsNoTracking().SingleAsync(p => p.Id == personId, Ct);
        person.FindDocument(documentId)!.FrontImage.ShouldNotBeNull();
    }

    /// <summary>Sets the platform value of the retention switch (only the tests of this class run the job, one at a time).</summary>
    private async Task SetRetentionAsync(bool enabled)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var setting = await db.Set<Setting>()
            .SingleAsync(s => s.Key == CustomerSettings.DocumentImageRetentionEnabled.Key && s.AgencyId == null, Ct);
        setting.ChangeValue(CustomerSettings.DocumentImageRetentionEnabled.Format(enabled)).IsSuccess.ShouldBeTrue();
        await db.SaveChangesAsync(Ct);
    }
}
