using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Administration;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Application.Customers.Privacy;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Customers;

/// <summary>
/// The customers of an agency are its own (ADR-0067), on SQL Server: the staff of another agency never lists, reads,
/// changes nor exports a person, and never opens its documents; the same person or company registered by two agencies is
/// a customer of each, never merged; a blacklist is the agency's own.
/// </summary>
public sealed class CustomerAgencyIsolationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_staff_of_an_agency_never_lists_reads_changes_nor_opens_the_persons_of_another_agency()
    {
        var (first, second) = (await NewAgencyAsync("Clients A"), await NewAgencyAsync("Clients B"));
        var (personId, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct, TestActors.Staff(second, Roles.Agent));
        var lastName = (await PersonAsync(personId)).LastName;

        await using (var scope = host.CreateScope(TestActors.Staff(first, Roles.Manager)))
        {
            var queries = scope.ServiceProvider.GetRequiredService<ICustomersQueries>();
            (await queries.GetPersonAsync(personId, Ct)).ShouldBeNull();
            (await queries.SearchPersonsAsync(lastName, cancellationToken: Ct)).ShouldBeEmpty();
            var administration = scope.ServiceProvider.GetRequiredService<CustomerAdministrationQueries>();
            (await administration.SearchPersonsAsync(lastName, new PageRequest(), Ct)).Value.Items.ShouldBeEmpty();
            (await administration.GetPersonFileAsync(personId, Ct)).Error.ShouldBe(CustomersErrors.PersonNotFound);
            (await scope.ServiceProvider.GetRequiredService<PersonalDataService>().ExportAsync(personId, cancellationToken: Ct)).Error.ShouldBe(CustomersErrors.PersonNotFound);
            (await scope.ServiceProvider.GetRequiredService<DocumentImageAccess>().OpenAsync(documentId, DocumentImageSideInfo.Front, DocumentAccessPurposes.Verification, Ct))
                .Error.ShouldBe(CustomersErrors.DocumentNotFound, "a document of another agency is unknown, never shown");
        }

        foreach (var change in new Func<PersonAdministrationService, Task<Result>>[]
        {
            s => s.UpdateAsync(personId, new PersonDetailsForm("Intrus", "Intrus"), Ct),
            s => s.VerifyDocumentAsync(personId, documentId, Ct),
            s => s.BlacklistAsync(personId, "Essai d'une autre agence", Ct),
        })
        {
            await using var scope = host.CreateScope(TestActors.Staff(first, Roles.Manager));
            (await change(scope.ServiceProvider.GetRequiredService<PersonAdministrationService>())).Error.ShouldBe(CustomersErrors.PersonNotFound);
        }

        (await AccessesAsync(documentId)).ShouldBeEmpty("a refused access opens nothing");
        var person = await PersonAsync(personId);
        person.LastName.ShouldBe(lastName);
        person.Documents.ShouldAllBe(d => !d.IsVerified);

        // The agency of the person reads it, and its journal records the access with the agency.
        await using (var scope = host.CreateScope(TestActors.Staff(second, Roles.Manager)))
        {
            (await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetPersonAsync(personId, Ct)).ShouldNotBeNull();
            var opened = await scope.ServiceProvider.GetRequiredService<DocumentImageAccess>().OpenAsync(documentId, DocumentImageSideInfo.Front, DocumentAccessPurposes.Verification, Ct);
            opened.IsSuccess.ShouldBeTrue(opened.IsFailure ? opened.Error.ToString() : null);
            await opened.Value.DisposeAsync();
        }

        (await AccessesAsync(documentId)).ShouldHaveSingleItem().AgencyId.ShouldBe(second);
    }

    [Fact]
    public async Task An_operation_without_agency_or_for_another_agency_registers_nobody()
    {
        var (first, second) = (await NewAgencyAsync("Saisie A"), await NewAgencyAsync("Saisie B"));
        var request = new PersonRegistrationRequest(CustomerTestData.Name(), CustomerTestData.Name(), "TN", Phone: CustomerTestData.TunisianPhone());

        await using (var scope = host.CreateScope(TestActors.Staff(first, Roles.Agent)))
        {
            var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
            (await registration.FindOrCreatePersonAsync(request with { AgencyId = second }, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
            (await registration.CreateCompanyCustomerAsync(Company() with { AgencyId = second }, Ct)).Error.ShouldBe(CustomersErrors.Forbidden);
        }

        await using (var scope = host.CreateScope(UserContextSnapshot.System("system:tests")))
        {
            (await scope.ServiceProvider.GetRequiredService<ICustomerRegistration>().FindOrCreatePersonAsync(request, Ct))
                .Error.ShouldBe(CustomersErrors.AgencyRequired, "an operation across agencies names the agency of the customer");
        }

        await using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Person>().AnyAsync(p => p.LastName == request.LastName, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_same_person_and_company_registered_by_two_agencies_are_customers_of_each_never_merged()
    {
        var (first, second) = (await NewAgencyAsync("Doublon A"), await NewAgencyAsync("Doublon B"));
        var request = new PersonRegistrationRequest(
            CustomerTestData.Name(),
            CustomerTestData.Name(),
            "TN",
            new DateOnly(1990, 1, 1),
            CustomerTestData.TunisianPhone(),
            $"{CustomerTestData.Name().ToLowerInvariant()}@example.test",
            Document: CustomerTestData.CinInput(CustomerTestData.Cin()));

        var inFirst = await RegisterAsync(first, request);
        var inSecond = await RegisterAsync(second, request);
        var again = await RegisterAsync(first, request);

        inFirst.Created.ShouldBeTrue();
        inSecond.Created.ShouldBeTrue("the CIN, the phone and the e-mail are unique within an agency only");
        inSecond.PersonId.ShouldNotBe(inFirst.PersonId);
        inSecond.SimilarPersonIds.ShouldNotContain(inFirst.PersonId);
        again.PersonId.ShouldBe(inFirst.PersonId, "an agency still finds its own customer again");
        again.Created.ShouldBeFalse();

        (await RunAsync<CompanyCustomerAdministrationService, Result<Guid>>(first, s => s.CreateAsync(Company(), Ct))).IsSuccess.ShouldBeTrue();
        (await RunAsync<CompanyCustomerAdministrationService, Result<Guid>>(second, s => s.CreateAsync(Company(), Ct))).IsSuccess.ShouldBeTrue("a tax identifier is unique within an agency only");
        (await RunAsync<CompanyCustomerAdministrationService, Result<Guid>>(first, s => s.CreateAsync(Company(), Ct))).Error.ShouldBe(CustomersErrors.CompanyAlreadyExists);

        // A blacklist, a search and an erasure stay within the agency that made them.
        (await RunAsync<PersonAdministrationService, Result>(second, s => s.BlacklistAsync(inSecond.PersonId, "Impayé", Ct))).IsSuccess.ShouldBeTrue();
        await using (var scope = host.CreateScope(TestActors.Staff(first, Roles.Agent)))
        {
            var queries = scope.ServiceProvider.GetRequiredService<ICustomersQueries>();
            (await queries.IsBlacklistedAsync(inFirst.PersonId, Ct)).ShouldBeFalse();
            (await queries.SearchPersonsAsync(request.Document!.Number, cancellationToken: Ct)).Select(p => p.Id).ShouldBe([inFirst.PersonId]);
        }

        (await RunAsync<PersonalDataService, Result>(first, s => s.AnonymizeAsync(inFirst.PersonId, "Demande écrite du client", Ct))).IsSuccess.ShouldBeTrue();
        (await PersonAsync(inSecond.PersonId)).LastName.ShouldBe(request.LastName, "the customer of the other agency keeps its data");
        await using var check = host.CreateScope(TestActors.Staff(second, Roles.Agent));
        (await check.ServiceProvider.GetRequiredService<ICustomersQueries>().IsBlacklistedAsync(inSecond.PersonId, Ct)).ShouldBeTrue();
    }

    private static CompanyCustomerRequest Company() =>
        new("Atlas Conseil SA", "7654321/B/P/000", new PostalAddressInfo("45 avenue de la Liberté", null, "1002", "Tunis", "TN"));

    private async Task<PersonRegistrationResult> RegisterAsync(Guid agencyId, PersonRegistrationRequest request)
    {
        var registered = await RunAsync<ICustomerRegistration, Result<PersonRegistrationResult>>(agencyId, s => s.FindOrCreatePersonAsync(request, Ct), Roles.Agent);
        registered.IsSuccess.ShouldBeTrue(registered.IsFailure ? registered.Error.ToString() : null);
        return registered.Value;
    }

    /// <summary>A call of a service as a member of <paramref name="agencyId"/> (a manager unless said otherwise).</summary>
    private async Task<T> RunAsync<TService, T>(Guid agencyId, Func<TService, Task<T>> operation, string role = Roles.Manager)
        where TService : notnull
    {
        await using var scope = host.CreateScope(TestActors.Staff(agencyId, role));
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<PersonInfo> PersonAsync(Guid personId)
    {
        await using var scope = host.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetPersonAsync(personId, Ct)).ShouldNotBeNull();
    }

    private async Task<List<DocumentAccessLog>> AccessesAsync(Guid documentId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<DocumentAccessLog>()
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId)
            .ToListAsync(Ct);
    }

    private async Task<Guid> NewAgencyAsync(string label)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = Agency.Register(new AgencyDetails($"Agence {label} {Guid.NewGuid():N}", "Tunis")).Value;
        db.Set<Agency>().Add(agency);
        await db.SaveChangesAsync(Ct);
        return agency.Id;
    }
}
