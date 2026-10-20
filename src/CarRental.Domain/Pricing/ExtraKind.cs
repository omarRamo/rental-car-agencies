namespace CarRental.Domain.Pricing;

/// <summary>What an extra means for the pricing engine (docs/conception/phase-1-exploitation.md §4, "Options").</summary>
public enum ExtraKind
{
    /// <summary>Chosen by the customer (additional driver, child seat, GPS, hotel delivery…), with a quantity.</summary>
    Optional = 1,

    /// <summary>Chosen by the customer (quantity 1); switches the deposit of the category to its reduced amount.</summary>
    FranchiseWaiver = 2,

    /// <summary>Automatic: one unit per driver younger than the young driver threshold at pick-up.</summary>
    YoungDriver = 3,

    /// <summary>Automatic: one unit when the pick-up location and one when the return location is <see cref="Extra.LocationId"/>.</summary>
    LocationDelivery = 4,

    /// <summary>Automatic: one unit when the pick-up and one when the return happens outside the agency opening hours.</summary>
    AfterHours = 5,

    /// <summary>Automatic: one unit when the return location differs from the pick-up location.</summary>
    OneWay = 6,
}

/// <summary>How an extra is charged.</summary>
public enum ExtraPricingMode
{
    /// <summary>Price × rental days × quantity, with an optional cap per unit for the whole rental.</summary>
    PerDay = 1,

    /// <summary>Flat price × quantity.</summary>
    PerRental = 2,
}

public static class ExtraKindExtensions
{
    /// <summary>Whether the customer (or the agent) chooses the extra; the other kinds are added automatically.</summary>
    public static bool IsSelectable(this ExtraKind kind) => kind is ExtraKind.Optional or ExtraKind.FranchiseWaiver;
}
