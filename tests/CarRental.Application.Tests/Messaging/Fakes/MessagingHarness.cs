using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Messaging.Fakes;

/// <summary>
/// The Messaging module wired on test doubles (<c>AddMessagingModule()</c> with in-memory data shared by every scope),
/// with a small outbox: <see cref="DrainAsync"/> hands the events published by the saves to their handlers, in order,
/// like the outbox processor (each handler opens its own scopes, as in production).
/// </summary>
internal sealed class MessagingHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2027, 7, 1, 8, 0, 0, TimeSpan.Zero);

    /// <param name="configure">Last changes to the services (the bot turn tests defer the turns to the runner).</param>
    public MessagingHarness(Action<IServiceCollection>? configure = null)
    {
        AgencyId = Guid.CreateVersion7();
        OfficeId = Guid.CreateVersion7();
        Referential = new FakeAddressReferential(AgencyId, OfficeId);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<ScopedUserContext>();
        services.AddScoped<IUserContext>(provider => provider.GetRequiredService<ScopedUserContext>());
        services.AddMessagingModule();
        services.AddSingleton<IAppDbContext>(Db);
        services.AddScoped<IUnitOfWork, SavingUnitOfWork>();
        services.AddSingleton<IOutbox>(Outbox);
        services.AddSingleton<IAuditTrail>(Audit);
        services.AddSingleton<ICustomersQueries>(Customers);
        services.AddSingleton<IBookingsQueries>(Bookings);
        services.AddSingleton<IReferentialQueries>(Referential);
        services.AddSingleton<ISettingsReader>(Settings);
        services.AddSingleton<IWhatsAppClient>(WhatsApp);
        services.AddSingleton<IWhatsAppWebhookParser>(Parser);
        services.AddSingleton<IIdentityQueries>(Identity);
        services.Replace(ServiceDescriptor.Scoped<IConversationResponder>(_ => Responder));
        configure?.Invoke(services);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>The clock of the tests: each reading moves it a millisecond forward, so that what happens later is later.</summary>
    public FakeTimeProvider Clock { get; } = new(Start) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };

    public InMemoryAppDbContext Db { get; } = new();

    public RecordingOutbox Outbox { get; } = new();

    public RecordingAuditTrail Audit { get; } = new();

    public FakeCustomers Customers { get; } = new();

    public FakeMessagingBookings Bookings { get; } = new();

    public FakeAddressReferential Referential { get; }

    public FakeSettingsReader Settings { get; } = new();

    public RecordingWhatsAppClient WhatsApp { get; } = new();

    public FakeWebhookParser Parser { get; } = new();

    public ScriptedResponder Responder { get; } = new();

    public FakeIdentityQueries Identity { get; } = new();

    public ServiceProvider Services { get; }

    public Guid AgencyId { get; }

    public Guid OfficeId { get; }

    public List<Conversation> Conversations => Db.Rows<Conversation>();

    public List<Message> Messages => Db.Rows<Message>();

    /// <summary>A scope acting as <paramref name="actor"/> (the system actor by default).</summary>
    public AsyncServiceScope Scope(UserContextSnapshot? actor = null)
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(actor ?? UserContextSnapshot.System("system:tests"));
        return scope;
    }

    /// <summary>An agent of the back office (whatsapp.inbox, customers.read, bookings.read).</summary>
    public static UserContextSnapshot Agent(Guid? id = null) =>
        UserContextSnapshot.ForUser(id ?? Guid.CreateVersion7(), "agent@demo.local", RolePresets.PermissionsOf(Roles.Agent));

    /// <summary>A delivery holding customer messages and statuses, processed like the outbox would (then drained).</summary>
    public async Task DeliverAsync(IReadOnlyList<InboundWhatsAppMessage>? messages = null, IReadOnlyList<WhatsAppStatusReport>? statuses = null)
    {
        var payload = $"payload-{Guid.NewGuid():N}";
        Parser.Batches[payload] = new WhatsAppWebhookBatch(messages ?? [], statuses ?? [], 0);
        await HandleAsync(new WhatsAppWebhookReceived(payload, Clock.GetUtcNow()));
        await DrainAsync();
    }

    /// <summary>A text from <paramref name="from"/> (a <c>wa_id</c>), sent now; returns its Meta id.</summary>
    public async Task<string> CustomerWritesAsync(string from, string text, string? profileName = "Hédi")
    {
        var id = $"wamid.{Guid.NewGuid():N}";
        await DeliverAsync([new InboundWhatsAppMessage(id, from, profileName, Clock.GetUtcNow(), TextContent.Received(text))]);
        return id;
    }

    /// <summary>Runs the handlers of every event published by the saves so far, then of the events they publish, until none is left.</summary>
    public async Task DrainAsync()
    {
        for (var round = 0; round < 50; round++)
        {
            var events = Db.PublishedEvents.ToList();
            var messages = Outbox.Messages.ToList();
            Db.PublishedEvents.Clear();
            Outbox.Messages.Clear();
            if (events.Count == 0 && messages.Count == 0)
            {
                return;
            }

            foreach (var item in events.Cast<object>().Concat(messages))
            {
                await HandleAsync(item);
            }
        }

        throw new InvalidOperationException("The events never stop: a handler loops.");
    }

    public Task HandleAsync(object message, int attempt = 1) => DispatchAsync(message, attempt, CancellationToken.None);

    /// <summary>Runs the outbox handlers of <paramref name="message"/> with the token of the outbox pump (a shutdown cancels it).</summary>
    public Task HandleUntilShutdownAsync(object message, CancellationToken pumpToken) => DispatchAsync(message, 1, pumpToken);

    private async Task DispatchAsync(object message, int attempt, CancellationToken pumpToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var context = new OutboxMessageContext(Guid.CreateVersion7(), Clock.GetUtcNow(), attempt, "corr-tests");
        await using var scope = Scope();
        var handlerType = typeof(IOutboxHandler<>).MakeGenericType(message.GetType());
        foreach (var handler in scope.ServiceProvider.GetServices(handlerType))
        {
            await (Task)handlerType.GetMethod(nameof(IOutboxHandler<object>.HandleAsync))!.Invoke(handler, [message, context, pumpToken])!;
        }
    }

    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> operation, UserContextSnapshot? actor = null)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = Scope(actor);
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>A person with this phone (and the WhatsApp notifications consent when <paramref name="consent"/>).</summary>
    public Guid AddPerson(string phone, bool consent = false, string firstName = "Hédi", string lastName = "Ben Salah")
    {
        var id = Customers.AddPerson(firstName, lastName);
        Customers.Persons[id] = Customers.Persons[id] with { Phone = phone };
        if (consent)
        {
            Customers.Consents.Add((id, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Web, true));
        }

        return id;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

/// <summary>An agency with an office that has an address (reminders carry it).</summary>
internal sealed class FakeAddressReferential(Guid agencyId, Guid officeId) : IReferentialQueries
{
    public const string OfficeAddress = "12 avenue Habib Bourguiba, Tunis";

    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>([new AgencyInfo(agencyId, "Agence Démo Tunis", "Tunis", "+21671000000", null, true, "Rue de l'agence, Tunis")]);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationInfo>>([new LocationInfo(officeId, agencyId ?? Guid.Empty, "AGC", "Agence Tunis centre", LocationKindInfo.AgencyOffice, OfficeAddress)]);

    public Task<LocationInfo?> FindLocationByCodeAsync(Guid agencyId, string code, CancellationToken cancellationToken = default) => Task.FromResult<LocationInfo?>(null);
}
