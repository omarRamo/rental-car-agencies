using CarRental.Application.Common.Persistence;
using CarRental.Application.Messaging.Contracts;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging;

/// <summary><see cref="IMessagingQueries"/>: projections of the conversations and messages for the other modules.</summary>
internal sealed class MessagingQueries(IAppDbContext db) : IMessagingQueries
{
    private const int MaxMessages = 200;

    public async Task<ConversationInfo?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        (await db.Set<Conversation>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken))?.ToInfo();

    public async Task<ConversationInfo?> FindByNumberAsync(Guid agencyId, string whatsAppNumber, CancellationToken cancellationToken = default)
    {
        var number = WhatsAppNumbers.Normalize(whatsAppNumber);
        if (number.IsFailure)
        {
            return null;
        }

        return (await db.Set<Conversation>().AsNoTracking().FirstOrDefaultAsync(c => c.AgencyId == agencyId && c.WhatsAppNumber == number.Value, cancellationToken))?.ToInfo();
    }

    public async Task<IReadOnlyList<ConversationInfo>> GetConversationsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default) =>
        [
            .. (await db.Set<Conversation>()
                .AsNoTracking()
                .Where(c => c.PersonId == personId)
                .OrderByDescending(c => c.LastActivityAtUtc)
                .ToListAsync(cancellationToken))
                .Select(c => c.ToInfo()),
        ];

    public async Task<IReadOnlyList<ConversationMessageInfo>> GetMessagesAsync(Guid conversationId, int limit = 50, CancellationToken cancellationToken = default)
    {
        var messages = await db.Set<Message>()
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(Math.Clamp(limit, 1, MaxMessages))
            .ToListAsync(cancellationToken);
        messages.Reverse();
        return [.. messages.Select(m => m.ToInfo())];
    }
}
