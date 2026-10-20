using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Common.Outbox;

/// <summary>A message type known to the outbox processor (used to deserialize stored messages).</summary>
public sealed record OutboxMessageTypeRegistration(Type MessageType);

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> for <typeparamref name="TMessage"/> (scoped) and declares the message type
    /// to the outbox processor. Several handlers may be registered for the same message.
    /// </summary>
    public static IServiceCollection AddOutboxHandler<TMessage, THandler>(this IServiceCollection services)
        where TMessage : class
        where THandler : class, IOutboxHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOutboxHandler<TMessage>, THandler>());
        services.AddSingleton(new OutboxMessageTypeRegistration(typeof(TMessage)));
        return services;
    }
}
