using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CarRental.Infrastructure.Persistence.Configurations.Messaging;

/// <summary>
/// Memory of the WhatsApp agent per conversation (<c>messaging.AgentConversationStates</c>, same id and agency as the
/// conversation, deleted with it): options of the latest search and amounts of the tools as JSON, counters. Ids, codes
/// and amounts only, left out of the audit log (it changes at every turn).
/// </summary>
internal sealed class AgentConversationStateConfiguration : IEntityTypeConfiguration<AgentConversationState>
{
    public void Configure(EntityTypeBuilder<AgentConversationState> builder)
    {
        builder.ToTable("AgentConversationStates", Schemas.Messaging);
        builder.ExcludeFromAudit();
        builder.HasKey(s => s.Id);
        builder.Property(s => s.AgencyId).IsRequired();
        builder.Ignore(s => s.ConversationId);
        builder.Ignore(s => s.Options);
        builder.Ignore(s => s.KnownAmounts);
        builder.Property<List<AgentOption>>("_options")
            .HasColumnName("Options")
            .HasConversion(AgentStateJson.OptionsConverter, AgentStateJson.OptionsComparer)
            .IsRequired();
        builder.Property<List<decimal>>("_amounts")
            .HasColumnName("KnownAmounts")
            .HasConversion(AgentStateJson.AmountsConverter, AgentStateJson.AmountsComparer)
            .IsRequired();
        builder.Property(s => s.LastQuoteLink).HasMaxLength(AgentConversationState.QuoteLinkMaxLength);
        builder.HasOne<Conversation>().WithOne().HasForeignKey<AgentConversationState>(s => s.Id).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Turns of the agent for the cost reports of each agency (<c>messaging.AgentTurnRecords</c>, by agency and month):
/// counts and costs, no content, kept after the conversation is purged (no foreign key to it: the id then points to
/// nothing). Not audited (one row per turn).
/// </summary>
internal sealed class AgentTurnRecordConfiguration : IEntityTypeConfiguration<AgentTurnRecord>
{
    public void Configure(EntityTypeBuilder<AgentTurnRecord> builder)
    {
        builder.ToTable("AgentTurnRecords", Schemas.Messaging);
        builder.ExcludeFromAudit();
        builder.HasKey(r => r.Id);
        builder.Property(r => r.AgencyId).IsRequired();
        builder.Property(r => r.Model).HasMaxLength(AgentTurnRecord.ModelMaxLength).IsUnicode(false);
        builder.Property(r => r.AiCostUsd).HasPrecision(18, 6);
        builder.Property(r => r.MetaCostUsd).HasPrecision(18, 6);
        builder.HasIndex(r => new { r.AgencyId, r.OccurredAtUtc });
        builder.HasIndex(r => r.ConversationId);
    }
}

/// <summary>Retries of the pending bot turn of a conversation (<c>messaging.BotTurnRetries</c>, same id and agency, deleted with it).</summary>
internal sealed class BotTurnRetryConfiguration : IEntityTypeConfiguration<BotTurnRetry>
{
    public void Configure(EntityTypeBuilder<BotTurnRetry> builder)
    {
        builder.ToTable("BotTurnRetries", Schemas.Messaging);
        builder.ExcludeFromAudit();
        builder.HasKey(r => r.Id);
        builder.Property(r => r.AgencyId).IsRequired();
        builder.Ignore(r => r.ConversationId);
        builder.HasOne<Conversation>().WithOne().HasForeignKey<BotTurnRetry>(r => r.Id).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// JSON of the memory of the agent: the options as small objects (amounts as invariant strings, every millime kept), the
/// amounts as an array of strings. Read back without validation; the comparers compare the JSON texts.
/// </summary>
internal static class AgentStateJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ValueConverter<List<AgentOption>, string> OptionsConverter { get; } = new(options => SerializeOptions(options), json => DeserializeOptions(json));

    public static ValueComparer<List<AgentOption>> OptionsComparer { get; } = new(
        (left, right) => SerializeOptions(left ?? new List<AgentOption>()) == SerializeOptions(right ?? new List<AgentOption>()),
        options => SerializeOptions(options).GetHashCode(StringComparison.Ordinal),
        options => DeserializeOptions(SerializeOptions(options)));

    public static ValueConverter<List<decimal>, string> AmountsConverter { get; } = new(amounts => SerializeAmounts(amounts), json => DeserializeAmounts(json));

    public static ValueComparer<List<decimal>> AmountsComparer { get; } = new(
        (left, right) => (left ?? new List<decimal>()).SequenceEqual(right ?? new List<decimal>()),
        amounts => amounts.Aggregate(0, (hash, amount) => HashCode.Combine(hash, amount)),
        amounts => amounts.ToList());

    public static string SerializeOptions(List<AgentOption> options) =>
        JsonSerializer.Serialize(options.Select(o => new OptionJson(
            o.Id,
            o.AgencyId,
            o.CategoryId,
            o.CategoryCode,
            o.CategoryName,
            o.PickupLocationId,
            o.PickupLocationCode,
            o.ReturnLocationId,
            o.ReturnLocationCode,
            o.PickupAt,
            o.ReturnAt,
            o.Days,
            Invariant(o.Total.Amount),
            Invariant(o.Deposit.Amount))).ToList(), Options);

    public static List<AgentOption> DeserializeOptions(string json) =>
        [
            .. (JsonSerializer.Deserialize<List<OptionJson>>(json, Options) ?? []).Select(o => new AgentOption(
                o.Id,
                o.AgencyId,
                o.CategoryId,
                o.CategoryCode,
                o.CategoryName,
                o.PickupLocationId,
                o.PickupLocationCode,
                o.ReturnLocationId,
                o.ReturnLocationCode,
                o.PickupAt,
                o.ReturnAt,
                o.Days,
                Money.Tnd(Parse(o.Total)),
                Money.Tnd(Parse(o.Deposit)))),
        ];

    public static string SerializeAmounts(List<decimal> amounts) => JsonSerializer.Serialize(amounts.Select(Invariant).ToList(), Options);

    public static List<decimal> DeserializeAmounts(string json) => [.. (JsonSerializer.Deserialize<List<string>>(json, Options) ?? []).Select(Parse)];

    private static string Invariant(decimal amount) => amount.ToString("0.000", CultureInfo.InvariantCulture);

    private static decimal Parse(string amount) => decimal.Parse(amount, NumberStyles.Number, CultureInfo.InvariantCulture);

    private sealed record OptionJson(
        string Id,
        Guid AgencyId,
        Guid CategoryId,
        string CategoryCode,
        string CategoryName,
        Guid PickupLocationId,
        string PickupLocationCode,
        Guid ReturnLocationId,
        string ReturnLocationCode,
        DateTimeOffset PickupAt,
        DateTimeOffset ReturnAt,
        int Days,
        string Total,
        string Deposit);
}
