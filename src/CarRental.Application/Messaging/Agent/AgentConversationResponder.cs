using System.Globalization;
using System.Text;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Messaging.Agent.Tools;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Messaging.Agent;

/// <summary>
/// The WhatsApp agent (package 4B, ADR-0055 and ADR-0056): answers one customer turn with the model of
/// <see cref="IAgentModel"/> and its five tools, then checks the reply with guardrails enforced by the code.
/// <list type="number">
/// <item>Silent (like the default responder) when the bot is off, no model is configured, the number is outside the
/// audience or no agency is set up.</item>
/// <item>A conversation over its caps (turns, tokens, cost, turns without progress) is handed over without a model call;
/// a turn made of attachments only gets the fixed reply pointing to the secure link (no model call, nothing opened).</item>
/// <item>Otherwise the request is the cached prefix (tool definitions and the system prompt of annexe B, without the date)
/// then the transcript and the variable context of the turn (date, window, need, options, linked file); the tool loop
/// is bounded by the model calls and the tool calls of a turn.</item>
/// <item>The single message of the turn is the model's final text — with reply buttons for the options it proposes —
/// unless a guardrail blocks it: an amount the tools never returned or a confirmation promise hand the conversation over
/// with a polite message; a disclosure of the instructions or a request for a document photo is replaced by a fixed
/// reply. A refusal of the model, a reply cut by the output limit or a tool failure hand over too.</item>
/// </list>
/// The structured need comes from the arguments of the tools; the usage (tokens, tool calls, AI cost from the price
/// grid, estimated Meta cost of the reply) goes back to the conversation and to a turn record for the cost reports.
/// A transient failure of the model service throws: the bot turn executor retries the turn, then hands it over.
/// </summary>
internal sealed partial class AgentConversationResponder(
    IAgentModel model,
    AgentToolbox toolbox,
    AgentPromptRenderer prompts,
    AgentAgencyLoader agencies,
    MessagingScopes scopes,
    ISettingsReader settings,
    IBookingsQueries bookings,
    IBotAudience audience,
    IOptions<AgentSiteOptions> site,
    TimeProvider clock,
    ILogger<AgentConversationResponder> logger) : IConversationResponder
{
    public const string CapReason = "Plafond de l'assistant atteint ({0}) : à reprendre par un agent.";
    public const string RefusalReason = "L'assistant IA a refusé de répondre : à reprendre par un agent.";
    public const string TruncatedReason = "Réponse de l'assistant coupée par la limite de sortie : à reprendre par un agent.";
    public const string ToolFailureReason = "Erreur technique d'un outil de l'assistant : à reprendre par un agent.";
    public const string RejectedReason = "Le service IA a refusé la requête : à reprendre par un agent (vérifier la configuration Ai).";
    public const string EmptyReason = "Réponse vide de l'assistant : à reprendre par un agent.";
    public const string AmountGuardReason = "Garde-fou : la réponse citait un montant absent des résultats des outils (réponse bloquée).";
    public const string ConfirmationGuardReason = "Garde-fou : la réponse promettait la confirmation d'une réservation (réponse bloquée).";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly CompositeFormat CapFormat = CompositeFormat.Parse(CapReason);

    public async Task<ConversationReply> RespondAsync(ConversationTurn turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (!model.IsAvailable
            || !audience.Includes(turn.WhatsAppNumber)
            || !await settings.GetAsync(AgentSettings.Enabled, cancellationToken: cancellationToken))
        {
            return ConversationReply.None;
        }

        var agency = await agencies.LoadAsync(cancellationToken);
        if (agency is null)
        {
            LogNoAgency();
            return ConversationReply.None;
        }

        var now = clock.GetUtcNow();
        var turnSettings = await AgentTurnSettings.ReadAsync(settings, cancellationToken);

        // The memory of the agent lives in a scope of its own: a failed unit of work of another module (locked quote) clears
        // the change tracker of the responder's scope, never this one.
        await using var memoryScope = scopes.CreateScope(correlationId: null);
        var memory = memoryScope.ServiceProvider.GetRequiredService<AgentMemory>();
        var state = await memory.LoadAsync(turn.ConversationId, now, cancellationToken);
        var context = new AgentTurnContext(turn, LanguageOf(turn), agency, state, turnSettings, now);
        var run = new AgentRun();
        AgentTurnResult result;
        try
        {
            result = await RunTurnAsync(context, run, cancellationToken);
        }
        catch (AgentModelRejectedException exception)
        {
            LogRejected(exception.GetType().Name);
            result = HandOver(context, run, RejectedReason, AgentTurnOutcome.Failed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Retried by the bot turn executor: what was spent counts in the cost reports.
            await memory.SaveAsync(Record(context, run, AgentTurnOutcome.Retried, metaCostUsd: 0m), cancellationToken);
            throw;
        }

        await memory.SaveAsync(result.Record, cancellationToken);
        LogTurn(result.Record.Outcome, run.ModelCalls, context.ToolCalls, result.Reply.Message is not null, result.Reply.HandOverReason is not null);
        return result.Reply;
    }

    private async Task<AgentTurnResult> RunTurnAsync(AgentTurnContext context, AgentRun run, CancellationToken cancellationToken)
    {
        var turn = context.Turn;
        var caps = context.Settings.Caps;
        var cap = caps.Reached(
            turn.Usage.BotTurns,
            turn.Usage.InputTokens + turn.Usage.OutputTokens + turn.Usage.CacheReadTokens + turn.Usage.CacheWriteTokens,
            turn.Usage.AiCostUsd + turn.Usage.MetaCostUsd,
            context.State.TurnsWithoutProgress);
        if (cap != AgentCap.None)
        {
            return HandOver(context, run, string.Format(French, CapFormat, CapName(cap)), AgentTurnOutcome.CapReached);
        }

        if (turn.NewMessages.Count > 0 && turn.NewMessages.All(AgentTranscript.IsAttachment))
        {
            return Fixed(context, run, AgentMessages.AttachmentNotProcessed(context.Language, SecureLink(context.Language)), AgentTurnOutcome.AttachmentReply);
        }

        var prompt = prompts.Render(context.Agency);
        var messages = AgentTranscript.Build(prompt.SystemPrompt, turn);
        messages.Add(new ChatMessage(ChatRole.System, await VariableContextAsync(context, prompt, cancellationToken)));
        var options = new ChatOptions
        {
            ModelId = model.ModelId,
            MaxOutputTokens = model.MaxOutputTokens,
            Tools = toolbox.Declarations,
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = true,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
            AdditionalProperties = new AdditionalPropertiesDictionary { [IAgentModel.ScriptKeyProperty] = turn.WhatsAppNumber },
        };

        while (true)
        {
            if (run.ModelCalls >= caps.MaxModelCallsPerTurn)
            {
                return HandOver(context, run, string.Format(French, CapFormat, CapName(AgentCap.ModelCallsPerTurn)), AgentTurnOutcome.CapReached);
            }

            var response = await model.Client.GetResponseAsync(messages, options, cancellationToken);
            run.Add(response.Usage);
            if (response.FinishReason == ChatFinishReason.ContentFilter)
            {
                return HandOver(context, run, RefusalReason, AgentTurnOutcome.Refused);
            }

            if (response.FinishReason == ChatFinishReason.Length)
            {
                // Never a truncated reply, nor a tool call cut in the middle.
                return HandOver(context, run, TruncatedReason, AgentTurnOutcome.Truncated);
            }

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                return Finish(context, run, prompt, response.Text);
            }

            if (context.ToolCalls + calls.Count > caps.MaxToolCallsPerTurn)
            {
                return HandOver(context, run, string.Format(French, CapFormat, CapName(AgentCap.ToolCallsPerTurn)), AgentTurnOutcome.CapReached);
            }

            messages.AddRange(response.Messages);
            var results = new List<AIContent>(calls.Count);
            foreach (var call in calls)
            {
                var (result, _, unexpected) = await toolbox.RunAsync(call, context, cancellationToken);
                if (unexpected)
                {
                    return HandOver(context, run, ToolFailureReason, AgentTurnOutcome.Failed);
                }

                results.Add(result);
            }

            messages.Add(new ChatMessage(ChatRole.Tool, results));
        }
    }

    /// <summary>The final text of the model, through the guardrails, as the message of the turn.</summary>
    private AgentTurnResult Finish(AgentTurnContext context, AgentRun run, AgentPrompt prompt, string? finalText)
    {
        var text = finalText?.Trim() ?? string.Empty;
        var leaks = new PromptLeakDetector(prompt.SystemPrompt, [.. AgentToolbox.ToolNames, AgentTranscript.ContextTag, "{{"], AgentPrompt.Headings);
        if (context.Transfer is { } transfer)
        {
            var farewell = text.Length > 0 && text.Length <= ButtonsContent.BodyMaxLength && Passes(text, context, leaks) ? text : AgentMessages.HandOver(context.Language);
            return Build(context, run, new OutboundText(farewell), transfer.Reason, AgentTurnOutcome.TransferredByModel);
        }

        if (text.Length == 0)
        {
            return HandOver(context, run, EmptyReason, AgentTurnOutcome.Failed);
        }

        if (leaks.Leaks(text))
        {
            LogGuardrail("prompt-leak");
            return Fixed(context, run, AgentMessages.PoliteRefusal(context.Language), AgentTurnOutcome.ReplacedByGuardrail);
        }

        if (DocumentRequests.Contains(text))
        {
            LogGuardrail("document-request");
            return Fixed(context, run, AgentMessages.DocumentsOnSecureLinkOnly(context.Language, SecureLink(context.Language)), AgentTurnOutcome.ReplacedByGuardrail);
        }

        if (AmountGuard.Unknown(text, context.KnownAmounts).Count > 0)
        {
            LogGuardrail("unknown-amount");
            return HandOver(context, run, AmountGuardReason, AgentTurnOutcome.BlockedByGuardrail);
        }

        if (!context.StatusOfConfirmedBooking && ConfirmationPromises.Contains(text))
        {
            LogGuardrail("confirmation-promise");
            return HandOver(context, run, ConfirmationGuardReason, AgentTurnOutcome.BlockedByGuardrail);
        }

        if (text.Length > CarRental.Domain.Messaging.TextContent.MaxLength)
        {
            return HandOver(context, run, TruncatedReason, AgentTurnOutcome.Truncated);
        }

        return Build(context, run, (OutboundMessageContent?)ButtonsFor(context, text) ?? new OutboundText(text), handOverReason: null, AgentTurnOutcome.Replied);
    }

    private static bool Passes(string text, AgentTurnContext context, PromptLeakDetector leaks) =>
        !leaks.Leaks(text)
        && !DocumentRequests.Contains(text)
        && AmountGuard.Unknown(text, context.KnownAmounts).Count == 0
        && (context.StatusOfConfirmedBooking || !ConfirmationPromises.Contains(text));

    /// <summary>
    /// Reply buttons to pick one of the options of this turn's search (at most three, titles of 20 characters at most),
    /// when the reply presents them and no quote link was sent yet: one tap answers, fewer round trips (SPEC §11).
    /// </summary>
    private static OutboundButtons? ButtonsFor(AgentTurnContext context, string text)
    {
        var options = context.SearchOptions;
        if (options.Count == 0 || context.QuoteLinkCreated || text.Length > ButtonsContent.BodyMaxLength)
        {
            return null;
        }

        var normalized = AgentText.ForMatching(text);
        var mentioned = AmountMentions.Find(text).SelectMany(m => m.Candidates).ToHashSet();
        if (!options.Any(o => mentioned.Contains(o.Total.Amount) || normalized.Contains(AgentText.ForMatching(o.CategoryName), StringComparison.Ordinal)))
        {
            return null;
        }

        var buttons = options
            .Take(ButtonsContent.MaxButtons)
            .Select((o, index) =>
            {
                var title = $"{index + 1}. {o.CategoryName}";
                return new ReplyButtonInfo($"option:{o.Id}", title.Length <= ReplyButton.TitleMaxLength ? title : title[..ReplyButton.TitleMaxLength].TrimEnd());
            })
            .ToList();
        return new OutboundButtons(text, buttons);
    }

    private AgentTurnResult HandOver(AgentTurnContext context, AgentRun run, string reason, AgentTurnOutcome outcome) =>
        Build(context, run, new OutboundText(AgentMessages.HandOver(context.Language)), reason, outcome);

    private AgentTurnResult Fixed(AgentTurnContext context, AgentRun run, string text, AgentTurnOutcome outcome) =>
        Build(context, run, new OutboundText(text), handOverReason: null, outcome);

    private AgentTurnResult Build(AgentTurnContext context, AgentRun run, OutboundMessageContent message, string? handOverReason, AgentTurnOutcome outcome)
    {
        var metaCost = context.Settings.MetaUsdPerMessage;
        var record = Record(context, run, outcome, metaCost);
        context.State.RememberAmounts(context.NewAmounts);
        context.State.CompleteTurn(context.Progress || context.NeedChanged, context.Now);
        var reply = new ConversationReply
        {
            Message = message,
            Need = context.NeedChanged ? context.Need : null,
            Usage = new ConversationTurnUsageInfo(
                Clamp(run.Tokens.InputTokens),
                Clamp(run.Tokens.OutputTokens),
                Clamp(run.Tokens.CacheReadTokens),
                Clamp(run.Tokens.CacheWriteTokens),
                context.ToolCalls,
                record.AiCostUsd,
                metaCost),
            HandOverReason = handOverReason,
        };
        return new AgentTurnResult(reply, record);
    }

    private AgentTurnRecord Record(AgentTurnContext context, AgentRun run, AgentTurnOutcome outcome, decimal metaCostUsd) =>
        AgentTurnRecord.Create(
            context.Turn.ConversationId,
            context.Now,
            run.ModelCalls > 0 ? model.ModelId : null,
            outcome,
            context.Language,
            run.ModelCalls,
            context.ToolCalls,
            run.Tokens,
            context.Settings.Prices.CostUsd(run.Tokens),
            metaCostUsd);

    /// <summary>
    /// The variable part of the request, after the cached prefix (a mid-conversation system message): the date sentence of
    /// annexe B, the end of the window, the likely language, what is known of the need, the options and link of earlier
    /// turns, the file linked by the agency, attachments not opened and the turns left before the caps.
    /// </summary>
    private async Task<string> VariableContextAsync(AgentTurnContext context, AgentPrompt prompt, CancellationToken cancellationToken)
    {
        var turn = context.Turn;
        var lines = new List<string>
        {
            $"<{AgentTranscript.ContextTag}>",
            prompt.TodaySentence(context.Now),
            $"Fenêtre de réponse WhatsApp ouverte jusqu'au {LocalText(turn.WindowClosesAtUtc)} (heure de Tunis).",
            $"Langue probable du client : {LanguageName(context.Language)}.",
        };
        if (NeedText(turn.Need) is { } need)
        {
            lines.Add($"Besoin connu : {need}.");
        }

        if (context.State.Options.Count > 0)
        {
            lines.Add("Options de la dernière recherche (ids pour creer_lien_devis) : "
                      + string.Join(" ; ", context.State.Options.Select(o =>
                          $"{o.Id} {o.CategoryName} ({o.CategoryCode}) {AgentJson.Amount(o.Total)}, du {AgentJson.Local(o.PickupAt)} au {AgentJson.Local(o.ReturnAt)}")) + ".");
        }

        if (context.State.LastQuoteLink is { } link)
        {
            lines.Add($"Dernier lien de devis envoyé : {link}");
        }

        if (turn.BookingId is { } bookingId && await bookings.GetSummaryAsync(bookingId, cancellationToken) is { } booking)
        {
            lines.Add($"Dossier rattaché à la conversation par l'agence : {booking.Number}.");
        }

        if (turn.NewMessages.Any(AgentTranscript.IsAttachment))
        {
            lines.Add("Le client a envoyé une pièce jointe : elle n'a pas été ouverte (aucun document par WhatsApp). Rappelle le lien sécurisé si besoin, ne demande jamais de photo.");
        }

        var turnsLeft = context.Settings.Caps.MaxBotTurns - turn.Usage.BotTurns - 1;
        if (turnsLeft <= 2)
        {
            lines.Add($"Il reste {turnsLeft} tour(s) avant le transfert automatique à un agent : va à l'essentiel.");
        }

        lines.Add($"</{AgentTranscript.ContextTag}>");
        return string.Join('\n', lines);
    }

    private static string? NeedText(ConversationNeedInfo need)
    {
        var parts = new List<string>();
        if (need.PickupPlace is { } pickup)
        {
            parts.Add($"prise en charge {pickup}");
        }

        if (need.ReturnPlace is { } dropOff)
        {
            parts.Add($"retour {dropOff}");
        }

        if (need.PickupAt is { } pickupAt)
        {
            parts.Add($"départ {AgentJson.Local(pickupAt)}");
        }

        if (need.ReturnAt is { } returnAt)
        {
            parts.Add($"retour {AgentJson.Local(returnAt)}");
        }

        if (need.Passengers is { } passengers)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{passengers} passager(s)"));
        }

        if (need.Luggage is { } luggage)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{luggage} bagage(s)"));
        }

        if (need.Gearbox is { } gearbox)
        {
            parts.Add(gearbox == GearboxPreferenceInfo.Automatic ? "boîte automatique" : "boîte manuelle");
        }

        if (need.Budget is { } budget)
        {
            parts.Add($"budget {AgentJson.Amount(budget)}");
        }

        return parts.Count == 0 ? null : string.Join(" ; ", parts);
    }

    private Uri SecureLinkUri(AgentLanguage language) =>
        site.Value.PublicBaseUrl is { IsAbsoluteUri: true } baseUrl ? BookingDeepLink.FunnelPage(baseUrl, language) : new Uri("https://localhost/" + BookingDeepLink.PathFor(language));

    private string SecureLink(AgentLanguage language) => SecureLinkUri(language).AbsoluteUri;

    /// <summary>The language of the turn's messages; when they tell nothing (« ok », a button), that of the earlier customer messages.</summary>
    private static AgentLanguage LanguageOf(ConversationTurn turn) =>
        CustomerLanguageDetector.TryDetect(turn.NewMessages.Select(m => m.Text))
        ?? CustomerLanguageDetector.TryDetect(turn.History.Where(m => m.Direction == MessageDirectionInfo.Inbound).TakeLast(5).Select(m => m.Text))
        ?? AgentLanguage.French;

    private static string LanguageName(AgentLanguage language) => language switch
    {
        AgentLanguage.English => "anglais",
        AgentLanguage.Arabic => "arabe",
        AgentLanguage.Darija => "darija tunisienne (lettres latines)",
        _ => "français",
    };

    private static string CapName(AgentCap cap) => cap switch
    {
        AgentCap.BotTurns => "nombre de tours",
        AgentCap.Tokens => "jetons consommés",
        AgentCap.Cost => "coût de la conversation",
        AgentCap.TurnsWithoutProgress => "tours sans progrès",
        AgentCap.ModelCallsPerTurn => "appels au modèle dans un tour",
        AgentCap.ToolCallsPerTurn => "appels d'outils dans un tour",
        _ => "plafond",
    };

    private static string LocalText(DateTimeOffset instant) =>
        CarRental.Domain.Common.TunisTime.ToTunis(instant).ToString("dddd d MMMM yyyy 'à' HH:mm", French);

    private static int Clamp(long value) => (int)Math.Min(value, int.MaxValue);

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp agent turn: {Outcome}, {ModelCalls} model call(s), {ToolCalls} tool call(s), message {Replied}, hand-over {HandOver}.")]
    private partial void LogTurn(AgentTurnOutcome outcome, int modelCalls, int toolCalls, bool replied, bool handOver);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp agent: a reply was stopped by the {Guardrail} guardrail.")]
    private partial void LogGuardrail(string guardrail);

    [LoggerMessage(Level = LogLevel.Critical, Message = "WhatsApp agent: the model service refused the request for good ({ExceptionType}); check the Ai settings.")]
    private partial void LogRejected(string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp agent: no active agency is set up; the bot stays silent.")]
    private partial void LogNoAgency();
}

/// <summary>The model calls of a turn and their tokens.</summary>
internal sealed class AgentRun
{
    public int ModelCalls { get; private set; }

    public ModelTokenUsage Tokens { get; private set; } = ModelTokenUsage.Zero;

    /// <summary>Adds a call (usage as the Claude adapter reports it: the input count includes the cache reads and writes).</summary>
    public void Add(UsageDetails? usage)
    {
        ModelCalls++;
        if (usage is null)
        {
            return;
        }

        var cacheRead = usage.CachedInputTokenCount ?? 0;
        var cacheWrite = usage.AdditionalCounts?.TryGetValue(IAgentModel.CacheWriteCountKey, out var written) == true ? written : 0;
        var input = Math.Max(0, (usage.InputTokenCount ?? 0) - cacheRead - cacheWrite);
        Tokens = Tokens.Add(new ModelTokenUsage(input, usage.OutputTokenCount ?? 0, cacheRead, cacheWrite));
    }
}

/// <summary>The reply of a turn and its record for the cost reports.</summary>
internal sealed record AgentTurnResult(ConversationReply Reply, AgentTurnRecord Record);
