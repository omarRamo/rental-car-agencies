using System.Globalization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging.Agent.Knowledge;

/// <summary>The texts of a knowledge entry as typed in <c>/admin/whatsapp/knowledge</c>.</summary>
public sealed record KnowledgeEntryForm(string? TopicKey, string? Title, string? Keywords, string? AnswerFrench, string? AnswerEnglish, string? AnswerArabic);

/// <summary>A line of the knowledge base screen, with its validation.</summary>
public sealed record KnowledgeEntryItem(
    Guid Id,
    string TopicKey,
    string Title,
    string? Keywords,
    string AnswerFrench,
    string AnswerEnglish,
    string? AnswerArabic,
    bool IsValidated,
    string? ValidatedByName,
    DateTimeOffset? ValidatedAtUtc,
    bool IsActive,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>The bot gives it (validated and active).</summary>
    public bool IsUsableByBot => IsValidated && IsActive;
}

/// <summary>Reads of the knowledge base screen (permission <c>whatsapp.knowledge</c>).</summary>
public sealed class KnowledgeBaseQueries(IAppDbContext db, IUserContext user)
{
    public async Task<Result<PagedResult<KnowledgeEntryItem>>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!user.HasPermission(Permissions.WhatsAppKnowledge))
        {
            return KnowledgeErrors.Forbidden;
        }

        var query = db.Set<KnowledgeEntry>().AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var entries = await query
            .OrderBy(e => e.IsValidated)
            .ThenBy(e => e.TopicKey)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<KnowledgeEntryItem>([.. entries.Select(ToItem)], total, page.Page, page.PageSize);
    }

    internal static KnowledgeEntryItem ToItem(KnowledgeEntry entry) =>
        new(
            entry.Id,
            entry.TopicKey,
            entry.Title,
            entry.Keywords,
            entry.AnswerFrench,
            entry.AnswerEnglish,
            entry.AnswerArabic,
            entry.IsValidated,
            entry.ValidatedByName,
            entry.ValidatedAtUtc,
            entry.IsActive,
            entry.UpdatedAtUtc);
}

/// <summary>
/// The knowledge base of the WhatsApp agent (tool <c>infos_agence</c>), managed by the holders of
/// <c>whatsapp.knowledge</c> (the manager by default): new answers are drafts, an edit sends a validated answer back to
/// draft, a validation (audited) lets the bot give it, a deactivation withdraws it. Changes are audited automatically and
/// kept in a temporal history.
/// </summary>
public sealed class KnowledgeBaseService(IAppDbContext db, IUnitOfWork unitOfWork, IUserContext user, IAuditTrail auditTrail, TimeProvider clock)
{
    public async Task<Result<Guid>> CreateAsync(KnowledgeEntryForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!user.HasPermission(Permissions.WhatsAppKnowledge))
        {
            return KnowledgeErrors.Forbidden;
        }

        var content = KnowledgeContent.Create(form.Title, form.Keywords, form.AnswerFrench, form.AnswerEnglish, form.AnswerArabic);
        if (content.IsFailure)
        {
            return content.Error;
        }

        var entry = KnowledgeEntry.CreateDraft(form.TopicKey, content.Value, clock.GetUtcNow());
        if (entry.IsFailure)
        {
            return entry.Error;
        }

        if (await db.Set<KnowledgeEntry>().AnyAsync(e => e.TopicKey == entry.Value.TopicKey, cancellationToken))
        {
            return KnowledgeErrors.TopicTaken;
        }

        db.Set<KnowledgeEntry>().Add(entry.Value);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A colleague created the same topic at the same instant (unique index).
            return KnowledgeErrors.TopicTaken;
        }

        return entry.Value.Id;
    }

    /// <summary>Changes the texts (the topic key stays): a validated answer goes back to draft when its texts change.</summary>
    public Task<Result> UpdateAsync(Guid entryId, KnowledgeEntryForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var content = KnowledgeContent.Create(form.Title, form.Keywords, form.AnswerFrench, form.AnswerEnglish, form.AnswerArabic);
        return content.IsFailure
            ? Task.FromResult(Result.Failure(content.Error))
            : ChangeAsync(entryId, (entry, now) =>
            {
                entry.Edit(content.Value, now);
                return Task.FromResult(Result.Success());
            }, cancellationToken);
    }

    /// <summary>Validates the current texts: the bot may give them from now on (audited).</summary>
    public Task<Result> ValidateAsync(Guid entryId, CancellationToken cancellationToken) =>
        ChangeAsync(entryId, async (entry, now) =>
        {
            var validated = entry.Validate(user.ToAuditActor(), now);
            if (validated.IsSuccess)
            {
                await auditTrail.RecordAsync(
                    new AuditEvent(AgentAuditActions.KnowledgeValidated)
                    {
                        EntityType = nameof(KnowledgeEntry),
                        EntityId = entry.Id.ToString(),
                        Details = string.Create(CultureInfo.InvariantCulture, $"knowledge entry {entry.TopicKey} validated for the WhatsApp agent"),
                    },
                    cancellationToken);
            }

            return validated;
        }, cancellationToken);

    public Task<Result> DeactivateAsync(Guid entryId, CancellationToken cancellationToken) =>
        ChangeAsync(entryId, (entry, now) =>
        {
            entry.Deactivate(now);
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    public Task<Result> ActivateAsync(Guid entryId, CancellationToken cancellationToken) =>
        ChangeAsync(entryId, (entry, now) =>
        {
            entry.Activate(now);
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    private async Task<Result> ChangeAsync(Guid entryId, Func<KnowledgeEntry, DateTimeOffset, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (!user.HasPermission(Permissions.WhatsAppKnowledge) || user.UserId is null)
        {
            return KnowledgeErrors.Forbidden;
        }

        try
        {
            // One unit of work: the change and its audit event are committed together (or not at all).
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var entry = await db.Set<KnowledgeEntry>().FirstOrDefaultAsync(e => e.Id == entryId, ct);
                    return entry is null ? KnowledgeErrors.NotFound : await change(entry, clock.GetUtcNow());
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return KnowledgeErrors.ConcurrentUpdate;
        }
    }
}
