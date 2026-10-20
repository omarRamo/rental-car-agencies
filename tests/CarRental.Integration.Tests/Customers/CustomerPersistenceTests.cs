using System.Text.Json;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Customers;

/// <summary>The person aggregate and the payers on SQL Server: mapping, temporal history, masked audit, unique keys, concurrency.</summary>
public sealed class CustomerPersistenceTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Person NewTunisian(string? cin = null)
    {
        var now = DateTimeOffset.UtcNow;
        var person = Person.Register(
            new PersonDetails(
                CustomerTestData.Name(),
                CustomerTestData.Name(),
                "TN",
                new DateOnly(1988, 4, 12),
                CustomerTestData.TunisianPhone(),
                $"{Guid.NewGuid():N}@example.com",
                PostalAddress.Create("15 rue d'Alger", null, "1000", "Tunis", "TN").Value),
            now).Value;
        person.AddDocument(new IdentityDocumentDetails(IdentityDocumentType.NationalId, cin ?? CustomerTestData.Cin(), "TN", new DateOnly(2016, 3, 1)), now).IsSuccess.ShouldBeTrue();
        return person;
    }

    private async Task SaveAsync(params object[] entities)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_person_aggregate_round_trips_with_its_documents_images_consents_and_blacklist()
    {
        var person = NewTunisian();
        var now = DateTimeOffset.UtcNow;
        var cin = person.CurrentDocument(IdentityDocumentType.NationalId)!;
        person.AttachDocumentImage(cin.Id, DocumentImageSide.Front, DocumentImage.Create($"persons/{person.Id:N}/front.png", "image/png", 68, now).Value, now).IsSuccess.ShouldBeTrue();
        person.AddDocument(new IdentityDocumentDetails(IdentityDocumentType.DrivingLicense, CustomerTestData.Cin(), "TN", new DateOnly(2008, 6, 15), IsLatinScript: false), now).IsSuccess.ShouldBeTrue();
        person.GiveConsent(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Counter, now).IsSuccess.ShouldBeTrue();
        person.Blacklist("Incident de test", new AuditActor(null, "tests", null, null), now).IsSuccess.ShouldBeTrue();
        await SaveAsync(person);

        await using var scope = host.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Person>().SingleAsync(p => p.Id == person.Id, Ct);

        loaded.FirstName.ShouldBe(person.FirstName);
        loaded.SearchName.ShouldBe(person.SearchName);
        loaded.Phone.ShouldBe(person.Phone);
        loaded.Address.ShouldBe(person.Address);
        loaded.Documents.Count.ShouldBe(2);
        var loadedCin = loaded.CurrentDocument(IdentityDocumentType.NationalId)!;
        loadedCin.FrontImage!.Path.ShouldBe($"persons/{person.Id:N}/front.png");
        loadedCin.FrontImage.UploadedAtUtc.ShouldBe(now, TimeSpan.FromMilliseconds(1));
        loadedCin.BackImage.ShouldBeNull("an optional image without columns is null");
        loaded.CurrentDocument(IdentityDocumentType.DrivingLicense)!.IsLatinScript.ShouldBeFalse();
        loaded.HasConsent(ConsentPurpose.WhatsAppNotifications).ShouldBeTrue();
        loaded.IsBlacklisted.ShouldBeTrue();
        loaded.ActiveBlacklistEntry!.AddedByName.ShouldBe("tests");
    }

    [Fact]
    public async Task A_person_without_address_has_a_null_address()
    {
        var person = Person.Register(new PersonDetails(CustomerTestData.Name(), CustomerTestData.Name()), DateTimeOffset.UtcNow).Value;
        await SaveAsync(person);

        await using var scope = host.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Person>().AsNoTracking().SingleAsync(p => p.Id == person.Id, Ct);

        loaded.Address.ShouldBeNull();
        loaded.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Persons_and_documents_keep_their_history_in_temporal_tables()
    {
        var person = NewTunisian();
        var firstPhone = person.Phone;
        await SaveAsync(person);

        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var loaded = await db.Set<Person>().SingleAsync(p => p.Id == person.Id, Ct);
            loaded.UpdateDetails(new PersonDetails(loaded.FirstName, loaded.LastName, "TN", loaded.BirthDate, CustomerTestData.TunisianPhone()), DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            loaded.AddDocument(new IdentityDocumentDetails(IdentityDocumentType.NationalId, CustomerTestData.Cin(), "TN", new DateOnly(2024, 1, 1)), DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        await using var check = host.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var phones = await context.Set<Person>().TemporalAll().IgnoreAutoIncludes().Where(p => p.Id == person.Id).Select(p => p.Phone).ToListAsync(Ct);
        var documentVersions = await context.Set<IdentityDocument>().TemporalAll().Where(d => d.PersonId == person.Id).Select(d => d.SupersededAtUtc).ToListAsync(Ct);

        phones.Count.ShouldBeGreaterThanOrEqualTo(2);
        phones.ShouldContain(firstPhone);
        documentVersions.Count.ShouldBe(3, "the first CIN before and after its supersession, then the new one");
    }

    [Fact]
    public async Task Personal_data_and_document_numbers_are_masked_in_the_audit_log()
    {
        var person = NewTunisian();
        await SaveAsync(person);

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var personAudit = await db.Set<AuditLog>().SingleAsync(a => a.EntityType == "Person" && a.EntityId == person.Id.ToString() && a.Action == AuditActions.EntityCreated, Ct);
        var documentId = person.Documents.Single().Id.ToString();
        var documentAudit = await db.Set<AuditLog>().SingleAsync(a => a.EntityType == "IdentityDocument" && a.EntityId == documentId, Ct);

        var after = JsonDocument.Parse(personAudit.After!).RootElement;
        foreach (var field in new[] { "FirstName", "LastName", "SearchName", "Phone", "Email", "BirthDate", "Address.Line1", "Address.City" })
        {
            after.GetProperty(field).GetString().ShouldBe("***", field);
        }

        after.GetProperty("Nationality").GetString().ShouldBe("TN");
        after.TryGetProperty("LastModifiedAtUtc", out _).ShouldBeFalse();
        var document = JsonDocument.Parse(documentAudit.After!).RootElement;
        document.GetProperty("Number").GetString().ShouldBe("***");
        document.GetProperty("NumberKey").GetString().ShouldBe("***");
        document.GetProperty("IssuingCountry").GetString().ShouldBe("TN");
        personAudit.After!.ShouldNotContain(person.Phone!);
    }

    [Fact]
    public async Task Two_persons_cannot_hold_the_same_current_document()
    {
        var cin = CustomerTestData.Cin();
        await SaveAsync(NewTunisian(cin));

        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(NewTunisian(cin)));
    }

    [Fact]
    public async Task A_person_has_one_individual_payer_and_a_tax_identifier_one_company()
    {
        var person = NewTunisian();
        await SaveAsync(person, Customer.ForPerson(person.Id, DateTimeOffset.UtcNow).Value);
        var taxIdentifier = $"{Random.Shared.Next(1_000_000, 9_999_999)}/A/M/000";
        var address = PostalAddress.Create("45 avenue de la Liberté", null, "1002", "Tunis", "TN").Value;
        await SaveAsync(Customer.ForCompany(new CompanyDetails("Société A", taxIdentifier, address), DateTimeOffset.UtcNow).Value);

        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(Customer.ForPerson(person.Id, DateTimeOffset.UtcNow).Value));
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(Customer.ForCompany(new CompanyDetails("Société B", taxIdentifier, address), DateTimeOffset.UtcNow).Value));
    }

    [Fact]
    public async Task A_change_to_a_document_also_protects_the_person_against_a_concurrent_edit()
    {
        var person = NewTunisian();
        await SaveAsync(person);
        await using var first = host.CreateScope();
        await using var second = host.CreateScope();
        var firstDb = first.ServiceProvider.GetRequiredService<IAppDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<IAppDbContext>();
        var inFirst = await firstDb.Set<Person>().SingleAsync(p => p.Id == person.Id, Ct);
        var inSecond = await secondDb.Set<Person>().SingleAsync(p => p.Id == person.Id, Ct);

        inFirst.VerifyDocument(inFirst.Documents.Single().Id, new AuditActor(null, "agent", null, null), DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        await firstDb.SaveChangesAsync(Ct);
        inSecond.Blacklist("Décision concurrente", new AuditActor(null, "agent2", null, null), DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(Ct));
    }
}
