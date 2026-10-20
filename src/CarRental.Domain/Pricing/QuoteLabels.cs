namespace CarRental.Domain.Pricing;

/// <summary>
/// Resource keys of the quote line labels (the web host translates them, French neutral and English). Arguments are
/// invariant strings, listed for each key. Keys are stored with the quotes: never rename a published key.
/// </summary>
public static class QuoteLabels
{
    public const string RentalBase = "Quote.Line.Rental.Base";
    public const string RentalLowSeason = "Quote.Line.Rental.LowSeason";
    public const string RentalMidSeason = "Quote.Line.Rental.MidSeason";
    public const string RentalHighSeason = "Quote.Line.Rental.HighSeason";

    /// <summary>{0} event name.</summary>
    public const string RentalEvent = "Quote.Line.Rental.Event";

    /// <summary>{0} first day of the tier, {1} last day, {2} discount percentage.</summary>
    public const string DurationDiscount = "Quote.Line.DurationDiscount";

    /// <summary>{0} first day of the open-ended last tier, {1} discount percentage.</summary>
    public const string DurationDiscountOpen = "Quote.Line.DurationDiscount.Open";

    /// <summary>{0} code, {1} percentage.</summary>
    public const string PromotionCodePercent = "Quote.Line.Promotion.Code.Percent";

    /// <summary>{0} code.</summary>
    public const string PromotionCodeAmount = "Quote.Line.Promotion.Code.Amount";

    /// <summary>{0} minimum days before pick-up, {1} percentage.</summary>
    public const string PromotionEarlyBookingPercent = "Quote.Line.Promotion.EarlyBooking.Percent";

    /// <summary>{0} minimum days before pick-up.</summary>
    public const string PromotionEarlyBookingAmount = "Quote.Line.Promotion.EarlyBooking.Amount";

    /// <summary>{0} minimum rental days, {1} percentage.</summary>
    public const string PromotionLongStayPercent = "Quote.Line.Promotion.LongStay.Percent";

    /// <summary>{0} minimum rental days.</summary>
    public const string PromotionLongStayAmount = "Quote.Line.Promotion.LongStay.Amount";

    /// <summary>{0} French name, {1} English name.</summary>
    public const string Extra = "Quote.Line.Extra";

    /// <summary>{0} French name, {1} English name, {2} cap per unit (amount).</summary>
    public const string ExtraCapped = "Quote.Line.Extra.Capped";

    /// <summary>{0} vehicle (make, model, plate).</summary>
    public const string GuaranteedModel = "Quote.Line.GuaranteedModel";

    /// <summary>{0} contribution per day excluding VAT (amount).</summary>
    public const string DailyContribution = "Quote.Line.DailyContribution";

    public static IReadOnlyList<string> All { get; } =
    [
        RentalBase, RentalLowSeason, RentalMidSeason, RentalHighSeason, RentalEvent, DurationDiscount, DurationDiscountOpen,
        PromotionCodePercent, PromotionCodeAmount, PromotionEarlyBookingPercent, PromotionEarlyBookingAmount,
        PromotionLongStayPercent, PromotionLongStayAmount, Extra, ExtraCapped, GuaranteedModel, DailyContribution,
    ];
}

/// <summary>Codes of the quote lines that are not extras (an extra line carries the code of the extra).</summary>
public static class QuoteLineCodes
{
    public const string Rental = "RENTAL";
    public const string DurationDiscount = "DURATION_DISCOUNT";
    public const string Promotion = "PROMOTION";
    public const string GuaranteedModel = "GUARANTEED_MODEL";
    public const string DailyContribution = "DAILY_CONTRIBUTION";
}
