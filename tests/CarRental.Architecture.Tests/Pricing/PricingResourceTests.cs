using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Pricing;

namespace CarRental.Architecture.Tests.Pricing;

/// <summary>Every text the pricing screens and quotes look up by key exists in French and in English.</summary>
public sealed class PricingResourceTests
{
    private static readonly CultureInfo[] Cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en")];

    private static readonly ResourceManager Shared = new("CarRental.Web.Resources.SharedResources", Assemblies.Web);
    private static readonly ResourceManager Admin = new("CarRental.Web.Resources.Admin.Pricing.PricingAdminResources", Assemblies.Web);
    private static readonly ResourceManager Quote = new("CarRental.Web.Resources.Admin.Pricing.QuoteResources", Assemblies.Web);

    public static TheoryData<string> ErrorCodes() =>
    [
        .. typeof(PricingErrors).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<Error>()
            .Select(e => e.Code)
            .Append(PricingErrors.MinimumDurationNotMet(3).Code)
            .Distinct(),
    ];

    public static TheoryData<string> QuoteLabelKeys() => [.. QuoteLabels.All];

    public static TheoryData<string> EnumKeys() =>
    [
        .. Enum.GetNames<SalesChannel>().Select(n => $"Channel.{n}").Append("Channel.Default"),
        .. Enum.GetNames<RatePlanStatus>().Select(n => $"Status.{n}"),
        .. Enum.GetNames<SeasonLevel>().Select(n => $"Level.{n}"),
        .. Enum.GetNames<RatePeriodKindInfo>().Select(n => $"Period.{n}"),
        .. Enum.GetNames<ExtraKind>().Select(n => $"ExtraKind.{n}"),
        .. Enum.GetNames<ExtraPricingMode>().Select(n => $"ExtraMode.{n}"),
        .. Enum.GetNames<PromotionKind>().Select(n => $"PromotionKind.{n}"),
        .. Enum.GetNames<PromotionDiscountType>().Select(n => $"DiscountType.{n}"),
        .. Enum.GetNames<ChannelPricingMode>().Select(n => $"ChannelPricing.{n}"),
        .. Enum.GetNames<PromoCodeOutcome>().Select(n => $"PromoCode.{n}"),
        .. Enum.GetNames<RatePlanStatus>().Select(n => $"Plan.ReadOnly.{n}"),
        .. new[] { PromotionKind.EarlyBooking, PromotionKind.LongStay }.Select(k => $"Promotions.MinimumDays.{k}"),
        .. typeof(PlanValidationCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => $"Plan.Issue.{f.GetValue(null)}"),
    ];

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public void Every_pricing_error_has_a_user_facing_text(string code)
    {
        foreach (var culture in Cultures)
        {
            Shared.GetString($"Error.{code}", culture).ShouldNotBeNullOrWhiteSpace($"Error.{code} ({culture.Name})");
        }
    }

    [Theory]
    [MemberData(nameof(QuoteLabelKeys))]
    public void Every_quote_line_label_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Quote.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }

    [Theory]
    [MemberData(nameof(EnumKeys))]
    public void Every_value_shown_by_the_pricing_screens_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Admin.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }

    [Fact]
    public void Extra_labels_use_the_name_of_their_language()
    {
        // Arguments of an extra label: {0} French name, {1} English name.
        Quote.GetString(QuoteLabels.Extra, CultureInfo.InvariantCulture).ShouldBe("{0}");
        Quote.GetString(QuoteLabels.Extra, CultureInfo.GetCultureInfo("en")).ShouldBe("{1}");
    }
}
