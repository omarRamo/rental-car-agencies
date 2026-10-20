using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Application.Pricing;

/// <summary>
/// Feeds the pricing engine (ADR-0024): checks the request, selects the plans by channel, loads the category deposits and
/// the guaranteed vehicle (Fleet), the locations (Referential) and the settings of the agency, runs
/// <see cref="PricingEngine"/> and maps the calculation to the public <see cref="PriceQuote"/>.
/// </summary>
internal sealed class QuoteCalculator(
    RatePlanSelector selector,
    IFleetQueries fleet,
    IReferentialQueries referential,
    ISettingsReader settings)
{
    /// <param name="request">The rental to price.</param>
    /// <param name="quotedAt">Booking instant (now, or the date chosen in the simulator).</param>
    /// <param name="replacement">A plan simulated in place of the published plan of its agency and channel.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<PriceQuote>> QuoteAsync(
        PriceRequest request,
        DateTimeOffset quotedAt,
        RatePlan? replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AgencyId == Guid.Empty || !Enum.IsDefined(request.Channel))
        {
            return PricingErrors.NoPublishedPlan;
        }

        var period = Period.Create(request.PickupAt, request.ReturnAt);
        if (period.IsFailure)
        {
            return PricingErrors.PeriodInvalid;
        }

        var plans = await selector.SelectAsync(request.AgencyId, request.Channel, replacement, cancellationToken);
        if (plans.IsFailure)
        {
            return plans.Error;
        }

        var category = request.CategoryId == Guid.Empty ? null : await fleet.GetCategoryAsync(request.CategoryId, cancellationToken);
        if (category is not { IsActive: true })
        {
            return PricingErrors.CategoryNotFound;
        }

        var locations = await referential.GetLocationsAsync(request.AgencyId, cancellationToken);
        if (!locations.Any(l => l.Id == request.PickupLocationId) || !locations.Any(l => l.Id == request.ReturnLocationId))
        {
            return PricingErrors.LocationNotFound;
        }

        var guaranteed = await GuaranteedModelAsync(request, cancellationToken);
        if (guaranteed.IsFailure)
        {
            return guaranteed.Error;
        }

        var parameters = await ParametersAsync(request.AgencyId, cancellationToken);
        var pricingRequest = new PricingRequest(
            request.CategoryId,
            period.Value,
            request.PickupLocationId,
            request.ReturnLocationId,
            request.Channel,
            quotedAt.ToUniversalTime(),
            [.. (request.Extras ?? []).Select(e => new ExtraSelection(e.Code, e.Quantity))],
            request.DriverBirthDates ?? [],
            request.PromoCode,
            guaranteed.Value);
        var deposits = new DepositTerms(category.Deposit, category.ReducedDeposit);

        var calculation = PricingEngine.Calculate(plans.Value.Grid, plans.Value.ChannelPricing, pricingRequest, parameters, deposits);
        return calculation.IsFailure
            ? calculation.Error
            : QuoteMapper.ToQuote(calculation.Value, request, category.AcrissCode, plans.Value.DefaultPlan);
    }

    private async Task<Result<GuaranteedModelSelection?>> GuaranteedModelAsync(PriceRequest request, CancellationToken cancellationToken)
    {
        if (request.GuaranteedVehicleId is not { } vehicleId)
        {
            return Result.Success<GuaranteedModelSelection?>(null);
        }

        var vehicle = await fleet.GetVehicleAsync(vehicleId, cancellationToken);
        if (vehicle is null || vehicle.Status == VehicleStatusInfo.Sold)
        {
            return PricingErrors.VehicleNotFound;
        }

        if (vehicle.CategoryId != request.CategoryId)
        {
            return PricingErrors.GuaranteedVehicleCategoryMismatch;
        }

        if (vehicle.GuaranteedModelDailySupplement is not { IsPositive: true } supplement)
        {
            return PricingErrors.GuaranteedModelNotOffered;
        }

        return new GuaranteedModelSelection(vehicle.Id, $"{vehicle.Make} {vehicle.Model} ({vehicle.PlateNumber})", supplement);
    }

    private async Task<PricingParameters> ParametersAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var vatRate = await settings.GetAsync(WellKnownSettings.VatRate, agencyId, cancellationToken);
        var contribution = await settings.GetAsync(WellKnownSettings.DailyContributionExclVat, agencyId, cancellationToken);
        var tolerance = await settings.GetAsync(WellKnownSettings.LateReturnTolerance, agencyId, cancellationToken);
        var roundToDinar = await settings.GetAsync(PricingSettings.RoundPublicPricesToDinar, agencyId, cancellationToken);
        var youngDriverAgeBelow = await settings.GetAsync(PricingSettings.YoungDriverAgeBelow, agencyId, cancellationToken);
        var openingHours = await settings.GetAsync(PricingSettings.AgencyOpeningHours, agencyId, cancellationToken);
        return new PricingParameters(
            vatRate,
            contribution,
            tolerance,
            roundToDinar,
            youngDriverAgeBelow,
            openingHours.OpensAt,
            openingHours.ClosesAt);
    }
}
