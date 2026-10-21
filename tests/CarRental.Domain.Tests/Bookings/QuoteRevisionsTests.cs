using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.BookingTestData;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>
/// New quote versions derived from a frozen one (ADR-0031): the manual discount, and the extension rule of SPEC §6
/// — previous lines kept, added days valued at the current plan as quote(new period, now) − quote(original, now).
/// </summary>
public sealed class QuoteRevisionsTests
{
    [Fact]
    public void A_manual_discount_is_a_negative_line_split_like_any_other()
    {
        var content = Content(days: 3, dailyPrice: 100m);

        var discounted = QuoteRevisions.WithManualDiscount(content, Money.Tnd(25m), 0.19m).Value;

        discounted.Lines.Count.ShouldBe(3);
        var line = discounted.Lines[^1];
        line.Kind.ShouldBe(QuoteLineKind.ManualDiscount);
        line.Code.ShouldBe(BookingQuoteLineCodes.ManualDiscount);
        line.LabelKey.ShouldBe(BookingQuoteLabels.ManualDiscount);
        line.InclVat.ShouldBe(Money.Tnd(-25m));
        line.ExclVat.ShouldBe(Money.Tnd(-21.008m));
        line.Vat.ShouldBe(Money.Tnd(-3.992m));
        discounted.TotalInclVat.ShouldBe(content.TotalInclVat - Money.Tnd(25m));
        discounted.TotalExclVat.ShouldBe(content.TotalExclVat - Money.Tnd(21.008m));
        discounted.Validate().IsSuccess.ShouldBeTrue();
        content.Lines.Count.ShouldBe(2, "the frozen content is not changed");
    }

    [Fact]
    public void A_manual_discount_is_positive_in_millimes_and_never_beyond_the_total()
    {
        var content = Content();

        QuoteRevisions.WithManualDiscount(content, Money.Zero, 0.19m).Error.ShouldBe(BookingErrors.DiscountInvalid);
        QuoteRevisions.WithManualDiscount(content, Money.Tnd(-5m), 0.19m).Error.ShouldBe(BookingErrors.DiscountInvalid);
        QuoteRevisions.WithManualDiscount(content, content.TotalInclVat + Money.Tnd(0.001m), 0.19m).Error.ShouldBe(BookingErrors.DiscountInvalid);
        QuoteRevisions.WithManualDiscount(content, Money.Of(5m, Currency.Eur), 0.19m).Error.ShouldBe(BookingErrors.DiscountInvalid);
        QuoteRevisions.WithManualDiscount(content, content.TotalInclVat, 0.19m).Value.TotalInclVat.ShouldBe(Money.Zero);
    }

    [Fact]
    public void An_extension_keeps_the_frozen_lines_and_values_the_added_days_at_the_current_plan()
    {
        // Booked at 100.000 per day for 3 days; the plan now says 120.000 per day.
        var frozen = Content(days: 3, dailyPrice: 100m);
        var originalNow = Content(days: 3, dailyPrice: 120m);
        var newNow = Content(days: 5, dailyPrice: 120m);

        var extended = QuoteRevisions.Extension(frozen, newNow, originalNow).Value;

        extended.Lines.Take(2).ShouldBe(frozen.Lines, "frozen lines are kept, at their price");
        var extension = extended.Lines[2];
        extension.Kind.ShouldBe(QuoteLineKind.Extension);
        extension.LabelKey.ShouldBe(BookingQuoteLabels.Extension);
        extension.LabelArguments.ShouldBe(["2"]);
        extension.InclVat.ShouldBe(Money.Tnd(240m), "2 added days at 120.000");
        var contribution = extended.Lines[3];
        contribution.Kind.ShouldBe(QuoteLineKind.DailyContribution);
        contribution.LabelKey.ShouldBe(BookingQuoteLabels.ExtensionContribution);
        contribution.LabelArguments.ShouldBe(["2", "2.000"]);
        contribution.Quantity.ShouldBe(2);
        contribution.ExclVat.ShouldBe(Money.Tnd(4m));
        contribution.InclVat.ShouldBe(Money.Tnd(4.760m));

        extended.TotalInclVat.ShouldBe(frozen.TotalInclVat + (newNow.TotalInclVat - originalNow.TotalInclVat));
        extended.TotalExclVat.ShouldBe(frozen.TotalExclVat + (newNow.TotalExclVat - originalNow.TotalExclVat));
        extended.ReturnAt.ShouldBe(newNow.ReturnAt);
        extended.Days.ShouldBe(5);
        extended.InputsJson.ShouldBe(newNow.InputsJson);
        extended.RatePlanId.ShouldBe(newNow.RatePlanId);
        extended.Validate().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_extension_within_the_late_tolerance_adds_no_day()
    {
        var frozen = Content(days: 3);

        var extended = QuoteRevisions.Extension(frozen, Content(days: 3, returnAt: frozen.ReturnAt.AddMinutes(30)), Content(days: 3)).Value;

        extended.Lines.ShouldBe(frozen.Lines);
        extended.TotalInclVat.ShouldBe(frozen.TotalInclVat);
        extended.ReturnAt.ShouldBe(frozen.ReturnAt.AddMinutes(30));
    }

    [Fact]
    public void An_extension_keeps_a_previous_manual_discount()
    {
        var frozen = QuoteRevisions.WithManualDiscount(Content(days: 3), Money.Tnd(20m), 0.19m).Value;

        var extended = QuoteRevisions.Extension(frozen, Content(days: 4), Content(days: 3)).Value;

        extended.Lines.Count(l => l.Kind == QuoteLineKind.ManualDiscount).ShouldBe(1);
        extended.TotalInclVat.ShouldBe(frozen.TotalInclVat + Money.Tnd(100m) + Money.Tnd(2.380m));
    }

    [Fact]
    public void Only_a_later_return_of_the_same_rental_is_an_extension()
    {
        var frozen = Content(days: 3);

        QuoteRevisions.Extension(frozen, Content(days: 3), Content(days: 3)).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
        QuoteRevisions.Extension(frozen, Content(days: 2), Content(days: 3)).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
        QuoteRevisions.Extension(frozen, Content(days: 4) with { CategoryId = OtherCategoryId }, Content(days: 3)).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
        QuoteRevisions.Extension(frozen, Content(days: 4), Content(days: 2)).Error.ShouldBe(BookingErrors.AmendmentExtensionOnly);
    }
}
