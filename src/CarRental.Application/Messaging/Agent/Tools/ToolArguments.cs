using System.Globalization;
using System.Text.Json;

namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// The arguments of a tool call, checked against the input schema of the tool before the tool runs: known properties
/// only (<c>additionalProperties: false</c>), required ones present, types (string, integer, number, boolean), enums,
/// bounds and lengths. A number written as a string (<c>"4"</c>) is accepted for a numeric property; a null is an
/// absent value. The errors are returned to the model, in French, so that it corrects its call.
/// </summary>
internal sealed class ToolArguments
{
    private readonly Dictionary<string, JsonElement> _values;

    private ToolArguments(Dictionary<string, JsonElement> values, IReadOnlyList<string> errors)
    {
        _values = values;
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    /// <summary>The checked arguments, or <see langword="null"/> when they cannot be read at all.</summary>
    public static ToolArguments? Parse(IDictionary<string, object?>? arguments, string inputSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSchema);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            foreach (var (name, value) in arguments ?? new Dictionary<string, object?>())
            {
                var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value);
                if (element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    values[name] = element.Clone();
                }
            }
        }
        catch (NotSupportedException)
        {
            return null;
        }

        using var schema = JsonDocument.Parse(inputSchema);
        var properties = schema.RootElement.GetProperty("properties");
        var errors = new List<string>();
        foreach (var name in values.Keys.Where(n => !properties.TryGetProperty(n, out _)).ToList())
        {
            errors.Add($"« {name} » n'est pas un argument de cet outil.");
            values.Remove(name);
        }

        if (schema.RootElement.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!).Where(n => !values.ContainsKey(n)))
            {
                errors.Add($"« {name} » est obligatoire.");
            }
        }

        foreach (var (name, value) in values.ToList())
        {
            if (Check(name, value, properties.GetProperty(name)) is { } error)
            {
                errors.Add(error);
                values.Remove(name);
            }
        }

        return new ToolArguments(values, errors);
    }

    public string? String(string name) => _values.TryGetValue(name, out var value) ? value.GetString()?.Trim() : null;

    public int? Integer(string name) => _values.TryGetValue(name, out var value) && TryNumber(value, out var number) ? (int)number : null;

    public decimal? Number(string name) => _values.TryGetValue(name, out var value) && TryNumber(value, out var number) ? number : null;

    public bool? Boolean(string name) => _values.TryGetValue(name, out var value) ? value.GetBoolean() : null;

    private static string? Check(string name, JsonElement value, JsonElement property)
    {
        var type = property.TryGetProperty("type", out var declared) ? declared.GetString() : null;
        switch (type)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return $"« {name} » doit être une chaîne de caractères.";
                }

                var text = value.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return $"« {name} » est vide.";
                }

                if (property.TryGetProperty("maxLength", out var maxLength) && text.Length > maxLength.GetInt32())
                {
                    return $"« {name} » dépasse {maxLength.GetInt32()} caractères.";
                }

                if (property.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(a => string.Equals(a.GetString(), text.Trim(), StringComparison.Ordinal)))
                {
                    return $"« {name} » doit valoir : {string.Join(", ", allowed.EnumerateArray().Select(a => a.GetString()))}.";
                }

                return null;

            case "integer" or "number":
                if (!TryNumber(value, out var number) || (type == "integer" && number != decimal.Truncate(number)))
                {
                    return type == "integer" ? $"« {name} » doit être un nombre entier." : $"« {name} » doit être un nombre.";
                }

                if ((property.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal())
                    || (property.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal()))
                {
                    return $"« {name} » doit être compris entre {Bound(property, "minimum")} et {Bound(property, "maximum")}.";
                }

                return null;

            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"« {name} » doit valoir true ou false.";

            default:
                return null;
        }
    }

    private static string Bound(JsonElement property, string keyword) =>
        property.TryGetProperty(keyword, out var bound) ? bound.GetDecimal().ToString(CultureInfo.InvariantCulture) : "…";

    private static bool TryNumber(JsonElement value, out decimal number)
    {
        number = 0m;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out number),
            JsonValueKind.String => decimal.TryParse(value.GetString()?.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out number),
            _ => false,
        };
    }
}
