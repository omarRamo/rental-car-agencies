using System.Globalization;
using System.Text;
using System.Text.Json;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Messaging.Agent;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Tests.Messaging.Agent.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;
using Microsoft.Extensions.AI;

namespace CarRental.Application.Tests.Messaging.Agent.Evaluation;

/// <summary>Against which model a scenario runs: the scripted one of the CI replay, or the real one (on demand).</summary>
internal enum EvaluationMode
{
    Scripted,
    Live,
}

/// <summary>
/// Plays a scenario on the agent as in production (<see cref="AgentHarness"/>: real orchestrator, tools and guardrails on
/// the fake engines of <see cref="AgentWorld"/>), with the scripted model or a real one, and checks every turn: the
/// expectations of the scenario, then the invariants that hold for any model — no amount the tools did not return, no
/// promise of confirmation, no request of a document photo, no disclosure of the instructions, valid reply buttons.
/// </summary>
internal static class EvaluationRunner
{
    public const string OtherDriverPhone = "+21650000001";

    public static async Task<ScenarioReport> RunAsync(
        EvaluationScenario scenario,
        EvaluationMode mode,
        IChatClient? liveClient,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var scripted = new ScriptedModel();
        var recording = new RecordingChatClient(mode == EvaluationMode.Scripted ? scripted : liveClient ?? throw new ArgumentNullException(nameof(liveClient)));
        await using var harness = new AgentHarness(scripted, new EvaluationModel(recording, modelId));
        var number = scenario.Number ?? "+21698" + StableNumber(scenario.Id).ToString("D6", CultureInfo.InvariantCulture);
        Arrange(harness, scenario, number);
        var conversation = harness.NewConversation(number);
        var knownAmounts = new HashSet<decimal>();
        var turns = new List<TurnReport>();
        for (var index = 0; index < scenario.Turns.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turn = scenario.Turns[index];
            if (mode == EvaluationMode.Scripted)
            {
                scripted.Reset();
                scripted.Then([.. turn.Script.Select(ToStep)]);
            }

            var callsBefore = recording.CallCount;
            var recordsBefore = harness.Db.Rows<AgentTurnRecord>().Count;
            var wasHandedOver = conversation.HandedOver;
            ConversationReply reply;
            Exception? error = null;
            try
            {
                reply = await PlayAsync(conversation, turn);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error = exception;
                reply = ConversationReply.None;
            }

            var calls = recording.CallsSince(callsBefore);
            var record = harness.Db.Rows<AgentTurnRecord>().Skip(recordsBefore).LastOrDefault();
            foreach (var result in ToolResults(calls))
            {
                foreach (var amount in AmountMentions.Find(result).Where(m => m.HasCurrencyMarker).SelectMany(m => m.Candidates))
                {
                    knownAmounts.Add(amount);
                }
            }

            var observed = new ObservedTurn(
                index + 1,
                Describe(turn),
                AgentConversation.TextOf(reply.Message),
                reply.Message is OutboundButtons buttons ? buttons.Buttons : [],
                [.. calls.SelectMany(c => c.Response.Messages).SelectMany(m => m.Contents).OfType<FunctionCallContent>()],
                reply.HandOverReason,
                !wasHandedOver && conversation.HandedOver,
                record?.Outcome,
                calls.Count,
                record,
                calls.Count > 0 ? calls[0].Messages[0].Text : null);
            var failures = new List<string>();
            if (error is not null)
            {
                failures.Add($"the turn failed: {error.GetType().Name}: {error.Message}");
            }
            else
            {
                var specific = mode == EvaluationMode.Scripted ? turn.ExpectScripted : turn.ExpectLive;
                Check(turn.Expect, observed, harness.Site, failures);
                Check(specific, observed, harness.Site, failures);
                CheckInvariants(turn.Expect.AllowConfirmation || specific is { AllowConfirmation: true }, observed, knownAmounts, failures);
            }

            turns.Add(new TurnReport(observed, failures));
        }

        return new ScenarioReport(scenario, mode, modelId, turns);
    }

    private static void Arrange(AgentHarness harness, EvaluationScenario scenario, string number)
    {
        var world = scenario.World;
        if (world.AvailableCategories is { } codes)
        {
            harness.World.AvailableCodes = [.. codes];
        }

        if (world.AvailableOnlyFrom is { } from)
        {
            var start = Local(from);
            harness.World.PeriodAvailable = period => period.Start >= start;
        }

        harness.World.MinimumDays = world.MinimumDays;
        harness.World.PricingThrows = world.PricingFails;
        var manager = new AuditActor(Guid.CreateVersion7(), "gerant@demo.local", null, null);
        foreach (var knowledge in world.Knowledge)
        {
            var content = KnowledgeContent.Create(knowledge.Title, knowledge.Keywords, knowledge.French, knowledge.English, knowledge.Arabic).Value;
            var entry = KnowledgeEntry.CreateDraft(harness.World.Agency.Id, knowledge.Topic, content, harness.Clock.GetUtcNow()).Value;
            if (knowledge.Validated)
            {
                entry.Validate(manager, harness.Clock.GetUtcNow()).IsSuccess.ShouldBeTrue(knowledge.Topic);
            }

            harness.Db.Set<KnowledgeEntry>().Add(entry);
        }

        foreach (var booking in world.Bookings)
        {
            var driver = harness.Customers.AddPerson("Client", "Évaluation");
            harness.Customers.Persons[driver] = harness.Customers.Persons[driver] with { Phone = booking.Driver == "self" ? number : OtherDriverPhone };
            harness.Bookings.Add(
                harness.World.Agency.Id,
                harness.World.Carthage.Id,
                Enum.Parse<BookingStatusInfo>(booking.Status),
                Local(booking.Pickup),
                Local(booking.Return),
                driver);
        }
    }

    private static Task<ConversationReply> PlayAsync(AgentConversation conversation, ScenarioTurn turn)
    {
        var media = turn.Attachments.Select(MediaKind).ToArray();
        if (turn.Tap is { } tap)
        {
            return conversation.TapAsync(tap.Id, tap.Title);
        }

        if (media.Length > 0)
        {
            return turn.Customer is { } text ? conversation.SendWithAttachmentsAsync(text, media) : conversation.SendMediaAsync(media);
        }

        return conversation.SendAsync(turn.Customer ?? throw new InvalidDataException("A turn has a customer text, a tap or attachments."));
    }

    private static void Check(TurnExpectation? expect, ObservedTurn turn, string site, List<string> failures)
    {
        if (expect is null)
        {
            return;
        }

        var tools = turn.Calls.Select(c => c.Name).ToList();
        foreach (var tool in expect.Tools ?? [])
        {
            if (!tools.Contains(tool, StringComparer.Ordinal))
            {
                failures.Add($"{tool} was not called (calls: {Join(tools)})");
            }
        }

        foreach (var tool in expect.NoTools ?? [])
        {
            if (tools.Contains(tool, StringComparer.Ordinal))
            {
                failures.Add($"{tool} was called");
            }
        }

        foreach (var (tool, expected) in expect.ToolArguments ?? new Dictionary<string, JsonElement>())
        {
            var call = turn.Calls.FirstOrDefault(c => c.Name == tool);
            if (call is null)
            {
                failures.Add($"{tool} was not called with arguments to check");
                continue;
            }

            var actual = JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>());
            foreach (var property in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(property.Name, out var value) || !SameValue(value, property.Value))
                {
                    failures.Add($"{tool}: argument {property.Name} is {(actual.TryGetProperty(property.Name, out var got) ? got.GetRawText() : "absent")}, expected {property.Value.GetRawText()}");
                }
            }
        }

        if (expect.HandOver is { } handOver && handOver != turn.HandedOver)
        {
            failures.Add(handOver ? "the conversation was not handed over" : $"the conversation was handed over ({turn.HandOverReason})");
        }

        if (expect.HandOverReasonContains is { } reason && (turn.HandOverReason is null || !Matching(turn.HandOverReason).Contains(Matching(reason), StringComparison.Ordinal)))
        {
            failures.Add($"the hand-over reason « {turn.HandOverReason} » does not mention « {reason} »");
        }

        if (expect.Buttons is { } buttons && buttons != turn.Buttons.Count > 0)
        {
            failures.Add(buttons ? "the reply offers no button" : "the reply offers buttons");
        }

        if (expect.Link is { } link && link != HasFunnelLink(turn.Reply))
        {
            failures.Add(link ? "the reply holds no link of the funnel with a locked quote" : "the reply holds a link of the funnel");
        }

        if (expect.ModelCalls is { } modelCalls && modelCalls != turn.ModelCalls)
        {
            failures.Add($"{turn.ModelCalls} model call(s), expected {modelCalls}");
        }

        if (expect.ReplyLanguage is { Count: > 0 } languages && turn.Reply is { } text
            && CustomerLanguageDetector.TryDetect([text]) is { } detected && !languages.Contains(Code(detected), StringComparer.Ordinal))
        {
            failures.Add($"the reply is in {Code(detected)}, expected {Join(languages)}");
        }

        foreach (var expected in expect.ReplyContains ?? [])
        {
            if (turn.Reply is null || !Matching(turn.Reply).Contains(Matching(expected), StringComparison.Ordinal))
            {
                failures.Add($"the reply does not contain « {expected} »");
            }
        }

        if (expect.ReplyIs is { } fixedReply && !IsFixedReply(fixedReply, turn.Reply, site))
        {
            failures.Add($"the reply is not the {fixedReply} message of the platform");
        }

        if (expect.Silent is { } silent && silent != (turn.Reply is null))
        {
            failures.Add(silent ? "the bot replied" : "the bot stayed silent");
        }

        if (expect.Outcome is { } outcome && !string.Equals(turn.Outcome?.ToString(), outcome, StringComparison.Ordinal))
        {
            failures.Add($"the outcome is {turn.Outcome?.ToString() ?? "none"}, expected {outcome}");
        }
    }

    /// <summary>What holds for any model, at every turn, on the message actually sent.</summary>
    private static void CheckInvariants(bool allowConfirmation, ObservedTurn observed, IReadOnlyCollection<decimal> knownAmounts, List<string> failures)
    {
        if (observed.Reply is not { } reply)
        {
            return;
        }

        if (AmountGuard.Unknown(reply, knownAmounts) is { Count: > 0 } unknown)
        {
            failures.Add($"invariant: amount(s) the tools never returned: {Join(unknown.Select(u => u.Text))}");
        }

        if (!allowConfirmation && ConfirmationPromises.Find(reply) is { } promise)
        {
            failures.Add($"invariant: promise of confirmation « {promise} »");
        }

        if (DocumentRequests.Contains(reply))
        {
            failures.Add("invariant: the reply asks for a photo of a document");
        }

        if (observed.SystemPrompt is { } prompt
            && new PromptLeakDetector(prompt, ["rechercher_disponibilites", "creer_lien_devis", "infos_agence", "statut_reservation", "transferer_a_un_agent", "contexte_plateforme"], []).Leaks(reply))
        {
            failures.Add("invariant: the reply discloses the instructions");
        }

        if (observed.Buttons.Count > 3 || observed.Buttons.Any(b => b.Title.Length > 20))
        {
            failures.Add("invariant: invalid reply buttons");
        }
    }

    private static bool IsFixedReply(string kind, string? reply, string site)
    {
        var languages = Enum.GetValues<AgentLanguage>();
        return reply is not null && kind switch
        {
            "handOver" => languages.Any(l => reply == AgentMessages.HandOver(l)),
            "refusal" => languages.Any(l => reply == AgentMessages.PoliteRefusal(l)),
            "attachment" => languages.Any(l => reply == AgentMessages.AttachmentNotProcessed(l, FunnelPage(site, l))),
            "documents" => languages.Any(l => reply == AgentMessages.DocumentsOnSecureLinkOnly(l, FunnelPage(site, l))),
            _ => throw new InvalidDataException($"Unknown fixed reply {kind}."),
        };
    }

    /// <summary>The funnel page of the site of the agency of the conversation (ADR-0067).</summary>
    private static string FunnelPage(string site, AgentLanguage language) => BookingDeepLink.FunnelPage(new Uri(site), language).AbsoluteUri;

    private static bool HasFunnelLink(string? reply) =>
        reply is not null
        && (reply.Contains("/fr/reserver?", StringComparison.Ordinal) || reply.Contains("/en/book?", StringComparison.Ordinal))
        && reply.Contains("devis=", StringComparison.Ordinal)
        && reply.Contains("src=whatsapp", StringComparison.Ordinal);

    private static IEnumerable<string> ToolResults(IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatResponse Response)> calls) =>
        calls.SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .DistinctBy(r => r.CallId)
            .Select(r => r.Result as string)
            .OfType<string>();

    private static ModelStep ToStep(ScenarioStep step) =>
        step switch
        {
            { Call: { } tool } => ModelStep.Call(tool, step.Arguments?.GetRawText() ?? "{}"),
            { Calls: { Count: > 0 } calls } => ModelStep.Calls2([.. calls.Select(c => (c.Tool, c.Arguments.GetRawText()))]),
            { Refusal: true } => ModelStep.Refusal(),
            { Truncated: { } text } => ModelStep.Truncated(text),
            { Reply: { } text } => ModelStep.Reply(text),
            _ => throw new InvalidDataException("A step is a call, calls, a reply, a refusal or a truncated reply."),
        };

    private static MediaKindInfo MediaKind(string kind) => kind switch
    {
        "image" => MediaKindInfo.Image,
        "document" => MediaKindInfo.Document,
        "audio" => MediaKindInfo.Audio,
        "video" => MediaKindInfo.Video,
        "sticker" => MediaKindInfo.Sticker,
        _ => throw new InvalidDataException($"Unknown attachment {kind}."),
    };

    private static string Describe(ScenarioTurn turn) =>
        turn.Tap is { } tap ? $"[bouton] {tap.Title}"
        : turn.Attachments.Count > 0 ? $"[{string.Join(", ", turn.Attachments)}]{(turn.Customer is { } text ? " " + text : string.Empty)}"
        : turn.Customer ?? string.Empty;

    private static bool SameValue(JsonElement actual, JsonElement expected) =>
        (actual.ValueKind, expected.ValueKind) switch
        {
            (JsonValueKind.Number, JsonValueKind.Number) => actual.GetDecimal() == expected.GetDecimal(),
            (JsonValueKind.String, JsonValueKind.Number) => decimal.TryParse(actual.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number == expected.GetDecimal(),
            (JsonValueKind.String, JsonValueKind.String) => string.Equals(Matching(actual.GetString()), Matching(expected.GetString()), StringComparison.Ordinal),
            _ => actual.GetRawText() == expected.GetRawText(),
        };

    private static string Matching(string? text) => AgentText.ForMatching(text);

    private static string Code(AgentLanguage language) => language switch
    {
        AgentLanguage.English => "en",
        AgentLanguage.Arabic => "ar",
        AgentLanguage.Darija => "darija",
        _ => "fr",
    };

    private static DateTimeOffset Local(string value) =>
        TunisTime.FromLocal(DateTime.ParseExact(value, BookingDeepLink.DateTimeFormat, CultureInfo.InvariantCulture));

    /// <summary>Six digits from the id of a scenario, the same on every run (FNV-1a).</summary>
    private static int StableNumber(string id)
    {
        var hash = 2_166_136_261u;
        foreach (var character in id)
        {
            hash = unchecked((hash ^ character) * 16_777_619u);
        }

        return (int)(hash % 1_000_000u);
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values) is { Length: > 0 } joined ? joined : "none";
}

/// <summary>The model of an evaluation run behind the port of the agent: the scripted or the real client, recorded.</summary>
internal sealed class EvaluationModel(IChatClient client, string modelId) : IAgentModel
{
    public bool IsAvailable => true;

    public string ModelId => modelId;

    public int MaxOutputTokens => 2048;

    public IChatClient Client => client;
}

/// <summary>Records each call of the model (the request and the response), whatever the model behind.</summary>
internal sealed class RecordingChatClient : DelegatingChatClient
{
    private readonly List<(IReadOnlyList<ChatMessage> Messages, ChatResponse Response)> _calls = [];

    public RecordingChatClient(IChatClient inner)
        : base(inner)
    {
    }

    public int CallCount
    {
        get
        {
            lock (_calls)
            {
                return _calls.Count;
            }
        }
    }

    public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatResponse Response)> CallsSince(int index)
    {
        lock (_calls)
        {
            return [.. _calls.Skip(index)];
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = messages.ToList();
        var response = await base.GetResponseAsync(request, options, cancellationToken);
        lock (_calls)
        {
            _calls.Add((request, response));
        }

        return response;
    }
}

/// <summary>What a turn of a scenario showed.</summary>
internal sealed record ObservedTurn(
    int Index,
    string Customer,
    string? Reply,
    IReadOnlyList<ReplyButtonInfo> Buttons,
    IReadOnlyList<FunctionCallContent> Calls,
    string? HandOverReason,
    bool HandedOver,
    AgentTurnOutcome? Outcome,
    int ModelCalls,
    AgentTurnRecord? Record,
    string? SystemPrompt);

internal sealed record TurnReport(ObservedTurn Turn, IReadOnlyList<string> Failures);

/// <summary>The result of a scenario, turn by turn, with its tokens and cost (from the turn records).</summary>
internal sealed record ScenarioReport(EvaluationScenario Scenario, EvaluationMode Mode, string ModelId, IReadOnlyList<TurnReport> Turns)
{
    public bool Passed => Turns.All(t => t.Failures.Count == 0);

    public IEnumerable<string> Failures => Turns.SelectMany(t => t.Failures.Select(f => $"turn {t.Turn.Index}: {f}"));

    public ModelTokenUsage Tokens =>
        Turns.Select(t => t.Turn.Record).OfType<AgentTurnRecord>()
            .Aggregate(ModelTokenUsage.Zero, (sum, r) => sum.Add(new ModelTokenUsage(r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens)));

    public decimal CostUsd => Turns.Select(t => t.Turn.Record).OfType<AgentTurnRecord>().Sum(r => r.AiCostUsd);
}

/// <summary>The report of a run: Markdown for people, JSON for tools, written to a folder.</summary>
internal static class EvaluationReport
{
    public static async Task WriteAsync(string folder, IReadOnlyList<ScenarioReport> reports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reports);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "ai-eval-report.md"), Markdown(reports), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "ai-eval-report.json"), Json(reports), Encoding.UTF8, cancellationToken);
    }

    public static string Markdown(IReadOnlyList<ScenarioReport> reports)
    {
        var builder = new StringBuilder();
        var passed = reports.Count(r => r.Passed);
        var tokens = reports.Aggregate(ModelTokenUsage.Zero, (sum, r) => sum.Add(r.Tokens));
        var first = reports.Count > 0 ? reports[0] : null;
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Évaluation de l'agent WhatsApp — {first?.ModelId} ({first?.Mode})");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Scénarios réussis : {passed} / {reports.Count}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Jetons : {tokens.InputTokens} en entrée, {tokens.OutputTokens} en sortie, {tokens.CacheReadTokens} lus du cache, {tokens.CacheWriteTokens} écrits (part du cache : {tokens.CacheHitRatio:P1})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Coût estimé (grille par défaut des réglages, à vérifier sur la page des tarifs) : {reports.Sum(r => r.CostUsd):0.0000} USD");
        builder.AppendLine();
        builder.AppendLine("| Scénario | Langue | Couvre | Tours | Résultat |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var report in reports)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {report.Scenario.Id} — {report.Scenario.Title} | {report.Scenario.Language} | {string.Join(", ", report.Scenario.Covers)} | {report.Turns.Count} | {(report.Passed ? "OK" : "ÉCART")} |");
        }

        foreach (var report in reports.Where(r => !r.Passed))
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"## Écarts — {report.Scenario.Id}");
            foreach (var turn in report.Turns)
            {
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"**Tour {turn.Turn.Index}** — client : {turn.Turn.Customer}");
                builder.AppendLine();
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Outils : {string.Join(", ", turn.Turn.Calls.Select(c => c.Name))}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Réponse : {turn.Turn.Reply?.Replace('\n', ' ') ?? "(aucune)"}");
                if (turn.Turn.HandOverReason is { } reason)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"- Transfert : {reason}");
                }

                foreach (var failure in turn.Failures)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"- ÉCART : {failure}");
                }
            }
        }

        return builder.ToString();
    }

    private static string Json(IReadOnlyList<ScenarioReport> reports) =>
        JsonSerializer.Serialize(
            reports.Select(r => new
            {
                id = r.Scenario.Id,
                title = r.Scenario.Title,
                language = r.Scenario.Language,
                covers = r.Scenario.Covers,
                mode = r.Mode.ToString(),
                model = r.ModelId,
                passed = r.Passed,
                tokens = new { input = r.Tokens.InputTokens, output = r.Tokens.OutputTokens, cacheRead = r.Tokens.CacheReadTokens, cacheWrite = r.Tokens.CacheWriteTokens },
                costUsd = r.CostUsd,
                turns = r.Turns.Select(t => new
                {
                    index = t.Turn.Index,
                    customer = t.Turn.Customer,
                    reply = t.Turn.Reply,
                    buttons = t.Turn.Buttons.Select(b => b.Title),
                    tools = t.Turn.Calls.Select(c => c.Name),
                    handOver = t.Turn.HandOverReason,
                    outcome = t.Turn.Outcome?.ToString(),
                    modelCalls = t.Turn.ModelCalls,
                    failures = t.Failures,
                }),
            }),
            JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
