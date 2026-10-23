using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>
/// The daily snapshot of one vehicle on one Tunis calendar day (ADR-0045): whether it could be rented, whether a rental
/// covered it, the rental revenue of the day (excluding VAT, the daily contribution and the deposit), the commission, the
/// direct costs and the depreciation of the day. One row per vehicle and day while the vehicle is in the fleet; the rows of
/// a day are derived data, replaced as a whole when the day is computed again (never edited).
/// </summary>
/// <remarks>
/// <para>A vehicle-day can carry several rentals (a rental billed one more day after a late return, and the next rental
/// starting the same day): <see cref="Rentals"/> keeps each share for an exact attribution to the sales channels, and the
/// row names the main one (largest share) in <see cref="BookingId"/> and <see cref="Channel"/>.</para>
/// <para>Plate, category code and booking number are copied so that the table reads on its own (exports, future SQL
/// views for Power BI) without joining the tables of other modules.</para>
/// </remarks>
public sealed class VehicleDaySnapshot : Entity, IAgencyOwned
{
    /// <summary>Same limit as the plate of a vehicle (Fleet module).</summary>
    public const int PlateNumberMaxLength = 20;

    /// <summary>Make and model of a vehicle (Fleet module: 50 and 60 characters, a space between).</summary>
    public const int VehicleNameMaxLength = 111;

    public const int CategoryCodeLength = 4;

    /// <summary>Same limit as the number of a rental file (<c>LOC-YYYY-NNNNNN</c>, Bookings module).</summary>
    public const int BookingNumberMaxLength = 24;

    private readonly List<VehicleDayRental> _rentals = [];

    private VehicleDaySnapshot()
    {
    }

    private VehicleDaySnapshot(Guid id, Guid agencyId, FleetVehicleDay fleetDay, DateTimeOffset computedAt)
        : base(id)
    {
        AgencyId = agencyId;
        Day = fleetDay.Day;
        VehicleId = fleetDay.VehicleId;
        PlateNumber = fleetDay.PlateNumber;
        VehicleName = fleetDay.VehicleName.Length > VehicleNameMaxLength ? fleetDay.VehicleName[..VehicleNameMaxLength] : fleetDay.VehicleName;
        CategoryId = fleetDay.CategoryId;
        CategoryCode = fleetDay.CategoryCode;
        Depreciation = fleetDay.Depreciation;
        ComputedAtUtc = computedAt.ToUniversalTime();
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Tunis calendar date.</summary>
    public DateOnly Day { get; private set; }

    public Guid VehicleId { get; private set; }

    public string PlateNumber { get; private set; } = null!;

    /// <summary>Make and model, e.g. <c>Peugeot 208</c>.</summary>
    public string VehicleName { get; private set; } = null!;

    /// <summary>Category of the vehicle that day.</summary>
    public Guid CategoryId { get; private set; }

    /// <summary>ACRISS code of the category that day.</summary>
    public string CategoryCode { get; private set; } = null!;

    /// <summary>
    /// The vehicle counts in the available fleet that day: rentable for at least part of the day according to the fleet
    /// windows (in service, not sold, documents valid, no maintenance or out-of-service status all day), or rented.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>A rented day of a rental that counts as revenue was attributed to this vehicle that day.</summary>
    public bool IsRented { get; private set; }

    /// <summary>The main rental of the day (largest share), when rented.</summary>
    public Guid? BookingId { get; private set; }

    public string? BookingNumber { get; private set; }

    /// <summary>Sales channel of the main rental of the day.</summary>
    public SalesChannel? Channel { get; private set; }

    /// <summary>Rental revenue of the day, excluding VAT: shares of the current quotes, daily contribution and deposit excluded.</summary>
    public Money RentalRevenue { get; private set; }

    /// <summary>Commission owed to the sales channels on the revenue of the day, excluding VAT.</summary>
    public Money Commission { get; private set; }

    /// <summary>Direct costs of the vehicle attributed to the day (costs recorded on the vehicle, spread over their period).</summary>
    public Money DirectCosts { get; private set; }

    /// <summary>Straight-line depreciation of the day (Fleet economics).</summary>
    public Money Depreciation { get; private set; }

    public DateTimeOffset ComputedAtUtc { get; private set; }

    /// <summary>Every rental sharing the day, with its share (empty when the vehicle was not rented).</summary>
    public IReadOnlyList<VehicleDayRental> Rentals => _rentals;

    /// <summary>Revenue minus commission, direct costs and depreciation.</summary>
    public Money Margin => RentalRevenue - Commission - DirectCosts - Depreciation;

    /// <summary>
    /// Builds the row of a vehicle-day from the fleet facts, the rental shares and the direct costs of the day. The inputs
    /// come from the contracts of the other modules: an inconsistent input is a bug (exception), not a business error.
    /// </summary>
    internal static VehicleDaySnapshot Create(
        Guid agencyId,
        FleetVehicleDay fleetDay,
        IReadOnlyList<RentalShare> shares,
        Money directCosts,
        DateTimeOffset computedAt)
    {
        ArgumentNullException.ThrowIfNull(fleetDay);
        ArgumentNullException.ThrowIfNull(shares);
        if (agencyId == Guid.Empty || fleetDay.VehicleId == Guid.Empty || fleetDay.CategoryId == Guid.Empty)
        {
            throw new ArgumentException("A vehicle-day needs an agency, a vehicle and a category.", nameof(fleetDay));
        }

        if (string.IsNullOrWhiteSpace(fleetDay.PlateNumber) || fleetDay.PlateNumber.Length > PlateNumberMaxLength
            || fleetDay.VehicleName is null || fleetDay.CategoryCode is not { Length: CategoryCodeLength })
        {
            throw new ArgumentException("The plate or the category code of a vehicle-day is invalid.", nameof(fleetDay));
        }

        if (fleetDay.Depreciation.IsNegative || directCosts.IsNegative)
        {
            throw new ArgumentException("Depreciation and direct costs are never negative.", nameof(directCosts));
        }

        var snapshot = new VehicleDaySnapshot(Entity.NewId(), agencyId, fleetDay, computedAt)
        {
            DirectCosts = directCosts,
            RentalRevenue = shares.Select(s => s.Revenue).Sum(),
            Commission = shares.Select(s => s.Commission).Sum(),
            IsRented = shares.Count > 0,
        };
        snapshot.IsAvailable = fleetDay.IsRentable || snapshot.IsRented;

        foreach (var share in shares)
        {
            snapshot._rentals.Add(new VehicleDayRental(snapshot, share));
        }

        if (shares
                .OrderByDescending(s => s.Revenue)
                .ThenByDescending(s => s.PickupAt)
                .ThenBy(s => s.BookingId)
                .FirstOrDefault() is { } main)
        {
            snapshot.BookingId = main.BookingId;
            snapshot.BookingNumber = main.BookingNumber;
            snapshot.Channel = main.Channel;
        }

        return snapshot;
    }
}

/// <summary>
/// The share of one rental in a vehicle-day: its booking, channel, revenue and commission of the day (excluding VAT).
/// The shares of a day add up to the revenue and commission of the snapshot.
/// </summary>
public sealed class VehicleDayRental : Entity, IAgencyOwned
{
    private VehicleDayRental()
    {
    }

    internal VehicleDayRental(VehicleDaySnapshot snapshot, RentalShare share)
        : base(NewId())
    {
        if (share.BookingId == Guid.Empty || string.IsNullOrWhiteSpace(share.BookingNumber)
            || share.BookingNumber.Length > VehicleDaySnapshot.BookingNumberMaxLength || !Enum.IsDefined(share.Channel))
        {
            throw new ArgumentException("A rental share needs a booking, its number and a known channel.", nameof(share));
        }

        SnapshotId = snapshot.Id;
        AgencyId = snapshot.AgencyId;
        Day = snapshot.Day;
        VehicleId = snapshot.VehicleId;
        BookingId = share.BookingId;
        BookingNumber = share.BookingNumber;
        Channel = share.Channel;
        Revenue = share.Revenue;
        Commission = share.Commission;
    }

    public Guid SnapshotId { get; private set; }

    public Guid AgencyId { get; private set; }

    public DateOnly Day { get; private set; }

    public Guid VehicleId { get; private set; }

    public Guid BookingId { get; private set; }

    public string BookingNumber { get; private set; } = null!;

    public SalesChannel Channel { get; private set; }

    public Money Revenue { get; private set; }

    public Money Commission { get; private set; }
}
