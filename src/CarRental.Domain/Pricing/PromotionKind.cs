namespace CarRental.Domain.Pricing;

/// <summary>Trigger of a promotion.</summary>
public enum PromotionKind
{
    /// <summary>Applies when the request carries the promotion code.</summary>
    Code = 1,

    /// <summary>Applies when the pick-up date is at least <see cref="Promotion.MinimumDays"/> days after the booking date.</summary>
    EarlyBooking = 2,

    /// <summary>Applies when the rental lasts at least <see cref="Promotion.MinimumDays"/> days.</summary>
    LongStay = 3,
}

/// <summary>How a promotion reduces the rental subtotal (after the duration tier).</summary>
public enum PromotionDiscountType
{
    Percentage = 1,
    FixedAmount = 2,
}

/// <summary>How a sales channel is priced (rules held by the default plan of an agency).</summary>
public enum ChannelPricingMode
{
    /// <summary>Same daily prices as the default plan.</summary>
    Parity = 1,

    /// <summary>Daily prices of the default plan increased by a percentage (e.g. Booking.com commission).</summary>
    Markup = 2,

    /// <summary>The channel has its own published plan (own grid).</summary>
    OwnGrid = 3,
}

/// <summary>Outcome of the promotion code of a request.</summary>
public enum PromoCodeStatus
{
    /// <summary>The request carries no code.</summary>
    None = 0,

    /// <summary>The code matched an active promotion that was applied.</summary>
    Applied = 1,

    /// <summary>No active promotion has this code.</summary>
    Unknown = 2,

    /// <summary>The promotion exists but its dates, categories or channels exclude the request.</summary>
    NotApplicable = 3,

    /// <summary>The promotion is valid but another non-combinable promotion gives a larger discount.</summary>
    NotRetained = 4,
}
