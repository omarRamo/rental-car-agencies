using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarRental.Domain.Common;

namespace CarRental.Infrastructure.Serialization;

/// <summary>JSON converters for the value objects of the shared kernel (outbox payloads, integration messages).</summary>
public static class DomainJsonConverters
{
    public static IReadOnlyList<JsonConverter> All { get; } =
    [
        new MoneyJsonConverter(),
        new PeriodJsonConverter(),
        new AcrissCodeJsonConverter(),
        new DocumentNumberJsonConverter(),
    ];
}

/// <summary><c>{"amount":"681.400","currency":"TND"}</c> (amount as an invariant string to keep every millime).</summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Money must be a JSON object.");
        }

        decimal? amount = null;
        string? currency = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "amount", StringComparison.OrdinalIgnoreCase))
            {
                amount = reader.TokenType == JsonTokenType.String
                    ? decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
                    : reader.GetDecimal();
            }
            else if (string.Equals(name, "currency", StringComparison.OrdinalIgnoreCase))
            {
                currency = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        var parsedCurrency = Currency.FromCode(currency);
        if (amount is null || parsedCurrency.IsFailure)
        {
            throw new JsonException("Money requires an amount and a known currency.");
        }

        return Money.Of(amount.Value, parsedCurrency.Value);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteString("amount", value.Amount.ToString("F" + value.Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        writer.WriteString("currency", value.Currency.Code);
        writer.WriteEndObject();
    }
}

/// <summary><c>{"start":"…Z","end":"…Z"}</c>.</summary>
public sealed class PeriodJsonConverter : JsonConverter<Period>
{
    public override Period Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Period must be a JSON object.");
        }

        DateTimeOffset? start = null;
        DateTimeOffset? end = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "start", StringComparison.OrdinalIgnoreCase))
            {
                start = reader.GetDateTimeOffset();
            }
            else if (string.Equals(name, "end", StringComparison.OrdinalIgnoreCase))
            {
                end = reader.GetDateTimeOffset();
            }
            else
            {
                reader.Skip();
            }
        }

        if (start is null || end is null || Period.Create(start.Value, end.Value) is not { IsSuccess: true } period)
        {
            throw new JsonException("Period requires a start before its end.");
        }

        return period.Value;
    }

    public override void Write(Utf8JsonWriter writer, Period value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteString("start", value.Start);
        writer.WriteString("end", value.End);
        writer.WriteEndObject();
    }
}

public sealed class AcrissCodeJsonConverter : JsonConverter<AcrissCode>
{
    public override AcrissCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        AcrissCode.Create(reader.GetString()) is { IsSuccess: true } code ? code.Value : throw new JsonException("Invalid ACRISS code.");

    public override void Write(Utf8JsonWriter writer, AcrissCode value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.Value);
    }
}

public sealed class DocumentNumberJsonConverter : JsonConverter<DocumentNumber>
{
    public override DocumentNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DocumentNumber.Parse(reader.GetString()) is { IsSuccess: true } number ? number.Value : throw new JsonException("Invalid document number.");

    public override void Write(Utf8JsonWriter writer, DocumentNumber value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.Value);
    }
}
