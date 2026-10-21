using System.Collections.Frozen;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarRental.Application.Common.Outbox;
using CarRental.Infrastructure.Serialization;

namespace CarRental.Infrastructure.Outbox;

/// <summary>JSON (de)serialization of outbox payloads.</summary>
public sealed class OutboxSerializer
{
    private readonly JsonSerializerOptions _options;

    public OutboxSerializer()
    {
        _options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        foreach (var converter in DomainJsonConverters.All)
        {
            _options.Converters.Add(converter);
        }

        _options.MakeReadOnly(populateMissingResolver: true);
    }

    public string Serialize(object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, message.GetType(), _options);
    }

    public object Deserialize(string payload, Type type)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(type);
        return JsonSerializer.Deserialize(payload, type, _options)
            ?? throw new JsonException($"The outbox payload of type {type.FullName} is null.");
    }
}

/// <summary>
/// Message types that have handlers, by CLR full name. Only these types are ever deserialized: a stored type name
/// can never make the processor instantiate an arbitrary type.
/// </summary>
public sealed class OutboxMessageTypeRegistry(IEnumerable<OutboxMessageTypeRegistration> registrations)
{
    private readonly FrozenDictionary<string, Type> _types = registrations
        .Select(r => r.MessageType)
        .Distinct()
        .ToFrozenDictionary(t => t.FullName ?? t.Name, StringComparer.Ordinal);

    public Type? Resolve(string typeName) => _types.GetValueOrDefault(typeName);

    /// <summary>Stored names of the types that have handlers (every other stored message has nothing to run).</summary>
    public IReadOnlyCollection<string> HandledTypeNames => _types.Keys;
}
