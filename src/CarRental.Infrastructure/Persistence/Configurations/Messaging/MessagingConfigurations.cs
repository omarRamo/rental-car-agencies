using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Messaging;

/// <summary>
/// WhatsApp conversations (<c>messaging.Conversations</c>): one per number (unique), indexes of the inbox (status and
/// activity) and of the links. A plain table, not temporal: the retention purge must really delete the exchanges.
/// Audited for the business actions (mode, links, status), with the number, the profile name and the hand-over reason
/// masked; the counters, the need and the activity times are left out of the audit log (noise of every message).
/// </summary>
internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations", Schemas.Messaging);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.WhatsAppNumber).HasMaxLength(WhatsAppNumbers.MaxLength).IsUnicode(false).IsRequired().IsAuditSensitive();
        builder.Property(c => c.ProfileName).HasMaxLength(Conversation.ProfileNameMaxLength).IsAuditSensitive();
        builder.Property(c => c.HumanAgentName).HasMaxLength(Conversation.AgentNameMaxLength);
        builder.Property(c => c.HandoverReason).HasMaxLength(Conversation.HandoverReasonMaxLength).IsAuditSensitive();
        builder.Property(c => c.LastActivityAtUtc).IsAuditIgnored();
        builder.Property(c => c.LastCustomerMessageAtUtc).IsAuditIgnored();
        builder.Property(c => c.UnreadCount).IsAuditIgnored();
        builder.Property(c => c.LastReadAtUtc).IsAuditIgnored();
        builder.Ignore(c => c.WindowClosesAtUtc);

        builder.ComplexProperty(c => c.Need, need =>
        {
            need.Property(n => n.PickupPlace).HasColumnName("NeedPickupPlace").HasMaxLength(ConversationNeed.PlaceMaxLength).HasAnnotation(AuditAnnotations.Ignore, true);
            need.Property(n => n.ReturnPlace).HasColumnName("NeedReturnPlace").HasMaxLength(ConversationNeed.PlaceMaxLength).HasAnnotation(AuditAnnotations.Ignore, true);
            need.Property(n => n.PickupAt).HasColumnName("NeedPickupAt").HasAnnotation(AuditAnnotations.Ignore, true);
            need.Property(n => n.ReturnAt).HasColumnName("NeedReturnAt").HasAnnotation(AuditAnnotations.Ignore, true);
            need.Property(n => n.Passengers).HasColumnName("NeedPassengers").HasAnnotation(AuditAnnotations.Ignore, true);
            need.Property(n => n.Luggage).HasColumnName("NeedLuggage").HasAnnotation(AuditAnnotations.Ignore, true);
            need.Property(n => n.Gearbox).HasColumnName("NeedGearbox").HasAnnotation(AuditAnnotations.Ignore, true);
            need.Property(n => n.Budget).HasColumnName("NeedBudget").HasAnnotation(AuditAnnotations.Ignore, true);
            need.Ignore(n => n.IsEmpty);
        });
        builder.ComplexProperty(c => c.Usage, usage =>
        {
            usage.Property(u => u.BotTurns).HasColumnName("BotTurns").HasAnnotation(AuditAnnotations.Ignore, true);
            usage.Property(u => u.InputTokens).HasColumnName("BotInputTokens").HasAnnotation(AuditAnnotations.Ignore, true);
            usage.Property(u => u.OutputTokens).HasColumnName("BotOutputTokens").HasAnnotation(AuditAnnotations.Ignore, true);
            usage.Property(u => u.CacheReadTokens).HasColumnName("BotCacheReadTokens").HasAnnotation(AuditAnnotations.Ignore, true);
            usage.Property(u => u.CacheWriteTokens).HasColumnName("BotCacheWriteTokens").HasAnnotation(AuditAnnotations.Ignore, true);
            usage.Property(u => u.ToolCalls).HasColumnName("BotToolCalls").HasAnnotation(AuditAnnotations.Ignore, true);
            usage.Property(u => u.AiCostUsd).HasColumnName("BotAiCostUsd").HasPrecision(18, 6).HasAnnotation(AuditAnnotations.Ignore, true);
            usage.Property(u => u.MetaCostUsd).HasColumnName("BotMetaCostUsd").HasPrecision(18, 6).HasAnnotation(AuditAnnotations.Ignore, true);
        });

        builder.HasIndex(c => c.WhatsAppNumber).IsUnique();
        builder.HasIndex(c => new { c.Status, c.LastActivityAtUtc });
        builder.HasIndex(c => c.LastActivityAtUtc);
        builder.HasIndex(c => c.PersonId).HasFilter("[PersonId] IS NOT NULL");
        builder.HasIndex(c => c.BookingId).HasFilter("[BookingId] IS NOT NULL");
    }
}

/// <summary>
/// Messages (<c>messaging.Messages</c>): the communications log itself (SPEC §14), so left out of the audit log — no
/// message content ever reaches it — and deleted by the retention purge. The Meta id is unique (deduplication of inbound
/// messages, lookup of statuses); the content is stored as JSON (<see cref="MessageContentJson"/>).
/// </summary>
internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages", Schemas.Messaging);
        builder.ExcludeFromAudit();
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Content).HasConversion(MessageContentJson.Converter, MessageContentJson.Comparer).IsRequired();
        builder.Property(m => m.Text).HasMaxLength(TextContent.MaxLength);
        builder.Property(m => m.MetaMessageId).HasMaxLength(Message.MetaMessageIdMaxLength).IsUnicode(false);
        builder.Property(m => m.AuthorName).HasMaxLength(Message.AuthorNameMaxLength);
        builder.Property(m => m.FailureCode).HasMaxLength(Message.FailureCodeMaxLength).IsUnicode(false);
        builder.Property(m => m.MetaErrorTitle).HasMaxLength(Message.MetaErrorTitleMaxLength);
        builder.Property(m => m.PricingCategory).HasMaxLength(Message.PricingFieldMaxLength).IsUnicode(false);
        builder.Property(m => m.PricingModel).HasMaxLength(Message.PricingFieldMaxLength).IsUnicode(false);
        builder.Ignore(m => m.IsInbound);

        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(m => m.StatusChanges).WithOne().HasForeignKey(c => c.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.StatusChanges).HasField("_statusChanges").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasIndex(m => m.MetaMessageId).IsUnique().HasFilter("[MetaMessageId] IS NOT NULL");
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAtUtc });
        builder.HasIndex(m => m.CreatedAtUtc);
    }
}

/// <summary>Delivery statuses reported by Meta, each kept once per message (unique index: duplicate webhooks).</summary>
internal sealed class MessageStatusChangeConfiguration : IEntityTypeConfiguration<MessageStatusChange>
{
    public void Configure(EntityTypeBuilder<MessageStatusChange> builder)
    {
        builder.ToTable("MessageStatusChanges", Schemas.Messaging);
        builder.ExcludeFromAudit();
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ErrorTitle).HasMaxLength(Message.MetaErrorTitleMaxLength);
        builder.HasIndex(c => new { c.MessageId, c.Status }).IsUnique();
    }
}

/// <summary>Notifications queued per rental file and kind (unique: never twice), ids only; purged with the conversations.</summary>
internal sealed class BookingNotificationConfiguration : IEntityTypeConfiguration<BookingNotification>
{
    public void Configure(EntityTypeBuilder<BookingNotification> builder)
    {
        builder.ToTable("BookingNotifications", Schemas.Messaging);
        builder.ExcludeFromAudit();
        builder.HasKey(n => n.Id);
        builder.HasIndex(n => new { n.BookingId, n.Kind }).IsUnique();
        builder.HasIndex(n => n.CreatedAtUtc);
    }
}
