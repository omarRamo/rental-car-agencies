using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Messaging;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// Data of the WhatsApp tests (they run in parallel on one database): a WhatsApp number of their own, signed webhook
/// deliveries as Meta posts them, and polling of the asynchronous processing (outbox) with a deadline.
/// </summary>
internal static class WhatsAppTestData
{
    public const string WebhookRoute = "/webhooks/whatsapp";

    /// <summary>A French mobile number nobody else uses in the run, as a Meta <c>wa_id</c> (digits only).</summary>
    public static string NewWaId() =>
        "3375" + RandomNumberGenerator.GetInt32(0, 10_000_000).ToString("D7", CultureInfo.InvariantCulture);

    public static string NewMetaId() => $"wamid.TEST{Guid.NewGuid():N}";

    public static UserContextSnapshot Agent(Guid? id = null) =>
        UserContextSnapshot.ForUser(id ?? Guid.CreateVersion7(), $"agent-{Guid.NewGuid():N}@tests.local", RolePresets.PermissionsOf(Roles.Agent));

    public static string Text(string from, string body, string? profileName = "Client test", string? metaId = null, DateTimeOffset? at = null) =>
        MetaWebhookPayloads.TextMessage(
            WhatsAppDevelopmentKeys.BusinessAccountId,
            WhatsAppDevelopmentKeys.PhoneNumberId,
            from,
            profileName,
            metaId ?? NewMetaId(),
            at ?? DateTimeOffset.UtcNow,
            body);

    public static string Status(string metaId, string to, string status, int? errorCode = null) =>
        MetaWebhookPayloads.Status(
            WhatsAppDevelopmentKeys.BusinessAccountId,
            WhatsAppDevelopmentKeys.PhoneNumberId,
            metaId,
            to,
            status,
            DateTimeOffset.UtcNow,
            errorCode: errorCode,
            errorTitle: errorCode is null ? null : "Message undeliverable");

    /// <summary>POSTs <paramref name="payload"/> to the webhook, signed with the app secret of the test host (or <paramref name="signature"/>).</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string payload, CancellationToken cancellationToken, string? signature = null, bool sign = true)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(WebhookRoute, UriKind.Relative)) { Content = content };
        if (sign)
        {
            request.Headers.TryAddWithoutValidation(
                WhatsAppWebhookSecurity.SignatureHeader,
                signature ?? WhatsAppWebhookSecurity.ComputeSignature(body, WhatsAppDevelopmentKeys.AppSecret));
        }

        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Polls <paramref name="read"/> until it returns a value matching <paramref name="done"/> (45 seconds at most).</summary>
    public static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"Timed out waiting for {what}.");
            await Task.Delay(100, cancellationToken);
        }
    }

    public static async Task<Conversation?> ConversationAsync(IntegrationHost host, string waId, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Conversation>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.WhatsAppNumber == "+" + waId, cancellationToken);
    }

    public static async Task<List<Message>> MessagesAsync(IntegrationHost host, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Message>()
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>A first name of letters only, unique enough for a test.</summary>
    public static string NewName()
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        var name = new char[8];
        for (var i = 0; i < name.Length; i++)
        {
            name[i] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
        }

        name[0] = char.ToUpperInvariant(name[0]);
        return new string(name);
    }

    /// <summary>A person with this phone (E.164 or <c>wa_id</c>), created by an agent, with the WhatsApp notifications consent when asked.</summary>
    public static async Task<Guid> NewPersonAsync(IntegrationHost host, string phone, bool consent, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope(Agent());
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var person = await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(NewName(), "Whatsapp", Phone: phone.StartsWith('+') ? phone : "+" + phone),
            cancellationToken);
        person.IsSuccess.ShouldBeTrue(person.IsFailure ? person.Error.ToString() : null);
        if (consent)
        {
            (await registration.SetConsentAsync(person.Value.PersonId, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Web, true, cancellationToken))
                .IsSuccess.ShouldBeTrue();
        }

        return person.Value.PersonId;
    }
}
