using CarRental.Application.Common.Files;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Privacy;
using CarRental.Domain.Customers;
using CarRental.Domain.Identity;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Customers;

/// <summary>Anonymization end to end: the row is scrubbed, the documents removed, the files deleted by the outbox, the payer kept.</summary>
public sealed class PersonalDataTests(IntegrationHost host)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymization_scrubs_the_person_deletes_its_images_and_keeps_the_payer()
    {
        var (personId, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        var manager = await CustomerTestData.ManagerAsync(host);
        string imagePath;
        Guid customerId;
        await using (var scope = host.CreateScope(manager))
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            imagePath = (await db.Set<Person>().AsNoTracking().SingleAsync(p => p.Id == personId, Ct)).FindDocument(documentId)!.FrontImage!.Path;
            customerId = (await scope.ServiceProvider.GetRequiredService<ICustomerRegistration>().GetOrCreateIndividualCustomerAsync(personId, Ct)).Value;

            var result = await scope.ServiceProvider.GetRequiredService<PersonalDataService>().AnonymizeAsync(personId, "Demande écrite (test)", Ct);
            result.IsSuccess.ShouldBeTrue(result.ToString());
        }

        await using (var check = host.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var person = await db.Set<Person>().AsNoTracking().SingleAsync(p => p.Id == personId, Ct);
            person.IsAnonymized.ShouldBeTrue();
            person.FirstName.ShouldBeEmpty();
            person.Phone.ShouldBeNull();
            person.BirthDate.ShouldBeNull();
            person.Documents.ShouldBeEmpty();
            (await db.Set<IdentityDocument>().AnyAsync(d => d.Id == documentId, Ct)).ShouldBeFalse();
            (await db.Set<Customer>().AnyAsync(c => c.Id == customerId && c.PersonId == personId, Ct)).ShouldBeTrue();
            var audit = await db.Set<AuditLog>().SingleAsync(a => a.Action == CustomerAuditActions.PersonAnonymized && a.EntityId == personId.ToString(), Ct);
            audit.UserName.ShouldBe(manager.UserName);
            audit.Details.ShouldBe("Demande écrite (test)");

            var queries = check.ServiceProvider.GetRequiredService<ICustomersQueries>();
            (await queries.GetPersonAsync(personId, Ct))!.IsAnonymized.ShouldBeTrue();
        }

        // The file is deleted by the outbox handler once the anonymization is committed.
        var storage = host.Factory.Services.GetRequiredService<IFileStorage>();
        var deadline = DateTime.UtcNow + Timeout;
        while (await storage.ExistsAsync(FileContainers.Documents, imagePath, Ct))
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the image file should be deleted by the outbox");
            await Task.Delay(250, Ct);
        }
    }

    [Fact]
    public async Task The_agent_may_not_anonymize()
    {
        var (personId, _) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        var agent = await CustomerTestData.AgentAsync(host);
        await using var scope = host.CreateScope(agent);

        var result = await scope.ServiceProvider.GetRequiredService<PersonalDataService>().AnonymizeAsync(personId, "Demande", Ct);

        result.Error.ShouldBe(CustomersErrors.Forbidden);
    }
}
