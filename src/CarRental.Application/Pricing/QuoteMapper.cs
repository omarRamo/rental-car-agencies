using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Pricing;

namespace CarRental.Application.Pricing;

/// <summary>Maps the engine output to the public contract (contract enums share the values of the domain enums).</summary>
internal static class QuoteMapper
{
    public static PriceQuote ToQuote(PriceCalculation calculation, PriceRequest request, string categoryCode, RatePlan defaultPlan)
    {
        var parameters = calculation.Parameters;
        var inputs = new PriceQuoteInputs(
            request.AgencyId,
            request.CategoryId,
            categoryCode,
            request.PickupLocationId,
            request.ReturnLocationId,
            calculation.Request.RentalPeriod.Start,
            calculation.Request.RentalPeriod.End,
            request.Channel,
            [.. (calculation.Request.Extras ?? []).Select(e => new SelectedExtra(Extra.NormalizeCode(e.Code), e.Quantity))],
            calculation.DriverAges,
            string.IsNullOrWhiteSpace(request.PromoCode) ? null : Promotion.NormalizeCode(request.PromoCode),
            calculation.Request.GuaranteedModel?.VehicleId,
            calculation.Request.GuaranteedModel?.DailySupplement,
            calculation.Request.QuotedAt,
            (ChannelPricingModeInfo)calculation.ChannelPricing.Mode,
            calculation.ChannelPricing.MarkupPercent,
            defaultPlan.Id,
            defaultPlan.Version,
            parameters.VatRate,
            parameters.DailyContributionExclVat,
            parameters.LateReturnTolerance,
            parameters.RoundDailyPricesToDinar,
            parameters.YoungDriverAgeBelow,
            parameters.OpensAt,
            parameters.ClosesAt,
            calculation.Deposits.Deposit,
            calculation.Deposits.ReducedDeposit);

        return new PriceQuote(
            calculation.RatePlanId,
            calculation.RatePlanVersion,
            calculation.Days,
            [.. calculation.Lines.Select(ToLine)],
            calculation.TotalExclVat,
            calculation.TotalVat,
            calculation.TotalInclVat,
            calculation.RentalSubtotal,
            calculation.Deposit,
            calculation.IncludesFranchiseWaiver,
            [.. calculation.AppliedPromotions.Select(p => new AppliedPromotionInfo(p.PromotionId, (PromotionKindInfo)p.Kind, p.Code, p.Name, p.Amount, p.IsCombinable))],
            (PromoCodeOutcome)calculation.PromoCodeStatus,
            [.. calculation.DailyRates.Select(r => new DailyRateInfo(r.Date, (RatePeriodKindInfo)r.Period.Kind, r.Period.EventId, r.GridPrice, r.Price))],
            inputs);
    }

    private static PriceQuoteLine ToLine(PriceLine line) => new(
        (QuoteLineKind)line.Kind,
        line.Code,
        line.LabelKey,
        [.. line.LabelArguments],
        line.Quantity,
        line.UnitPrice,
        line.ExclVat,
        line.Vat,
        line.InclVat,
        line.VatRate);
}
