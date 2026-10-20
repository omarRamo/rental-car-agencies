namespace CarRental.Infrastructure.Outbox;

/// <summary>
/// A message written in the same transaction as the change that produced it (domain event or application message),
/// then dispatched to its handlers by the outbox processor (Hangfire), with retries and back-off.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 512;
    public const int ErrorMaxLength = 4000;
    public const int CorrelationIdMaxLength = 64;

    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>CLR full name of the payload type.</summary>
    public string Type { get; private set; } = null!;

    /// <summary>JSON payload.</summary>
    public string Payload { get; private set; } = null!;

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    /// <summary>Failed attempts so far.</summary>
    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create(string type, string payload, DateTimeOffset occurredAtUtc, string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);
        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = type.Length > TypeMaxLength ? type[..TypeMaxLength] : type,
            Payload = payload,
            OccurredAtUtc = occurredAtUtc.ToUniversalTime(),
            CorrelationId = correlationId is { Length: > CorrelationIdMaxLength } ? correlationId[..CorrelationIdMaxLength] : correlationId,
        };
    }

    public void MarkProcessed(DateTimeOffset processedAtUtc)
    {
        ProcessedAtUtc = processedAtUtc.ToUniversalTime();
        NextAttemptAtUtc = null;
    }
}
