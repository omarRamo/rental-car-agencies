using CarRental.Domain.Messaging.Agent;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Configurations.Messaging;

/// <summary>
/// Knowledge base of the WhatsApp agent (<c>messaging.KnowledgeEntries</c>): a temporal table — what the bot could say at
/// any date — audited, one entry per topic (unique), read by the bot when validated and active.
/// </summary>
internal sealed class KnowledgeEntryConfiguration : IEntityTypeConfiguration<KnowledgeEntry>
{
    public void Configure(EntityTypeBuilder<KnowledgeEntry> builder)
    {
        builder.ToTemporalTable("KnowledgeEntries", Schemas.Messaging);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.TopicKey).HasMaxLength(KnowledgeEntry.TopicKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(KnowledgeEntry.TitleMaxLength).IsRequired();
        builder.Property(e => e.Keywords).HasMaxLength(KnowledgeEntry.KeywordsMaxLength);
        builder.Property(e => e.AnswerFrench).HasMaxLength(KnowledgeEntry.AnswerMaxLength).IsRequired();
        builder.Property(e => e.AnswerEnglish).HasMaxLength(KnowledgeEntry.AnswerMaxLength).IsRequired();
        builder.Property(e => e.AnswerArabic).HasMaxLength(KnowledgeEntry.AnswerMaxLength);
        builder.Property(e => e.ValidatedByName).HasMaxLength(KnowledgeEntry.ValidatorNameMaxLength);
        builder.Ignore(e => e.IsUsableByBot);
        builder.HasIndex(e => e.TopicKey).IsUnique();
    }
}
