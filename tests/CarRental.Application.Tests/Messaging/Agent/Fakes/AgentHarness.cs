using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging;
using CarRental.Application.Messaging.Agent;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Application.Tests.Messaging.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Messaging.Agent.Fakes;

/// <summary>
/// The WhatsApp agent wired as in production (<c>AddMessagingModule()</c>: the agent replaces the default responder) on
/// test doubles: the fake engines of <see cref="AgentWorld"/>, in-memory data, the real system prompt (linked from the
/// infrastructure), and the scripted model. <see cref="NewConversation"/> plays a conversation turn after turn the way
/// the platform applies the replies (history, need, usage, hand-over).
/// </summary>
internal sealed class AgentHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2027, 6, 20, 8, 0, 0, TimeSpan.Zero);

    public static readonly Uri PublicSite = new("https://www.example.tn/");

    /// <param name="model">The scripted model of the tests (a new one by default).</param>
    /// <param name="agentModel">Another model behind the port instead (the Claude adapter offline, the real model of the evaluation).</param>
    public AgentHarness(ScriptedModel? model = null, IAgentModel? agentModel = null)
    {
        Model = model ?? new ScriptedModel();
        Availability = new FakeAgentAvailability(World);
        Agencies = new StaticAgencyDirectory(World.Agency.Id);
        Site = CarRental.Domain.Messaging.Agent.BookingDeepLink.SiteOf(PublicSite, Agencies.Set(World.Agency.Id, CarRental.Domain.Common.AgencyModuleCatalog.Every).Slug).AbsoluteUri;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<ScopedUserContext>();
        services.AddScoped<IUserContext>(provider => provider.GetRequiredService<ScopedUserContext>());
        services.AddMessagingModule();
        services.AddSingleton<IAppDbContext>(Db);
        services.AddScoped<IUnitOfWork, SavingUnitOfWork>();
        services.AddSingleton<IOutbox>(new RecordingOutbox());
        services.AddSingleton<IAuditTrail>(Audit);
        services.AddSingleton<ICustomersQueries>(Customers);
        services.AddSingleton<IBookingsQueries>(Bookings);
        services.AddSingleton<IReferentialQueries>(new FakeAgentReferential(World));
        services.AddSingleton<IAgencyDirectory>(Agencies);
        services.AddSingleton<IFleetQueries>(new FakeAgentFleet(World));
        services.AddSingleton<IAvailabilityQueries>(Availability);
        services.AddSingleton<IPricingService>(new FakeAgentPricing(World));
        services.AddSingleton<IBookingRequests>(new FakeAgentBookingRequests(World));
        services.AddSingleton<ISettingsReader>(Settings);
        services.AddSingleton<IIdentityQueries>(new FakeIdentityQueries());
        services.AddSingleton<IWhatsAppClient>(new RecordingWhatsAppClient());
        services.AddSingleton<IWhatsAppWebhookParser>(new FakeWebhookParser());
        services.Replace(ServiceDescriptor.Singleton(agentModel ?? Model));
        services.Replace(ServiceDescriptor.Singleton<IAgentPromptSource>(new RepositoryPromptSource()));
        services.Configure<AgentSiteOptions>(site => site.PublicBaseUrl = PublicSite);
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public FakeTimeProvider Clock { get; } = new(Start) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };

    public AgentWorld World { get; } = new();

    /// <summary>The address of the site of the agency of the world (<c>https://www.example.tn/agence-…/</c>): the base of the bot's links (ADR-0067).</summary>
    public string Site { get; }

    /// <summary>The agencies of the platform: the agency of the world, with every module.</summary>
    public StaticAgencyDirectory Agencies { get; }

    public ScriptedModel Model { get; }

    public InMemoryAppDbContext Db { get; } = new();

    public FakeSettingsReader Settings { get; } = new();

    public FakeCustomers Customers { get; } = new();

    public FakeMessagingBookings Bookings { get; } = new();

    public FakeAgentAvailability Availability { get; }

    public RecordingAuditTrail Audit { get; } = new();

    public ServiceProvider Services { get; }

    public AsyncServiceScope Scope(UserContextSnapshot? actor = null)
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(actor ?? UserContextSnapshot.System("system:tests"));
        return scope;
    }

    public async Task<ConversationReply> RespondAsync(ConversationTurn turn)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<IConversationResponder>().RespondAsync(turn, TestContext.Current.CancellationToken);
    }

    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> operation, UserContextSnapshot? actor = null)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = Scope(actor);
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public AgentConversation NewConversation(string number = "+21698123456") => new(this, number);

    /// <summary>A manager of <paramref name="agencyId"/>.</summary>
    public static UserContextSnapshot Manager(Guid agencyId) =>
        UserContextSnapshot.ForUser(Guid.CreateVersion7(), "gerant@demo.local", RolePresets.PermissionsOf(Roles.Manager)) with { AgencyId = agencyId };

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

/// <summary>
/// A conversation as the platform plays it: each <see cref="SendAsync"/> is a customer turn; the reply is applied like
/// <c>ConversationTurns.ApplyAsync</c> does (message into the history, need replaced, usage added, human mode after a
/// hand-over — the bot is then silent).
/// </summary>
internal sealed class AgentConversation(AgentHarness harness, string number)
{
    private readonly List<ConversationMessageInfo> _history = [];

    public Guid Id { get; } = Guid.CreateVersion7();

    public string Number { get; } = number;

    public ConversationNeedInfo Need { get; private set; } = ConversationNeedInfo.Empty;

    public ConversationUsageInfo Usage { get; private set; } = new(0, 0, 0, 0, 0, 0, 0m, 0m);

    public bool HandedOver { get; private set; }

    public string? HandOverReason { get; private set; }

    public List<ConversationReply> Replies { get; } = [];

    public IReadOnlyList<ConversationMessageInfo> History => _history;

    public Guid? BookingId { get; set; }

    public Task<ConversationReply> SendAsync(params string[] texts) =>
        TurnAsync([.. texts.Select(text => Customer(MessageKindInfo.Text, text))]);

    public Task<ConversationReply> SendMediaAsync(params MediaKindInfo[] kinds) =>
        TurnAsync([.. kinds.Select(kind => Customer(MessageKindInfo.Media, null, mediaKind: kind))]);

    /// <summary>Attachments then a text, in one turn (the customer sends a photo and writes about it).</summary>
    public Task<ConversationReply> SendWithAttachmentsAsync(string text, params MediaKindInfo[] kinds) =>
        TurnAsync([.. kinds.Select(kind => Customer(MessageKindInfo.Media, null, mediaKind: kind)), Customer(MessageKindInfo.Text, text)]);

    public Task<ConversationReply> TapAsync(string replyId, string title) =>
        TurnAsync([Customer(MessageKindInfo.ButtonReply, title, replyId)]);

    public async Task<ConversationReply> TurnAsync(IReadOnlyList<ConversationMessageInfo> messages)
    {
        var now = harness.Clock.GetUtcNow();
        if (HandedOver)
        {
            _history.AddRange(messages);
            return ConversationReply.None;
        }

        var turn = new ConversationTurn(
            Id, harness.World.Agency.Id, messages[^1].Id, Number, "Client", null, null, BookingId, now, now.AddHours(24), Need, Usage, messages, [.. _history.TakeLast(ConversationTurn.MaxHistory)]);
        var reply = await harness.RespondAsync(turn);
        Replies.Add(reply);
        _history.AddRange(messages);
        if (reply.Usage is { } usage)
        {
            Usage = new ConversationUsageInfo(
                Usage.BotTurns + 1,
                Usage.InputTokens + usage.InputTokens,
                Usage.OutputTokens + usage.OutputTokens,
                Usage.CacheReadTokens + usage.CacheReadTokens,
                Usage.CacheWriteTokens + usage.CacheWriteTokens,
                Usage.ToolCalls + usage.ToolCalls,
                Usage.AiCostUsd + usage.AiCostUsd,
                Usage.MetaCostUsd + usage.MetaCostUsd);
        }

        if (reply.Need is { } need)
        {
            Need = need;
        }

        if (reply.Message is { } message)
        {
            _history.Add(new ConversationMessageInfo(
                Guid.CreateVersion7(), MessageDirectionInfo.Outbound, MessageAuthorInfo.Bot, MessageKindInfo.Text, TextOf(message), null, null, null,
                MessageStatusInfo.Delivered, harness.Clock.GetUtcNow()));
        }

        if (reply.HandOverReason is { } reason)
        {
            HandedOver = true;
            HandOverReason = reason;
        }

        return reply;
    }

    public static string? TextOf(OutboundMessageContent? message) => message switch
    {
        OutboundText text => text.Body,
        OutboundButtons buttons => buttons.Body,
        OutboundList list => list.Body,
        _ => null,
    };

    private ConversationMessageInfo Customer(MessageKindInfo kind, string? text, string? replyId = null, MediaKindInfo? mediaKind = null) =>
        new(Guid.CreateVersion7(), MessageDirectionInfo.Inbound, MessageAuthorInfo.Customer, kind, text, replyId, mediaKind, null, MessageStatusInfo.Received, harness.Clock.GetUtcNow());
}

/// <summary>The real system prompt of the agent (files of the infrastructure, linked into this assembly as resources).</summary>
internal sealed class RepositoryPromptSource : IAgentPromptSource
{
    public string SystemPromptTemplate { get; } = Read("whatsapp-agent.md");

    public string Addendum { get; } = Read("whatsapp-agent-addendum.md");

    public static string Read(string file)
    {
        using var stream = typeof(RepositoryPromptSource).Assembly.GetManifestResourceStream($"CarRental.Tests.Prompts.{file}")
            ?? throw new InvalidOperationException($"The prompt {file} is not linked into the test assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
