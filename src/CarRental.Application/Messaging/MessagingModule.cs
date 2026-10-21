using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Caching;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Inbox;
using CarRental.Application.Messaging.Notifications;
using CarRental.Application.Messaging.Responder;
using CarRental.Application.Messaging.Retention;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Messaging;

/// <summary>
/// Messaging module: WhatsApp conversations and messages (webhook events processed through the outbox, 24-hour window,
/// bot or human mode), the auto-responder extension point, sending through <see cref="IWhatsAppClient"/>, booking
/// notifications by approved templates with consent, the shared inbox and the retention purge (ADR-0047, ADR-0048).
/// The WhatsApp client, the webhook parser and the fake are adapters of the infrastructure.
/// </summary>
public static class MessagingModule
{
    public static IServiceCollection AddMessagingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Contracts for the other modules, and the extension point of the auto-responder (package 4B replaces it here).
        services.AddScoped<IMessagingQueries, MessagingQueries>();
        services.TryAddScoped<IConversationResponder, NoReplyResponder>();

        // Pipeline: webhook deliveries → events → bot turns → sending, each step in a scope of its own.
        services.AddSingleton<MessagingScopes>();
        services.AddScoped<WhatsAppWebhookInbox>();
        services.AddScoped<WhatsAppEventRecorder>();
        services.AddScoped<ConversationTurns>();
        services.AddScoped<OutboundMessageSender>();
        services.AddOutboxHandler<WhatsAppWebhookReceived, WhatsAppWebhookHandler>();
        services.AddOutboxHandler<CustomerMessageReceived, ConversationTurnHandler>();
        services.AddOutboxHandler<OutboundMessageQueued, OutboundMessageQueuedHandler>();

        // Booking notifications (approved templates, consent, once per file and kind).
        services.AddScoped<BookingNotifier>();
        services.AddScoped<BookingReminderSweep>();
        services.AddOutboxHandler<BookingStatusChanged, BookingStatusNotificationHandler>();
        services.AddRecurringJob<PickupReminderJob>();
        services.AddRecurringJob<ReturnReminderJob>();
        services.AddRecurringJob<ReviewRequestJob>();

        // Shared inbox.
        services.AddScoped<WhatsAppInboxQueries>();
        services.AddScoped<WhatsAppInboxService>();
        services.AddSingleton<WhatsAppInboxActivity>();
        services.AddSingleton<IWhatsAppInboxActivity>(provider => provider.GetRequiredService<WhatsAppInboxActivity>());
        services.AddSingleton<ICommittedChangesListener>(provider => provider.GetRequiredService<WhatsAppInboxActivity>());

        services.AddRecurringJob<ConversationRetentionJob>();
        services.AddSettingDefinitions(MessagingSettings.All);
        return services;
    }
}
