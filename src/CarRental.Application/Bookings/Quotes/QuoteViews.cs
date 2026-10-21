using CarRental.Application.Bookings.Administration;
using CarRental.Domain.Bookings.Quotes;

namespace CarRental.Application.Bookings.Quotes;

/// <summary>Quotes and quote contents as back office views.</summary>
internal static class QuoteViews
{
    public static QuoteView ToView(this Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return new QuoteView(
            quote.Id,
            quote.Code,
            quote.Version,
            quote.Origin,
            quote.CreatedAtUtc,
            quote.CreatedBy,
            quote.RatePlanId,
            quote.RatePlanVersion,
            quote.Days,
            quote.PickupAt,
            quote.ReturnAt,
            [.. quote.Lines.Select(line => new QuoteLineView(
                line.Position,
                line.Kind,
                line.Code,
                line.LabelKey,
                line.LabelArguments,
                line.Quantity,
                line.UnitPrice,
                line.ExclVat,
                line.Vat,
                line.InclVat,
                line.VatRate))],
            quote.TotalExclVat,
            quote.TotalVat,
            quote.TotalInclVat,
            quote.Deposit,
            quote.IncludesFranchiseWaiver,
            quote.PromoCodeOutcome,
            quote.PromoCode,
            QuoteSnapshots.ReadPromotions(quote.AppliedPromotionsJson),
            quote.ManualDiscountReason);
    }

    /// <summary>A content not saved yet (amendment preview).</summary>
    public static QuoteView ToView(this QuoteContent content, QuoteOrigin origin, string? manualDiscountReason = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new QuoteView(
            QuoteId: null,
            Code: null,
            Version: null,
            origin,
            CreatedAtUtc: null,
            CreatedBy: null,
            content.RatePlanId,
            content.RatePlanVersion,
            content.Days,
            content.PickupAt,
            content.ReturnAt,
            [.. content.Lines.Select((line, index) => new QuoteLineView(
                index + 1,
                line.Kind,
                line.Code,
                line.LabelKey,
                line.LabelArguments,
                line.Quantity,
                line.UnitPrice,
                line.ExclVat,
                line.Vat,
                line.InclVat,
                line.VatRate))],
            content.TotalExclVat,
            content.TotalVat,
            content.TotalInclVat,
            content.Deposit,
            content.IncludesFranchiseWaiver,
            content.PromoCodeOutcome,
            content.PromoCode,
            QuoteSnapshots.ReadPromotions(content.AppliedPromotionsJson),
            manualDiscountReason);
    }
}
