using CarRental.Application.Common.Caching;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Inbox;

/// <summary>
/// Tells the open inbox screens that conversations or messages changed — a customer message, a status from Meta, a
/// reply, a takeover — once the change is committed, whoever wrote it (webhook processing, bot, notification, agent),
/// so that they refresh at once instead of waiting for their periodic refresh.
/// </summary>
/// <remarks>In-process only (one App Service instance, ADR-0004): the screens still refresh periodically as a fallback.</remarks>
public interface IWhatsAppInboxActivity
{
    /// <summary>Raised on a background thread after a commit that changed the WhatsApp data; handlers must return fast.</summary>
    event EventHandler? Changed;
}

internal sealed partial class WhatsAppInboxActivity(ILogger<WhatsAppInboxActivity> logger) : IWhatsAppInboxActivity, ICommittedChangesListener
{
    private static readonly Type[] Watched = [typeof(Conversation), typeof(Message), typeof(MessageStatusChange)];

    public event EventHandler? Changed;

    public void OnCommitted(IReadOnlySet<Type> changedEntityTypes)
    {
        ArgumentNullException.ThrowIfNull(changedEntityTypes);
        if (!Array.Exists(Watched, changedEntityTypes.Contains) || Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A screen that fails to refresh must neither fail the commit nor the other screens.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogHandlerFailed(exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A WhatsApp inbox screen failed to handle a change notification.")]
    private partial void LogHandlerFailed(Exception exception);
}
