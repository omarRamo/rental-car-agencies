using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging.Inbox;

/// <summary>
/// Actions of the shared WhatsApp inbox (permission <c>whatsapp.inbox</c>, checked again here): take a conversation over
/// (the bot is silenced) and hand it back, answer within the 24-hour window or send an approved template (only while the
/// platform's number serves the agency of the conversation, ADR-0067), link a person (<c>customers.read</c>) or a rental
/// file (<c>bookings.read</c>) of the agency of the conversation, mark as read, close and reopen. Takeovers, hand-backs,
/// links and agent messages are written to the audit log (never the content of a message); the conversation row itself
/// is audited with its personal data masked.
/// </summary>
public sealed class WhatsAppInboxService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext user,
    IAuditTrail auditTrail,
    ICustomersQueries customers,
    IBookingsQueries bookings,
    ISettingsReader settings,
    IWhatsAppClient client,
    IWhatsAppNumber whatsAppNumber,
    TimeProvider clock)
{
    /// <summary>
    /// Takes the conversation over: the bot is silenced at once, including a turn in progress (its customer messages are
    /// marked silenced, so that its reply is dropped even if the conversation is handed back before it comes) and its
    /// replies not sent yet (cancelled; one already being sent still leaves).
    /// </summary>
    public Task<Result> TakeOverAsync(Guid conversationId, CancellationToken cancellationToken) =>
        ChangeAsync(
            conversationId,
            async (conversation, actor, now, ct) =>
            {
                var taken = conversation.TakeOver(actor, now);
                if (taken.IsSuccess)
                {
                    await SilenceBotAsync(conversation.Id, now, ct);
                    await AuditAsync(MessagingAuditActions.ConversationTakenOver, conversation, "taken over by an agent: the bot is silenced", ct);
                }

                return taken;
            },
            cancellationToken);

    public Task<Result> HandBackToBotAsync(Guid conversationId, CancellationToken cancellationToken) =>
        ChangeAsync(
            conversationId,
            async (conversation, _, now, ct) =>
            {
                var wasHuman = conversation.Mode == ConversationMode.Human;
                var handedBack = conversation.HandBackToBot(now);
                if (handedBack.IsSuccess && wasHuman)
                {
                    await AuditAsync(MessagingAuditActions.ConversationHandedBack, conversation, "handed back to the bot", ct);
                }

                return handedBack;
            },
            cancellationToken);

    /// <summary>A free-form text answer (taken over, within the window); the customer's messages count as read.</summary>
    public async Task<Result> SendTextAsync(Guid conversationId, string? text, CancellationToken cancellationToken)
    {
        var content = TextContent.Create(text);
        if (content.IsFailure)
        {
            return content.Error;
        }

        return await SendAsync(conversationId, content.Value, cancellationToken);
    }

    /// <summary>An approved template (taken over, any time: the only message possible outside the 24-hour window).</summary>
    public async Task<Result> SendTemplateAsync(
        Guid conversationId,
        InboxTemplateKind kind,
        IReadOnlyList<string?> parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Enum.IsDefined(kind))
        {
            return MessagingErrors.TemplateNotConfigured;
        }

        var name = await settings.GetAsync(InboxTemplates.SettingOf(kind), cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(name))
        {
            return MessagingErrors.TemplateNotConfigured;
        }

        if (parameters.Count != InboxTemplates.ParameterCount(kind))
        {
            return MessagingErrors.TemplateParameterInvalid;
        }

        var language = await settings.GetAsync(MessagingSettings.TemplateLanguage, cancellationToken: cancellationToken);
        var content = TemplateContent.Create(name, language, parameters);
        if (content.IsFailure)
        {
            return content.Error;
        }

        return await SendAsync(conversationId, content.Value, cancellationToken);
    }

    /// <summary>
    /// Links the conversation to a person of the agency of the conversation (and its individual payer), or unlinks it
    /// (<see langword="null"/>): a person of another agency is not found (ADR-0067 — also for the support working across
    /// agencies).
    /// </summary>
    public async Task<Result> LinkPersonAsync(Guid conversationId, Guid? personId, CancellationToken cancellationToken)
    {
        if (!user.HasPermission(Permissions.CustomersRead))
        {
            return MessagingErrors.Forbidden;
        }

        return await ChangeAsync(
            conversationId,
            async (conversation, _, _, ct) =>
            {
                Guid? customerId = null;
                if (personId is { } id)
                {
                    if (await customers.GetPersonAsync(id, ct) is not { IsAnonymized: false } person || person.AgencyId != conversation.AgencyId)
                    {
                        return MessagingErrors.PersonNotFound;
                    }

                    customerId = (await customers.FindIndividualCustomerAsync(id, ct))?.Id;
                }

                conversation.LinkPerson(personId, customerId);
                await AuditAsync(MessagingAuditActions.ConversationLinked, conversation, personId is { } linked ? $"person {linked}" : "person unlinked", ct);
                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>
    /// Links the conversation to the rental file of <paramref name="bookingNumber"/> in the agency of the conversation
    /// (numbers are unique within an agency only, ADR-0067 — also for the support working across agencies), or unlinks
    /// it (blank).
    /// </summary>
    public async Task<Result> LinkBookingAsync(Guid conversationId, string? bookingNumber, CancellationToken cancellationToken)
    {
        if (!user.HasPermission(Permissions.BookingsRead))
        {
            return MessagingErrors.Forbidden;
        }

        return await ChangeAsync(
            conversationId,
            async (conversation, _, _, ct) =>
            {
                Guid? bookingId = null;
                if (!string.IsNullOrWhiteSpace(bookingNumber))
                {
                    var booking = await bookings.FindByNumberAsync(conversation.AgencyId, bookingNumber, ct);
                    if (booking is null || booking.AgencyId != conversation.AgencyId)
                    {
                        return MessagingErrors.BookingNotFound;
                    }

                    bookingId = booking.Id;
                }

                conversation.LinkBooking(bookingId);
                await AuditAsync(MessagingAuditActions.ConversationLinked, conversation, bookingId is { } linked ? $"rental file {linked}" : "rental file unlinked", ct);
                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>The agent read the customer's messages (a concurrent change is reported but harmless: the next refresh retries).</summary>
    public Task<Result> MarkReadAsync(Guid conversationId, CancellationToken cancellationToken) =>
        ChangeAsync(
            conversationId,
            (conversation, _, now, _) =>
            {
                if (conversation.UnreadCount > 0)
                {
                    conversation.MarkRead(now);
                }

                return Task.FromResult(Result.Success());
            },
            cancellationToken);

    public Task<Result> CloseAsync(Guid conversationId, CancellationToken cancellationToken) =>
        ChangeAsync(
            conversationId,
            (conversation, _, now, _) =>
            {
                conversation.Close(now);
                return Task.FromResult(Result.Success());
            },
            cancellationToken);

    public Task<Result> ReopenAsync(Guid conversationId, CancellationToken cancellationToken) =>
        ChangeAsync(
            conversationId,
            (conversation, _, _, _) =>
            {
                conversation.Reopen();
                return Task.FromResult(Result.Success());
            },
            cancellationToken);

    private async Task<Result> SendAsync(Guid conversationId, MessageContent content, CancellationToken cancellationToken)
    {
        if (!client.IsEnabled)
        {
            return MessagingErrors.Disabled;
        }

        return await ChangeAsync(
            conversationId,
            async (conversation, actor, now, ct) =>
            {
                if (!await whatsAppNumber.ServesAsync(conversation.AgencyId, ct))
                {
                    // The message would leave from the number of another agency, and the customer's reply would reach it.
                    return MessagingErrors.NumberNotServed;
                }

                var message = conversation.ReplyAsAgent(content, actor, now);
                if (message.IsFailure)
                {
                    return message.Error;
                }

                db.Set<Message>().Add(message.Value);
                await AuditAsync(
                    MessagingAuditActions.MessageSentByAgent,
                    conversation,
                    content is TemplateContent template ? $"template {template.Name} ({template.Language})" : content.Kind == MessageKind.Text ? "text" : "interactive message",
                    ct);
                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>
    /// Loads the conversation and applies <paramref name="change"/> in a unit of work, as the current user (an agent of the
    /// back office). A customer message recorded at the same instant changes the same row: the change is replayed once on
    /// fresh data (its rules — mode, window — checked again); a second conflict becomes
    /// <see cref="MessagingErrors.ConcurrentUpdate"/>.
    /// </summary>
    private async Task<Result> ChangeAsync(
        Guid conversationId,
        Func<Conversation, AuditActor, DateTimeOffset, CancellationToken, Task<Result>> change,
        CancellationToken cancellationToken)
    {
        if (!user.HasPermission(Permissions.WhatsAppInbox) || user.UserId is null)
        {
            return MessagingErrors.Forbidden;
        }

        var actor = user.ToAuditActor();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await unitOfWork.ExecuteAsync(
                    async ct =>
                    {
                        var conversation = await db.Set<Conversation>().FirstOrDefaultAsync(c => c.Id == conversationId, ct);
                        return conversation is null
                            ? MessagingErrors.ConversationNotFound
                            : await change(conversation, actor, clock.GetUtcNow(), ct);
                    },
                    cancellationToken: cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                // The unit of work cleared its change tracker: the next attempt reloads the conversation.
            }
            catch (DbUpdateConcurrencyException)
            {
                return MessagingErrors.ConcurrentUpdate;
            }
        }
    }

    /// <summary>Ends the pending bot turns of the conversation (silenced) and cancels the bot replies still queued.</summary>
    private async Task SilenceBotAsync(Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var messages = await db.Set<Message>()
            .Where(m => m.ConversationId == conversationId
                        && ((m.Direction == MessageDirection.Inbound && m.BotTurn == BotTurnState.Pending)
                            || (m.Direction == MessageDirection.Outbound && m.Author == MessageAuthor.Bot && m.Status == MessageStatus.Queued)))
            .ToListAsync(cancellationToken);
        foreach (var message in messages)
        {
            if (message.IsInbound)
            {
                message.CompleteBotTurn(BotTurnState.Silenced);
            }
            else
            {
                message.Cancel(MessagingErrors.BotSilenced, now);
            }
        }
    }

    private Task AuditAsync(string action, Conversation conversation, string details, CancellationToken cancellationToken) =>
        auditTrail.RecordAsync(
            new AuditEvent(action)
            {
                EntityType = nameof(Conversation),
                EntityId = conversation.Id.ToString(),
                Details = string.Create(CultureInfo.InvariantCulture, $"conversation {conversation.Id}: {details}"),
                AgencyId = conversation.AgencyId,
            },
            cancellationToken);
}
