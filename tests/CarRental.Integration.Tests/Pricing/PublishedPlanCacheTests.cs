using CarRental.Application.Pricing.Administration;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Pricing.PricingScenario;

namespace CarRental.Integration.Tests.Pricing;

/// <summary>Quotes read the published plans from memory (ADR-0044); publishing a new version prices the next quote at once.</summary>
public sealed class PublishedPlanCacheTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_newly_published_version_prices_the_next_quote_at_once()
    {
        var agency = await CreateAgencyAsync(host, Ct);
        await PublishReferencePlanAsync(host, agency, Ct);
        var first = await QuoteAsync(ReferenceRequest(agency));
        var again = await QuoteAsync(ReferenceRequest(agency));

        var draftId = await AdministerAsync(host, s => s.CreateDraftAsync(new CreateDraftRequest(agency.AgencyId, null, "Grille v2"), Ct));
        var draft = await AdministerAsync(host, s => s.UpdatePricesAsync(
            draftId,
            0,
            [
                new CategoryPriceDefinition(agency.Ecmr, RatePeriodKey.Base, Money.Tnd(100m)),
                new CategoryPriceDefinition(agency.Ecmr, RatePeriodKey.ForSeason(SeasonLevel.Mid), Money.Tnd(120m)),
                new CategoryPriceDefinition(agency.Ecmr, RatePeriodKey.ForSeason(SeasonLevel.High), Money.Tnd(140m)),
            ],
            Ct));
        await AdministerAsync(host, s => s.PublishAsync(draftId, draft.Plan.Revision, Ct));
        var second = await QuoteAsync(ReferenceRequest(agency));

        first.TotalInclVat.ShouldBe(Money.Tnd(681.4m));
        again.ShouldSatisfyAllConditions(q => q.RatePlanVersion.ShouldBe(1), q => q.TotalInclVat.ShouldBe(first.TotalInclVat));
        second.RatePlanVersion.ShouldBe(2);
        second.DailyRates.Select(r => r.Price.Amount).ShouldBe([120m, 120m, 120m, 140m, 140m]);
    }

    private async Task<PriceQuote> QuoteAsync(PriceRequest request)
    {
        await using var scope = host.CreateScope();
        var quote = await scope.ServiceProvider.GetRequiredService<IPricingService>().QuoteAsync(request, Ct);
        quote.IsSuccess.ShouldBeTrue(quote.IsFailure ? quote.Error.ToString() : null);
        return quote.Value;
    }
}
