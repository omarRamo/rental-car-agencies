using CarRental.Domain.Common;
using CarRental.Domain.Pricing;
using static CarRental.Domain.Tests.Pricing.PricingTestData;

namespace CarRental.Domain.Tests.Pricing;

/// <summary>Promotions: code, early booking, long stay; best non-combinable plus every combinable one; never below zero.</summary>
public sealed class PricingEnginePromotionTests
{
    private static readonly PromotionDefinition Welcome = new(
        "Bienvenue 10 %",
        PromotionKind.Code,
        PromotionDiscountType.Percentage,
        DiscountPercent: 10m,
        Code: "BIENVENUE10");

    private static readonly PromotionDefinition EarlyBooking = new(
        "Réservation anticipée",
        PromotionKind.EarlyBooking,
        PromotionDiscountType.Percentage,
        DiscountPercent: 5m,
        MinimumDays: 30);

    private static readonly PromotionDefinition LongStay = new(
        "Long séjour",
        PromotionKind.LongStay,
        PromotionDiscountType.Percentage,
        DiscountPercent: 7m,
        MinimumDays: 14,
        IsCombinable: true);

    private static RatePlan Plan(params PromotionDefinition[] promotions)
    {
        var builder = ReferencePlan();
        foreach (var promotion in promotions)
        {
            builder.Promotion(promotion);
        }

        return builder.Build();
    }

    /// <summary>Reference rental (subtotal after tier 579.500) booked on <paramref name="quotedAt"/>.</summary>
    private static PricingRequest Reference(DateTimeOffset quotedAt, string? code = null) =>
        ReferenceRequest() with { QuotedAt = quotedAt, PromoCode = code };

    [Fact]
    public void A_code_promotion_applies_to_the_rental_subtotal_after_the_tier()
    {
        var quote = Quote(Plan(Welcome), Reference(Tunis(2027, 6, 1), " bienvenue10 "));

        var line = quote.Lines.Single(l => l.Kind == PriceLineKind.Promotion);
        (line.Code, line.Quantity, line.InclVat.Amount).ShouldBe((QuoteLineCodes.Promotion, 1, -57.950m));
        line.LabelKey.ShouldBe(QuoteLabels.PromotionCodePercent);
        line.LabelArguments.ShouldBe(["BIENVENUE10", "10"]);
        quote.PromoCodeStatus.ShouldBe(PromoCodeStatus.Applied);
        quote.AppliedPromotions.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            p => p.Kind.ShouldBe(PromotionKind.Code),
            p => p.Code.ShouldBe("BIENVENUE10"),
            p => p.Amount.ShouldBe(Money.Tnd(57.950m)),
            p => p.IsCombinable.ShouldBeFalse());
        quote.TotalInclVat.ShouldBe(Money.Tnd(681.400m - 57.950m));
    }

    [Fact]
    public void An_early_booking_applies_from_its_minimum_number_of_days_before_pickup()
    {
        var plan = Plan(EarlyBooking);

        // Pick-up 28/06/2027: booked 29/05 (30 days before) or earlier.
        var early = Quote(plan, Reference(Tunis(2027, 5, 29, 23, 0)));
        var late = Quote(plan, Reference(Tunis(2027, 5, 30, 0, 30)));

        early.Lines.Single(l => l.Kind == PriceLineKind.Promotion).ShouldSatisfyAllConditions(
            l => l.InclVat.ShouldBe(Money.Tnd(-28.975m)),
            l => l.LabelKey.ShouldBe(QuoteLabels.PromotionEarlyBookingPercent),
            l => l.LabelArguments.ShouldBe(["30", "5"]));
        late.Lines.ShouldNotContain(l => l.Kind == PriceLineKind.Promotion);
        early.PromoCodeStatus.ShouldBe(PromoCodeStatus.None);
    }

    [Fact]
    public void A_long_stay_applies_from_its_minimum_number_of_rental_days()
    {
        var plan = Plan(LongStay);

        var thirteen = Quote(plan, Request(Tunis(2027, 7, 1), Tunis(2027, 7, 14)));
        var fourteen = Quote(plan, Request(Tunis(2027, 7, 1), Tunis(2027, 7, 15)));

        thirteen.Lines.ShouldNotContain(l => l.Kind == PriceLineKind.Promotion);
        // 14 × 140 = 1960; tier 15 % → 1666; 7 % → 116.620.
        fourteen.Lines.Single(l => l.Kind == PriceLineKind.Promotion).InclVat.ShouldBe(Money.Tnd(-116.620m));
    }

    [Fact]
    public void Only_the_best_non_combinable_promotion_is_applied()
    {
        var plan = Plan(EarlyBooking, Welcome);

        var withCode = Quote(plan, Reference(Tunis(2027, 4, 1), "BIENVENUE10"));
        var withoutCode = Quote(plan, Reference(Tunis(2027, 4, 1)));

        withCode.AppliedPromotions.ShouldHaveSingleItem().Code.ShouldBe("BIENVENUE10");
        withCode.Lines.Single(l => l.Kind == PriceLineKind.Promotion).InclVat.ShouldBe(Money.Tnd(-57.950m));
        withoutCode.AppliedPromotions.ShouldHaveSingleItem().Kind.ShouldBe(PromotionKind.EarlyBooking);
    }

    [Fact]
    public void A_valid_code_that_is_not_the_best_offer_is_reported_as_not_retained()
    {
        var smallCode = Welcome with { DiscountPercent = 3m };
        var plan = Plan(EarlyBooking, smallCode);

        var quote = Quote(plan, Reference(Tunis(2027, 4, 1), "BIENVENUE10"));

        quote.AppliedPromotions.ShouldHaveSingleItem().Kind.ShouldBe(PromotionKind.EarlyBooking);
        quote.PromoCodeStatus.ShouldBe(PromoCodeStatus.NotRetained);
    }

    [Fact]
    public void Equal_non_combinable_discounts_keep_the_first_promotion_of_the_catalogue()
    {
        var plan = Plan(EarlyBooking with { DiscountPercent = 10m }, Welcome);

        var quote = Quote(plan, Reference(Tunis(2027, 4, 1), "BIENVENUE10"));

        quote.AppliedPromotions.ShouldHaveSingleItem().Kind.ShouldBe(PromotionKind.EarlyBooking);
        quote.PromoCodeStatus.ShouldBe(PromoCodeStatus.NotRetained);
    }

    [Fact]
    public void Combinable_promotions_stack_on_the_best_non_combinable_one()
    {
        var plan = Plan(EarlyBooking, Welcome, LongStay);

        // 15 days in high season: 2100; tier 14–29 −15 % → 1785; code 10 % → 178.500; long stay 7 % → 124.950.
        var quote = Quote(plan, Request(Tunis(2027, 7, 5), Tunis(2027, 7, 20), quotedAt: Tunis(2027, 6, 30), promoCode: "BIENVENUE10"));

        quote.Lines.Where(l => l.Kind == PriceLineKind.Promotion).Select(l => l.InclVat.Amount).ShouldBe([-178.500m, -124.950m]);
        quote.AppliedPromotions.Select(p => p.Kind).ShouldBe([PromotionKind.Code, PromotionKind.LongStay]);
        quote.PromoCodeStatus.ShouldBe(PromoCodeStatus.Applied);
    }

    [Fact]
    public void Promotions_never_take_the_rental_below_zero()
    {
        var huge = new PromotionDefinition("Offert", PromotionKind.Code, PromotionDiscountType.FixedAmount, DiscountAmount: Money.Tnd(5000m), Code: "OFFERT");
        var extra = new PromotionDefinition(
            "Fidélité",
            PromotionKind.LongStay,
            PromotionDiscountType.FixedAmount,
            DiscountAmount: Money.Tnd(20m),
            MinimumDays: 1,
            IsCombinable: true);
        var plan = Plan(huge, extra);

        var quote = Quote(plan, Reference(Tunis(2027, 6, 1), "OFFERT"));

        quote.Lines.Where(l => l.Kind == PriceLineKind.Promotion).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            l => l.InclVat.ShouldBe(Money.Tnd(-579.500m)),
            l => l.LabelKey.ShouldBe(QuoteLabels.PromotionCodeAmount),
            l => l.LabelArguments.ShouldBe(["OFFERT"]));
        quote.AppliedPromotions.ShouldHaveSingleItem().Amount.ShouldBe(Money.Tnd(579.500m));
        quote.TotalInclVat.ShouldBe(Money.Tnd(101.900m));
        quote.TotalInclVat.IsNegative.ShouldBeFalse();
    }

    [Fact]
    public void A_fixed_amount_promotion_reduces_by_its_amount()
    {
        var fixedLongStay = LongStay with { DiscountType = PromotionDiscountType.FixedAmount, DiscountPercent = null, DiscountAmount = Money.Tnd(50m), MinimumDays = 5 };

        var line = Quote(Plan(fixedLongStay), Reference(Tunis(2027, 6, 1))).Lines.Single(l => l.Kind == PriceLineKind.Promotion);

        line.InclVat.ShouldBe(Money.Tnd(-50m));
        line.LabelKey.ShouldBe(QuoteLabels.PromotionLongStayAmount);
        line.LabelArguments.ShouldBe(["5"]);
    }

    [Fact]
    public void An_unknown_or_inactive_code_is_reported_and_ignored()
    {
        var plan = Plan(Welcome, Welcome with { Name = "Ancien code", Code = "ETE2026", IsActive = false });

        var unknown = Quote(plan, Reference(Tunis(2027, 6, 1), "NOPE"));
        var inactive = Quote(plan, Reference(Tunis(2027, 6, 1), "ETE2026"));
        var blank = Quote(plan, Reference(Tunis(2027, 6, 1), "  "));

        unknown.PromoCodeStatus.ShouldBe(PromoCodeStatus.Unknown);
        unknown.AppliedPromotions.ShouldBeEmpty();
        inactive.PromoCodeStatus.ShouldBe(PromoCodeStatus.Unknown);
        blank.PromoCodeStatus.ShouldBe(PromoCodeStatus.None);
    }

    [Theory]
    [InlineData("booking_window")]
    [InlineData("pickup_window")]
    [InlineData("category")]
    [InlineData("channel")]
    public void A_code_excluded_by_a_filter_is_not_applicable(string filter)
    {
        var restricted = filter switch
        {
            "booking_window" => Welcome with { BookableFrom = new DateOnly(2027, 1, 1), BookableUntil = new DateOnly(2027, 5, 31) },
            "pickup_window" => Welcome with { PickupFrom = new DateOnly(2027, 9, 1), PickupUntil = new DateOnly(2027, 10, 31) },
            "category" => Welcome with { CategoryIds = [Mcmr] },
            _ => Welcome with { Channels = [SalesChannel.Phone, SalesChannel.Counter] },
        };

        var quote = Quote(Plan(restricted), Reference(Tunis(2027, 6, 1), "BIENVENUE10"));

        quote.PromoCodeStatus.ShouldBe(PromoCodeStatus.NotApplicable);
        quote.AppliedPromotions.ShouldBeEmpty();
    }

    [Fact]
    public void Filters_that_match_let_the_promotion_apply()
    {
        var restricted = Welcome with
        {
            BookableFrom = new DateOnly(2027, 6, 1),
            BookableUntil = new DateOnly(2027, 6, 1),
            PickupFrom = new DateOnly(2027, 6, 28),
            PickupUntil = new DateOnly(2027, 6, 28),
            CategoryIds = [Ecmr, Mcmr],
            Channels = [SalesChannel.Web],
        };

        Quote(Plan(restricted), Reference(Tunis(2027, 6, 1), "BIENVENUE10")).PromoCodeStatus.ShouldBe(PromoCodeStatus.Applied);
        Quote(Plan(restricted), Reference(Tunis(2027, 6, 1), "BIENVENUE10") with { Channel = SalesChannel.BookingCom })
            .PromoCodeStatus.ShouldBe(PromoCodeStatus.NotApplicable);
    }
}
