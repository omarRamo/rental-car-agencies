using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Pricing;

/// <summary><see cref="IPricingService"/>: prices with the published plans, the booking instant being now.</summary>
internal sealed class PricingService(QuoteCalculator calculator, TimeProvider timeProvider) : IPricingService
{
    public Task<Result<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return calculator.QuoteAsync(request, timeProvider.GetUtcNow(), replacement: null, cancellationToken);
    }
}
