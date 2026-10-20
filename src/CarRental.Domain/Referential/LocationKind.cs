namespace CarRental.Domain.Referential;

public enum LocationKind
{
    /// <summary>The agency office (counter).</summary>
    AgencyOffice = 1,

    /// <summary>An airport desk or meeting point (TUN, NBE, MIR, DJE…).</summary>
    Airport = 2,

    /// <summary>A delivery point (hotel, address), possibly with a delivery fee.</summary>
    DeliveryPoint = 3,
}
