using CarRental.Application.Bookings.Parties;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Quotes;

/// <summary>What a rental is priced with: the terms of a file (or of its amendment) and its drivers.</summary>
internal sealed record RentalTerms(
    Guid AgencyId,
    SalesChannel Channel,
    Guid CategoryId,
    Guid PickupLocationId,
    Guid ReturnLocationId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    IReadOnlyList<ExtraChoice> Extras,
    string? PromoCode,
    Guid? GuaranteedVehicleId,
    BookingParties Parties)
{
    public static RentalTerms Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return Of(booking, booking.CurrentChanges);
    }

    public static RentalTerms Of(Booking booking, BookingChanges changes)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(changes);
        return new RentalTerms(
            booking.AgencyId,
            booking.Channel,
            changes.CategoryId,
            changes.PickupLocationId,
            changes.ReturnLocationId,
            changes.PickupAt,
            changes.ReturnAt,
            changes.Extras,
            changes.PromoCode,
            changes.GuaranteedVehicleId,
            changes.Parties);
    }
}

/// <summary>Prices rental terms with the pricing engine (the only source of prices, PROMPT §16).</summary>
internal sealed class BookingPricing(IPricingService pricing, PartyDirectory parties)
{
    public async Task<Result<PriceQuote>> QuoteAsync(RentalTerms terms, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var birthDates = await parties.BirthDatesAsync(terms.Parties, cancellationToken);
        return await pricing.QuoteAsync(
            new PriceRequest(
                terms.AgencyId,
                terms.CategoryId,
                terms.PickupLocationId,
                terms.ReturnLocationId,
                terms.PickupAt,
                terms.ReturnAt,
                terms.Channel,
                [.. terms.Extras.Select(extra => new SelectedExtra(extra.Code, extra.Quantity))],
                birthDates,
                terms.PromoCode,
                terms.GuaranteedVehicleId),
            cancellationToken);
    }

    /// <summary>The extras of a price request as booking choices (invalid entries are a validation error).</summary>
    public static Result<IReadOnlyList<ExtraChoice>> ChoicesOf(IReadOnlyList<SelectedExtra>? extras)
    {
        var choices = new List<ExtraChoice>();
        foreach (var extra in extras ?? [])
        {
            var choice = ExtraChoice.Create(extra?.Code, extra?.Quantity ?? 0);
            if (choice.IsFailure)
            {
                return choice.Error;
            }

            choices.Add(choice.Value);
        }

        return ExtraChoice.Normalize(choices);
    }
}
