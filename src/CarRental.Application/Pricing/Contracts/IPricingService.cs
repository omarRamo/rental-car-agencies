using CarRental.Domain.Common;

namespace CarRental.Application.Pricing.Contracts;

/// <summary>
/// Prices a rental with the published rate plan of the agency: the only way for other modules (Bookings, public site,
/// WhatsApp bot, channels) to obtain a price — no price is computed anywhere else (PROMPT §16).
/// </summary>
/// <remarks>
/// The plan is chosen by channel: the published default plan of the agency, whose channel rule for
/// <see cref="PriceRequest.Channel"/> is parity (default), a markup, or the channel's own published plan. The booking
/// instant is the current time of the <see cref="TimeProvider"/>. Business failures are returned as errors with
/// <c>pricing.*</c> codes (see <see cref="PricingErrorCodes"/>); no permission is required (public funnel).
/// </remarks>
public interface IPricingService
{
    Task<Result<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A rental to price. Instants are absolute (stored in UTC; calendar rules use Africa/Tunis). <see cref="Extras"/> are
/// the selectable extras chosen by code (automatic extras are added by the engine); <see cref="DriverBirthDates"/>
/// trigger the young driver extra; <see cref="GuaranteedVehicleId"/> books an exact vehicle ("modèle garanti").
/// </summary>
public sealed record PriceRequest(
    Guid AgencyId,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    SalesChannel Channel,
    IReadOnlyList<SelectedExtra>? Extras = null,
    IReadOnlyList<DateOnly>? DriverBirthDates = null,
    string? PromoCode = null,
    Guid? GuaranteedVehicleId = null);

/// <summary>A selectable extra (see <see cref="IPricingQueries.GetExtrasAsync"/>) and its quantity.</summary>
public sealed record SelectedExtra(string Code, int Quantity);
