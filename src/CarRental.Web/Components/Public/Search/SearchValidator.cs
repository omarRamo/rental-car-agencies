using System.Globalization;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Web.Components.Public.Search;

/// <summary>A valid search: locations of the agency and the rental instants (UTC), plus the query it comes from.</summary>
public sealed record SearchCriteria(
    LocationInfo PickupLocation,
    LocationInfo ReturnLocation,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    SearchQuery Query)
{
    public Guid AgencyId => PickupLocation.AgencyId;

    public SalesChannel Channel => Query.Traffic.Channel;

    public Period Period => Period.Create(PickupAt, ReturnAt).Value;
}

/// <summary>A field of the search form in error, with the stable code of its message (<c>Search.Error.{code}</c>).</summary>
public sealed record SearchError(string Field, string Code);

/// <summary>Outcome of <see cref="SearchValidator.Validate"/>: the criteria, or the errors per field.</summary>
public sealed record SearchValidation(SearchCriteria? Criteria, IReadOnlyList<SearchError> Errors)
{
    public bool IsValid => Criteria is not null;

    public string? ErrorFor(string field) => Errors.FirstOrDefault(e => e.Field == field)?.Code;
}

/// <summary>
/// Validates a search against the locations of the agency and the clock. Dates are Tunis wall-clock times
/// (<c>yyyy-MM-ddTHH:mm</c>, seconds tolerated); the pick-up must be in the future and within <see cref="MaxDaysAhead"/>
/// days, the return after the pick-up and the rental at most <see cref="MaxRentalDays"/> days long (longer rentals are
/// quoted by the agency). Nothing in the URL is trusted beyond that: prices and availability are recomputed.
/// </summary>
public static class SearchValidator
{
    /// <summary>The availability snapshot covers 400 days; a search goes one year ahead at most.</summary>
    public const int MaxDaysAhead = 365;

    public const int MaxRentalDays = 90;

    private static readonly string[] DateFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];

    public static SearchValidation Validate(SearchQuery query, IReadOnlyList<LocationInfo> locations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(locations);
        var errors = new List<SearchError>();

        var pickupLocation = Find(locations, query.PickupLocation);
        if (query.PickupLocation is null)
        {
            errors.Add(new SearchError(SearchParameters.PickupLocation, "pickup_location_required"));
        }
        else if (pickupLocation is null)
        {
            errors.Add(new SearchError(SearchParameters.PickupLocation, "pickup_location_unknown"));
        }

        var returnLocation = query.ReturnLocation is null ? pickupLocation : Find(locations, query.ReturnLocation);
        if (query.ReturnLocation is not null && returnLocation is null)
        {
            errors.Add(new SearchError(SearchParameters.ReturnLocation, "return_location_unknown"));
        }

        var pickup = ParseInstant(query.Pickup);
        if (query.Pickup is null)
        {
            errors.Add(new SearchError(SearchParameters.Pickup, "pickup_required"));
        }
        else if (pickup is null)
        {
            errors.Add(new SearchError(SearchParameters.Pickup, "pickup_invalid"));
        }
        else if (pickup <= now)
        {
            errors.Add(new SearchError(SearchParameters.Pickup, "pickup_past"));
        }
        else if (pickup > now.AddDays(MaxDaysAhead))
        {
            errors.Add(new SearchError(SearchParameters.Pickup, "pickup_too_far"));
        }

        var returnAt = ParseInstant(query.Return);
        if (query.Return is null)
        {
            errors.Add(new SearchError(SearchParameters.Return, "return_required"));
        }
        else if (returnAt is null)
        {
            errors.Add(new SearchError(SearchParameters.Return, "return_invalid"));
        }
        else if (pickup is not null && returnAt <= pickup)
        {
            errors.Add(new SearchError(SearchParameters.Return, "return_before_pickup"));
        }
        else if (pickup is not null && returnAt - pickup > TimeSpan.FromDays(MaxRentalDays))
        {
            errors.Add(new SearchError(SearchParameters.Return, "rental_too_long"));
        }

        if (errors.Count > 0 || pickupLocation is null || returnLocation is null || pickup is null || returnAt is null)
        {
            return new SearchValidation(null, errors);
        }

        // Canonical query: location codes as stored, dates without seconds, same return location left implicit.
        var canonical = query with
        {
            PickupLocation = pickupLocation.Code,
            ReturnLocation = returnLocation.Id == pickupLocation.Id ? null : returnLocation.Code,
            Pickup = FormatLocal(pickup.Value),
            Return = FormatLocal(returnAt.Value),
        };
        return new SearchValidation(new SearchCriteria(pickupLocation, returnLocation, pickup.Value, returnAt.Value, canonical), []);
    }

    /// <summary>A Tunis wall-clock date and time of a deep link, as an instant; <see langword="null"/> when malformed.</summary>
    public static DateTimeOffset? ParseInstant(string? value) =>
        value is not null && DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? TunisTime.FromLocal(local)
            : null;

    /// <summary>The Tunis wall-clock form of an instant, as written in deep links and date inputs.</summary>
    public static string FormatLocal(DateTimeOffset instant) =>
        TunisTime.ToTunis(instant).ToString(SearchParameters.DateTimeFormat, CultureInfo.InvariantCulture);

    private static LocationInfo? Find(IReadOnlyList<LocationInfo> locations, string? code) =>
        code is null ? null : locations.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
}
