using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Messaging;

/// <summary>Messaging module: WhatsApp conversations, AI agent and message templates. Built in Phase 4; registration is empty until then.</summary>
public static class MessagingModule
{
    public static IServiceCollection AddMessagingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
