using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Quotes;

/// <summary>
/// How a new quote version is derived from a frozen one without pricing the whole rental again (ADR-0031): a manual
/// discount, and the extension rule of SPEC §6 ("valorise les jours ajoutés au tarif du moment"). Pure functions.
/// </summary>
public static class QuoteRevisions
{
    /// <summary>
    /// The frozen lines plus a negative <see cref="QuoteLineKind.ManualDiscount"/> line of <paramref name="discount"/>
    /// (VAT included, split with <paramref name="vatRate"/>); the total can never become negative.
    /// </summary>
    public static Result<QuoteContent> WithManualDiscount(QuoteContent content, Money discount, decimal vatRate)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!discount.IsPositive || discount.Currency != Currency.Tnd || Money.FromExact(discount.Amount, Currency.Tnd).IsFailure
            || discount > content.TotalInclVat || vatRate is < 0m or > 1m)
        {
            return BookingErrors.DiscountInvalid;
        }

        var inclVat = discount.Negate();
        var exclVat = Money.Tnd(inclVat.Amount / (1m + vatRate));
        var line = new QuoteLineData(
            QuoteLineKind.ManualDiscount,
            BookingQuoteLineCodes.ManualDiscount,
            BookingQuoteLabels.ManualDiscount,
            [],
            1,
            inclVat,
            exclVat,
            inclVat - exclVat,
            inclVat,
            vatRate);
        return content.WithLines([.. content.Lines, line]);
    }

    /// <summary>
    /// Extension of a rental (SPEC §6): the frozen lines are kept, and the added days are valued at the current plan as
    /// <c>quote(new full period, now) − quote(original period, now)</c>, split into an
    /// <see cref="QuoteLineKind.Extension"/> line and a <see cref="QuoteLineKind.DailyContribution"/> line for the added
    /// days (kept apart for accounting). The new content takes the period, days, plan and inputs of
    /// <paramref name="newPeriodNow"/>.
    /// </summary>
    /// <param name="frozen">The current version of the file.</param>
    /// <param name="newPeriodNow">The whole rental with the new return, priced now with the same inputs.</param>
    /// <param name="originalPeriodNow">The whole rental with the current return, priced now with the same inputs.</param>
    public static Result<QuoteContent> Extension(QuoteContent frozen, QuoteContent newPeriodNow, QuoteContent originalPeriodNow)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(newPeriodNow);
        ArgumentNullException.ThrowIfNull(originalPeriodNow);
        if (newPeriodNow.ReturnAt <= frozen.ReturnAt
            || newPeriodNow.PickupAt != frozen.PickupAt
            || originalPeriodNow.PickupAt != frozen.PickupAt
            || originalPeriodNow.ReturnAt != frozen.ReturnAt
            || newPeriodNow.CategoryId != frozen.CategoryId
            || originalPeriodNow.CategoryId != frozen.CategoryId)
        {
            return BookingErrors.AmendmentExtensionOnly;
        }

        var addedDays = Math.Max(0, newPeriodNow.Days - originalPeriodNow.Days);
        var (newContributionExclVat, newContributionVat) = ContributionOf(newPeriodNow);
        var (originalContributionExclVat, originalContributionVat) = ContributionOf(originalPeriodNow);
        var contributionExclVat = newContributionExclVat - originalContributionExclVat;
        var contributionVat = newContributionVat - originalContributionVat;
        var contributionInclVat = contributionExclVat + contributionVat;

        var rentalInclVat = newPeriodNow.TotalInclVat - originalPeriodNow.TotalInclVat - contributionInclVat;
        var rentalExclVat = newPeriodNow.TotalExclVat - originalPeriodNow.TotalExclVat - contributionExclVat;
        var vatRate = RateOf(newPeriodNow);
        var days = addedDays.ToString(CultureInfo.InvariantCulture);

        var lines = new List<QuoteLineData>(frozen.Lines);
        if (!rentalInclVat.IsZero || !rentalExclVat.IsZero)
        {
            lines.Add(new QuoteLineData(
                QuoteLineKind.Extension,
                BookingQuoteLineCodes.Extension,
                BookingQuoteLabels.Extension,
                [days],
                1,
                rentalInclVat,
                rentalExclVat,
                rentalInclVat - rentalExclVat,
                rentalInclVat,
                vatRate));
        }

        if (!contributionInclVat.IsZero)
        {
            var source = newPeriodNow.Lines.FirstOrDefault(line => line.Kind == QuoteLineKind.DailyContribution);
            var perDayExclVat = source is { LabelArguments.Count: > 0 } ? source.LabelArguments[0] : string.Empty;
            lines.Add(new QuoteLineData(
                QuoteLineKind.DailyContribution,
                BookingQuoteLineCodes.DailyContribution,
                BookingQuoteLabels.ExtensionContribution,
                [days, perDayExclVat],
                addedDays,
                source?.UnitPrice ?? contributionInclVat,
                contributionExclVat,
                contributionVat,
                contributionInclVat,
                source?.VatRate ?? vatRate));
        }

        var extended = frozen.WithLines(lines) with
        {
            ReturnAt = newPeriodNow.ReturnAt,
            ReturnLocationId = newPeriodNow.ReturnLocationId,
            Days = newPeriodNow.Days,
            RatePlanId = newPeriodNow.RatePlanId,
            RatePlanVersion = newPeriodNow.RatePlanVersion,
            RentalSubtotal = frozen.RentalSubtotal + (newPeriodNow.RentalSubtotal - originalPeriodNow.RentalSubtotal),
            Deposit = newPeriodNow.Deposit,
            InputsJson = newPeriodNow.InputsJson,
            DailyRatesJson = newPeriodNow.DailyRatesJson,
        };
        return extended.TotalInclVat.IsNegative ? BookingErrors.NegativeTotal : extended;
    }

    private static (Money ExclVat, Money Vat) ContributionOf(QuoteContent content)
    {
        var lines = content.Lines.Where(line => line.Kind == QuoteLineKind.DailyContribution).ToList();
        return (lines.Select(line => line.ExclVat).Sum(), lines.Select(line => line.Vat).Sum());
    }

    /// <summary>The VAT rate of the rental lines (one rate for the whole rental).</summary>
    private static decimal RateOf(QuoteContent content) =>
        content.Lines.FirstOrDefault(line => line.Kind == QuoteLineKind.Rental)?.VatRate
        ?? (content.Lines.Count > 0 ? content.Lines[0].VatRate : (decimal?)null)
        ?? 0m;
}
