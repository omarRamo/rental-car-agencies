using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using QuoteLineKind = CarRental.Domain.Bookings.Quotes.QuoteLineKind;

namespace CarRental.Application.Bookings.Quotes;

/// <summary>
/// Freezes a <see cref="PriceQuote"/> of the pricing engine as the content of a quote version (lines, totals, deposit,
/// promotions, inputs and daily rates as JSON snapshots) and reads the snapshots back for display.
/// </summary>
/// <remarks>
/// Snapshots are written with stable formats: amounts as invariant strings with their millimes (<c>"681.400"</c>),
/// enums by name, instants in ISO 8601. They describe how a frozen price was computed and are never re-priced from.
/// </remarks>
internal static class QuoteSnapshots
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static QuoteContent ToContent(PriceQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        var inputs = quote.Inputs;
        return new QuoteContent(
            inputs.AgencyId,
            inputs.CategoryId,
            inputs.Channel,
            inputs.PickupLocationId,
            inputs.ReturnLocationId,
            inputs.PickupAt,
            inputs.ReturnAt,
            quote.RatePlanId,
            quote.RatePlanVersion,
            quote.Days,
            [.. quote.Lines.Select(ToLine)],
            quote.TotalExclVat,
            quote.TotalVat,
            quote.TotalInclVat,
            quote.RentalSubtotal,
            quote.Deposit,
            quote.IncludesFranchiseWaiver,
            (QuotePromoCodeOutcome)quote.PromoCodeOutcome,
            inputs.PromoCode,
            JsonSerializer.Serialize(quote.AppliedPromotions, Options),
            JsonSerializer.Serialize(inputs, Options),
            JsonSerializer.Serialize(quote.DailyRates, Options));
    }

    public static PriceQuoteInputs? ReadInputs(string json) => Read<PriceQuoteInputs>(json);

    public static IReadOnlyList<AppliedPromotionInfo> ReadPromotions(string json) => Read<List<AppliedPromotionInfo>>(json) ?? [];

    public static IReadOnlyList<DailyRateInfo> ReadDailyRates(string json) => Read<List<DailyRateInfo>>(json) ?? [];

    /// <summary>The VAT rate of a content (from its inputs, else from its rental lines).</summary>
    public static decimal VatRateOf(QuoteContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (ReadInputs(content.InputsJson) is { } inputs)
        {
            return inputs.VatRate;
        }

        return content.Lines.FirstOrDefault(line => line.Kind == QuoteLineKind.Rental)?.VatRate ?? 0m;
    }

    private static QuoteLineData ToLine(PriceQuoteLine line) => new(
        (QuoteLineKind)line.Kind,
        line.Code,
        line.LabelKey,
        [.. line.LabelArguments],
        line.Quantity,
        line.UnitPrice,
        line.ExclVat,
        line.Vat,
        line.InclVat,
        line.VatRate);

    private static T? Read<T>(string json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            // A snapshot is an explanation, never an input: an unreadable one is shown as missing.
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MoneySnapshotConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>TND amounts as invariant strings with their millimes, e.g. <c>"681.400"</c>.</summary>
    private sealed class MoneySnapshotConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? Money.Tnd(decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture))
                : Money.Tnd(reader.GetDecimal());

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Amount.ToString("F3", CultureInfo.InvariantCulture));
    }
}
