using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Notifications;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging.Inbox;

/// <summary>
/// Reads of the shared WhatsApp inbox (<c>/admin/whatsapp</c>, permission <c>whatsapp.inbox</c>): the conversation list
/// of the agency of the operation with its filters, a conversation with its thread, links and bot state, and the
/// templates an agent can send. Names of persons and numbers of files come from the Customers and Bookings contracts
/// (files of the agency of the conversation only).
/// </summary>
public sealed class WhatsAppInboxQueries(
    IAppDbContext db,
    IUserContext user,
    ICustomersQueries customers,
    IBookingsQueries bookings,
    ISettingsReader settings,
    IWhatsAppClient client,
    IIdentityQueries identity,
    TimeProvider clock)
{
    /// <summary>Messages of the thread shown at once (the most recent ones).</summary>
    public const int ThreadSize = 200;

    private const int MaxSuggestedBookings = 5;

    public async Task<Result<PagedResult<InboxConversationItem>>> ListAsync(InboxFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!user.HasPermission(Permissions.WhatsAppInbox))
        {
            return MessagingErrors.Forbidden;
        }

        var query = Filter(db.Set<Conversation>().AsNoTracking(), filter);
        var total = await query.CountAsync(cancellationToken);
        var conversations = await query
            .OrderByDescending(c => c.LastActivityAtUtc)
            .ThenBy(c => c.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        var ids = conversations.Select(c => c.Id).ToList();
        var lastMessages = (await db.Set<Message>()
                .AsNoTracking()
                .Where(m => ids.Contains(m.ConversationId))
                .GroupBy(m => m.ConversationId)
                .Select(g => g
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .ThenByDescending(m => m.Id)
                    .Select(m => new { m.ConversationId, m.Direction, m.Author, m.Kind, m.Text, m.Status })
                    .First())
                .ToListAsync(cancellationToken))
            .ToDictionary(m => m.ConversationId);

        var names = new Dictionary<Guid, string>();
        foreach (var personId in conversations.Where(c => c.PersonId is not null).Select(c => c.PersonId!.Value).Distinct())
        {
            names[personId] = (await customers.GetPersonAsync(personId, cancellationToken))?.FullName ?? string.Empty;
        }

        var numbers = new Dictionary<Guid, string>();
        foreach (var conversation in conversations.Where(c => c.BookingId is not null).DistinctBy(c => c.BookingId))
        {
            if (await bookings.GetSummaryAsync(conversation.BookingId!.Value, cancellationToken) is { } booking && booking.AgencyId == conversation.AgencyId)
            {
                numbers[booking.Id] = booking.Number;
            }
        }

        var agents = await DisplayNamesAsync(conversations.Select(c => c.HumanAgentId), cancellationToken);
        var items = conversations.Select(c =>
        {
            var last = lastMessages.GetValueOrDefault(c.Id);
            return new InboxConversationItem(
                c.Id,
                c.WhatsAppNumber,
                c.ProfileName,
                c.PersonId,
                c.PersonId is { } personId ? names.GetValueOrDefault(personId) : null,
                c.BookingId,
                c.BookingId is { } bookingId ? numbers.GetValueOrDefault(bookingId) : null,
                c.Mode.ToInfo(),
                c.Status == ConversationStatus.Open,
                c.Mode == ConversationMode.Human && c.HumanAgentId is null,
                AgentName(agents, c.HumanAgentId, c.HumanAgentName),
                c.UnreadCount,
                c.LastActivityAtUtc,
                c.WindowClosesAtUtc,
                last is null
                    ? null
                    : new InboxMessagePreview(last.Direction.ToInfo(), last.Author.ToInfo(), last.Kind.ToInfo(), Preview(last.Text), last.Status.ToInfo()));
        }).ToList();
        return new PagedResult<InboxConversationItem>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<InboxConversationDetails>> GetAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        if (!user.HasPermission(Permissions.WhatsAppInbox))
        {
            return MessagingErrors.Forbidden;
        }

        var conversation = await db.Set<Conversation>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            return MessagingErrors.ConversationNotFound;
        }

        var latest = await db.Set<Message>()
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(ThreadSize + 1)
            .ToListAsync(cancellationToken);
        var hasEarlier = latest.Count > ThreadSize;
        var shown = latest.Take(ThreadSize).Reverse().ToList();
        var names = await DisplayNamesAsync(shown.Select(m => m.AuthorUserId).Append(conversation.HumanAgentId), cancellationToken);
        var thread = shown.Select(m => ToInboxMessage(m, names)).ToList();

        InboxLinkedPerson? person = null;
        IReadOnlyList<InboxPersonCandidate> candidates = [];
        IReadOnlyList<InboxLinkedBooking> suggested = [];
        var canReadCustomers = user.HasPermission(Permissions.CustomersRead);
        if (conversation.PersonId is { } personId && canReadCustomers)
        {
            var info = await customers.GetPersonAsync(personId, cancellationToken);
            person = new InboxLinkedPerson(
                personId,
                info?.FullName ?? string.Empty,
                info?.Phone,
                info is null || info.IsAnonymized,
                info is { IsAnonymized: false } && await customers.HasConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, cancellationToken));
            if (user.HasPermission(Permissions.BookingsRead))
            {
                suggested =
                [
                    .. (await bookings.GetBookingsOfPersonAsync(personId, cancellationToken))
                        .Where(b => b.Booking.Id != conversation.BookingId && b.Booking.AgencyId == conversation.AgencyId)
                        .Take(MaxSuggestedBookings)
                        .Select(b => ToLinked(b.Booking)),
                ];
            }
        }
        else if (conversation.PersonId is null && canReadCustomers)
        {
            candidates =
            [
                .. (await customers.FindPersonsByPhoneAsync(conversation.WhatsAppNumber, cancellationToken))
                    .Select(p => new InboxPersonCandidate(p.Id, p.FullName, p.BirthDate)),
            ];
        }

        InboxLinkedBooking? booking = null;
        if (conversation.BookingId is { } bookingId && user.HasPermission(Permissions.BookingsRead)
            && await bookings.GetSummaryAsync(bookingId, cancellationToken) is { } summary && summary.AgencyId == conversation.AgencyId)
        {
            booking = ToLinked(summary);
        }

        return new InboxConversationDetails(
            conversation.Id,
            conversation.WhatsAppNumber,
            conversation.ProfileName,
            conversation.Mode.ToInfo(),
            conversation.Status == ConversationStatus.Open,
            conversation.HumanSinceUtc,
            conversation.HumanAgentId,
            AgentName(names, conversation.HumanAgentId, conversation.HumanAgentName),
            conversation.HandoverReason,
            conversation.UnreadCount,
            conversation.CreatedAtUtc,
            conversation.LastActivityAtUtc,
            conversation.LastCustomerMessageAtUtc,
            conversation.WindowClosesAtUtc,
            conversation.IsWindowOpen(clock.GetUtcNow()),
            person,
            candidates,
            booking,
            suggested,
            conversation.Need.ToInfo(),
            conversation.Usage.ToInfo(),
            thread,
            hasEarlier);
    }

    /// <summary>Whether WhatsApp is configured (messages can be sent) and the templates an agent can send.</summary>
    public async Task<Result<InboxOverview>> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var templates = await GetTemplatesAsync(cancellationToken);
        return templates.IsSuccess ? new InboxOverview(client.IsEnabled, templates.Value) : templates.Error;
    }

    /// <summary>The templates an agent can send (those with a configured name), the follow-up first.</summary>
    public async Task<Result<IReadOnlyList<InboxTemplate>>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        if (!user.HasPermission(Permissions.WhatsAppInbox))
        {
            return MessagingErrors.Forbidden;
        }

        var language = await settings.GetAsync(MessagingSettings.TemplateLanguage, cancellationToken: cancellationToken);
        var templates = new List<InboxTemplate>();
        foreach (var kind in Enum.GetValues<InboxTemplateKind>())
        {
            var name = await settings.GetAsync(InboxTemplates.SettingOf(kind), cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                templates.Add(new InboxTemplate(kind, name, language, InboxTemplates.ParameterCount(kind)));
            }
        }

        return templates;
    }

    private static IQueryable<Conversation> Filter(IQueryable<Conversation> query, InboxFilter filter)
    {
        query = filter.View switch
        {
            InboxView.Unread => query.Where(c => c.Status == ConversationStatus.Open && c.UnreadCount > 0),
            InboxView.Human => query.Where(c => c.Status == ConversationStatus.Open && c.Mode == ConversationMode.Human),
            InboxView.Bot => query.Where(c => c.Status == ConversationStatus.Open && c.Mode == ConversationMode.Bot),
            InboxView.Closed => query.Where(c => c.Status == ConversationStatus.Closed),
            InboxView.All => query,
            _ => query.Where(c => c.Status == ConversationStatus.Open),
        };

        var search = filter.Search?.Trim();
        if (string.IsNullOrEmpty(search))
        {
            return query;
        }

        var digits = new string([.. search.Where(char.IsAsciiDigit)]);
        return digits.Length >= 3 && digits.Length * 2 >= search.Length
            ? query.Where(c => c.WhatsAppNumber.Contains(digits))
            : query.Where(c => c.ProfileName != null && c.ProfileName.Contains(search));
    }

    /// <summary>The display name of a user of the back office (Identity module), else the name recorded with the action.</summary>
    private static string? AgentName(IReadOnlyDictionary<Guid, string> names, Guid? userId, string? recorded) =>
        userId is { } id && names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name : recorded;

    private async Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IEnumerable<Guid?> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.OfType<Guid>().Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<Guid, string>() : await identity.GetDisplayNamesAsync(ids, cancellationToken);
    }

    private static string? Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = string.Join(' ', text.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= InboxMessagePreview.MaxLength ? line : string.Concat(line.AsSpan(0, InboxMessagePreview.MaxLength - 1), "…");
    }

    private static InboxLinkedBooking ToLinked(BookingSummaryInfo booking) =>
        new(booking.Id, booking.Number, booking.Status, booking.PickupAt, booking.ReturnAt);

    private static InboxMessage ToInboxMessage(Message message, IReadOnlyDictionary<Guid, string> names)
    {
        IReadOnlyList<string> choices = message.Content switch
        {
            ButtonsContent buttons => [.. buttons.Buttons.Select(b => b.Title)],
            ListContent list => [.. list.Sections.SelectMany(s => s.Rows).Select(r => r.Title)],
            _ => [],
        };
        var template = message.Content as TemplateContent;
        return new InboxMessage(
            message.Id,
            message.Direction.ToInfo(),
            message.Author.ToInfo(),
            AgentName(names, message.AuthorUserId, message.AuthorName),
            message.Kind.ToInfo(),
            message.Text,
            choices,
            (message.Content as ListContent)?.ButtonText,
            (message.Content as ReplyContent)?.ReplyId,
            (message.Content as MediaContent)?.MediaKind.ToInfo(),
            (message.Content as UnsupportedContent)?.OriginalType,
            template?.Name,
            template?.Language,
            message.Status.ToInfo(),
            message.OccurredAtUtc,
            message.DeliveredAtUtc,
            message.ReadAtUtc,
            message.FailureCode,
            message.MetaErrorCode,
            message.MetaErrorTitle,
            message.PricingCategory,
            message.IsBillable);
    }
}

/// <summary>The settings and parameter counts of the templates an agent can send (ADR-0048).</summary>
public static class InboxTemplates
{
    public static SettingDefinition<string> SettingOf(InboxTemplateKind kind) => kind switch
    {
        InboxTemplateKind.FollowUp => MessagingSettings.FollowUpTemplate,
        InboxTemplateKind.BookingConfirmed => MessagingSettings.BookingConfirmedTemplate,
        InboxTemplateKind.PaymentLink => MessagingSettings.PaymentLinkTemplate,
        InboxTemplateKind.PickupReminder => MessagingSettings.PickupReminderTemplate,
        InboxTemplateKind.ReturnReminder => MessagingSettings.ReturnReminderTemplate,
        InboxTemplateKind.ReviewRequest => MessagingSettings.ReviewRequestTemplate,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown template."),
    };

    /// <summary>Body parameters of each template, as filled by <see cref="BookingNotifier"/> and documented in ADR-0048.</summary>
    public static int ParameterCount(InboxTemplateKind kind) => kind switch
    {
        InboxTemplateKind.FollowUp => 1,
        InboxTemplateKind.BookingConfirmed or InboxTemplateKind.PaymentLink => 4,
        InboxTemplateKind.PickupReminder or InboxTemplateKind.ReturnReminder => 5,
        InboxTemplateKind.ReviewRequest => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown template."),
    };
}
