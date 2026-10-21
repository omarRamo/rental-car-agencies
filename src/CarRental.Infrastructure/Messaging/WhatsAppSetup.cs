using CarRental.Application.Messaging.Sending;
using CarRental.Application.Messaging.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Messaging;

public static class WhatsAppSetup
{
    /// <summary>
    /// The WhatsApp adapters: options (validated at startup), webhook checks and parser, the Graph API client (typed
    /// <see cref="HttpClient"/>), the fake, and the <see cref="IWhatsAppClient"/> of the configured mode.
    /// </summary>
    public static IServiceCollection AddWhatsApp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WhatsAppOptions>().Bind(configuration.GetSection(WhatsAppOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<WhatsAppOptions>, WhatsAppOptionsSetup>();
        services.AddSingleton<IValidateOptions<WhatsAppOptions>, WhatsAppOptionsValidator>();

        services.AddSingleton<WhatsAppWebhookSecurity>();
        services.AddSingleton<IWhatsAppWebhookParser, WhatsAppWebhookParser>();
        services.AddSingleton<FakeWhatsApp>();
        services.AddHttpClient<WhatsAppGraphClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<WhatsAppOptions>>().Value;
            client.BaseAddress = options.GraphBaseUrl.AbsoluteUri.EndsWith('/') ? options.GraphBaseUrl : new Uri(options.GraphBaseUrl.AbsoluteUri + "/");
            client.Timeout = options.Timeout;
        });
        services.AddScoped<IWhatsAppClient>(provider => provider.GetRequiredService<IOptions<WhatsAppOptions>>().Value.Mode switch
        {
            WhatsAppMode.Graph => provider.GetRequiredService<WhatsAppGraphClient>(),
            WhatsAppMode.Fake => provider.GetRequiredService<FakeWhatsApp>(),
            _ => DisabledWhatsAppClient.Instance,
        });
        return services;
    }
}
