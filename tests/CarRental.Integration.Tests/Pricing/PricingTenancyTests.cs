using CarRental.Application.Common.Persistence;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using CarRental.Infrastructure.Persistence.Interceptors;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Pricing.PricingScenario;

namespace CarRental.Integration.Tests.Pricing;

/// <summary>
/// The rate plans of two agencies never meet (ADR-0067), on SQL Server: the staff of an agency list, read, edit and quote
/// with their own plans only (the published plans kept in memory for every agency included), and two agencies may have
/// plans of the same structure, each with its own versions.
/// </summary>
public sealed class PricingTenancyTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_agencies_publish_the_same_grid_each_with_its_own_versions()
    {
        var (a, b) = (await CreateAgencyAsync(host, Ct), await CreateAgencyAsync(host, Ct));

        var first = await PublishReferencePlanAsync(host, a, Ct);
        var second = await PublishReferencePlanAsync(host, b, Ct);

        (first.Plan.Version, second.Plan.Version).ShouldBe((1, 1));
        first.Plan.AgencyId.ShouldBe(a.AgencyId);
        second.Plan.AgencyId.ShouldBe(b.AgencyId);
        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.Set<CategoryPrice>().Where(p => p.RatePlanId == second.Plan.Id).Select(p => p.AgencyId).Distinct().ToListAsync(Ct)).ShouldBe([b.AgencyId]);
        (await db.Set<Extra>().Where(e => e.RatePlanId == second.Plan.Id).Select(e => e.AgencyId).Distinct().ToListAsync(Ct)).ShouldBe([b.AgencyId]);
        (await db.Set<Season>().Where(s => s.RatePlanId == first.Plan.Id).Select(s => s.AgencyId).Distinct().ToListAsync(Ct)).ShouldBe([a.AgencyId]);
    }

    [Fact]
    public async Task The_staff_of_an_agency_see_and_edit_their_own_plans_only()
    {
        var (a, b) = (await CreateAgencyAsync(host, Ct), await CreateAgencyAsync(host, Ct));
        var published = await PublishReferencePlanAsync(host, b, Ct);
        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(b.AgencyId, null, "Brouillon de B"), Ct));

        await using (var scope = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Manager)))
        {
            var plans = scope.ServiceProvider.GetRequiredService<RatePlanAdministrationService>();
            (await plans.ListAsync(null, includeArchived: true, Ct)).Value.ShouldBeEmpty();
            (await plans.ListAsync(b.AgencyId, includeArchived: true, Ct)).Value.ShouldBeEmpty();
            (await plans.GetHistoryAsync(b.AgencyId, null, Ct)).Value.ShouldBeEmpty();
            (await plans.GetAsync(published.Plan.Id, Ct)).Error.ShouldBe(PricingErrors.PlanNotFound);
            (await plans.ValidateAsync(draftId, Ct)).Error.ShouldBe(PricingErrors.PlanNotFound);
            (await plans.RenameAsync(draftId, 0, "Intrusion", Ct)).Error.ShouldBe(PricingErrors.PlanNotFound);
            (await plans.UpdateSeasonsAsync(draftId, 0, [new SeasonDefinition(SeasonLevel.High, new DateOnly(2027, 7, 1), new DateOnly(2027, 8, 31))], Ct))
                .Error.ShouldBe(PricingErrors.PlanNotFound);
            (await plans.PublishAsync(draftId, 0, Ct)).Error.ShouldBe(PricingErrors.PlanNotFound);
            (await plans.ArchiveAsync(published.Plan.Id, Ct)).Error.ShouldBe(PricingErrors.PlanNotFound);
            (await plans.CreateDraftAsync(new CreateDraftRequest(b.AgencyId, null), Ct)).Error.ShouldBe(PricingErrors.AgencyNotFound);
            (await plans.CreateDraftAsync(new CreateDraftRequest(a.AgencyId, null, SourcePlanId: published.Plan.Id), Ct)).Error.ShouldBe(PricingErrors.PlanNotFound);

            // Even a write made without the services is refused by the persistence layer.
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<RatePlan>().Add(RatePlan.CreateDraft(b.AgencyId, SalesChannel.Web, "Grille pirate", DateTimeOffset.UtcNow).Value);
            await Should.ThrowAsync<AgencyBoundaryException>(() => db.SaveChangesAsync(Ct));
        }

        await using var check = host.CreateScope();
        var plan = await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<RatePlan>().AsNoTracking().SingleAsync(p => p.Id == published.Plan.Id, Ct);
        plan.Status.ShouldBe(RatePlanStatus.Published);
        (await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<RatePlan>().AsNoTracking().SingleAsync(p => p.Id == draftId, Ct)).Name.ShouldBe("Brouillon de B");
    }

    [Fact]
    public async Task An_agency_never_quotes_with_the_published_plan_of_another_even_from_memory()
    {
        var (a, b) = (await CreateAgencyAsync(host, Ct), await CreateAgencyAsync(host, Ct));
        await PublishReferencePlanAsync(host, a, Ct);
        await PublishReferencePlanAsync(host, b, Ct);

        // The plan of B is loaded in the shared memory by a quote of the platform.
        await using (var system = host.CreateScope())
        {
            (await system.ServiceProvider.GetRequiredService<IPricingService>().QuoteAsync(ReferenceRequest(b), Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var scope = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Agent));
        var pricing = scope.ServiceProvider.GetRequiredService<IPricingService>();
        var queries = scope.ServiceProvider.GetRequiredService<IPricingQueries>();
        (await pricing.QuoteAsync(ReferenceRequest(a), Ct)).Value.TotalInclVat.ShouldBe(Money.Tnd(681.4m));
        (await pricing.QuoteAsync(ReferenceRequest(b), Ct)).Error.ShouldBe(PricingErrors.NoPublishedPlan);
        (await pricing.QuoteAsync(ReferenceRequest(a) with { CategoryId = b.Ecmr }, Ct)).Error.ShouldBe(PricingErrors.CategoryNotFound);
        (await queries.GetExtrasAsync(b.AgencyId, SalesChannel.Web, Ct)).ShouldBeEmpty();
        (await queries.GetExtrasAsync(a.AgencyId, SalesChannel.Web, Ct)).ShouldNotBeEmpty();
        (await queries.GetPublishedPlanAsync(b.AgencyId, cancellationToken: Ct)).ShouldBeNull();
        (await queries.GetPublishedPlanAsync(a.AgencyId, cancellationToken: Ct)).ShouldNotBeNull().AgencyId.ShouldBe(a.AgencyId);

        await using var visitor = host.CreateScope(TestActors.Visitor(a.AgencyId));
        (await visitor.ServiceProvider.GetRequiredService<IPricingService>().QuoteAsync(ReferenceRequest(b), Ct)).Error
            .ShouldBe(PricingErrors.NoPublishedPlan, "a visitor of the public site of A never gets the prices of B");

        await using var manager = host.CreateScope(TestActors.Staff(a.AgencyId, Roles.Manager));
        var simulation = await manager.ServiceProvider.GetRequiredService<RatePlanAdministrationService>()
            .SimulateAsync(new SimulationRequest(ReferenceRequest(b)), Ct);
        simulation.Value.Published.Error.ShouldBe(PricingErrors.NoPublishedPlan);
    }
}
