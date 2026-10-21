using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarRental.Application.Messaging.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// The state of one turn of the agent, shared by its tools: who and when, the agency, the memory of the conversation,
/// the need as the tools understood it, the amounts the tools returned (for the amount guardrail), the options found,
/// the quote link sent, the hand-over asked by the model, and whether the turn made progress.
/// </summary>
internal sealed class AgentTurnContext
{
    private readonly HashSet<decimal> _knownAmounts = [];
    private readonly List<decimal> _newAmounts = [];

    public AgentTurnContext(ConversationTurn turn, AgentLanguage language, AgentAgencyContext agency, AgentConversationState state, AgentTurnSettings settings, DateTimeOffset now)
    {
        Turn = turn;
        Language = language;
        Agency = agency;
        State = state;
        Settings = settings;
        Now = now;
        Need = turn.Need;
        foreach (var amount in state.KnownAmounts)
        {
            _knownAmounts.Add(amount);
        }
    }

    public ConversationTurn Turn { get; }

    public AgentLanguage Language { get; }

    public AgentAgencyContext Agency { get; }

    public AgentConversationState State { get; }

    public AgentTurnSettings Settings { get; }

    public DateTimeOffset Now { get; }

    /// <summary>The need, updated from the arguments of the tools (no extra model call).</summary>
    public ConversationNeedInfo Need { get; set; }

    public bool NeedChanged => Need != Turn.Need;

    /// <summary>Amounts the reply may quote: those of the conversation so far and of the tools of this turn.</summary>
    public IReadOnlyCollection<decimal> KnownAmounts => _knownAmounts;

    /// <summary>Amounts the tools returned in this turn (remembered for the next turns).</summary>
    public IReadOnlyList<decimal> NewAmounts => _newAmounts;

    /// <summary>The options of the latest search of this turn (buttons of the reply).</summary>
    public IReadOnlyList<AgentOption> SearchOptions { get; set; } = [];

    public bool QuoteLinkCreated { get; set; }

    /// <summary>
    /// <c>statut_reservation</c> found a file of this customer confirmed by the agency: the reply may report it (the bot
    /// states the agency's decision, it confirms nothing itself), so the confirmation guardrail stands aside for this turn.
    /// </summary>
    public bool StatusOfConfirmedBooking { get; set; }

    public AgentTransfer? Transfer { get; set; }

    /// <summary>A tool gave something useful (options, a link, an answer, a status) or the need got clearer.</summary>
    public bool Progress { get; set; }

    public int ToolCalls { get; set; }

    public void Remember(IEnumerable<decimal> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        foreach (var amount in amounts.Select(Math.Abs).Where(a => a > 0m))
        {
            if (_knownAmounts.Add(amount))
            {
                _newAmounts.Add(amount);
            }
        }
    }

    public void Remember(params Money[] amounts) => Remember(amounts.Select(a => a.Amount));
}

/// <summary>A hand-over asked by the model (tool <c>transferer_a_un_agent</c>): the reason category and a short summary for the agent.</summary>
internal sealed record AgentTransfer(string Category, string Summary)
{
    /// <summary>The reason shown in the shared inbox (French, like the back office).</summary>
    public string Reason => $"{Category} : {Summary}";
}

/// <summary>A tool of the agent: its name and description for the model, its input schema, and what it does.</summary>
internal interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON schema of the input (object, <c>additionalProperties: false</c>), written once: stable for the prompt cache.</summary>
    string InputSchema { get; }

    /// <summary>Runs the tool; a business failure is a result with <see cref="AgentToolOutcome.IsError"/>, never an exception.</summary>
    Task<AgentToolOutcome> RunAsync(ToolArguments arguments, AgentTurnContext context, CancellationToken cancellationToken);
}

/// <summary>What a tool returns to the model: data (serialized as JSON) and whether it is an error.</summary>
internal sealed record AgentToolOutcome(object Result, bool IsError = false)
{
    public static AgentToolOutcome Error(string message, object? details = null) =>
        new(details is null ? new { erreur = message } : new { erreur = message, details }, IsError: true);
}

/// <summary>JSON of the tool results: readable accents (the model reads them), declared order, no nulls.</summary>
internal static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.General)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    /// <summary>An amount as the tools give it: <c>681.400 TND</c> (three decimals, dot).</summary>
    public static string Amount(Money money) => money.Amount.ToString("0.000", CultureInfo.InvariantCulture) + " TND";

    /// <summary>A Tunis wall-clock instant as the tools give it: <c>2027-07-01T10:00</c>.</summary>
    public static string Local(DateTimeOffset instant) =>
        TunisTime.ToTunis(instant).ToString(BookingDeepLink.DateTimeFormat, CultureInfo.InvariantCulture);
}

/// <summary>
/// The five tools of the agent (PROMPT.md annexe B and §9.2), in a fixed order with fixed schemas: their declarations
/// open the prompt and are part of the cached prefix. Each call is validated against the schema of its tool before it
/// runs; a malformed call, an unknown tool or an unexpected failure is returned to the model as a tool error. Tool
/// results are data for the model, never instructions.
/// </summary>
internal sealed partial class AgentToolbox
{
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly ILogger<AgentToolbox> _logger;

    public AgentToolbox(
        SearchAvailabilityTool search,
        QuoteLinkTool quoteLink,
        AgencyInfoTool agencyInfo,
        BookingStatusTool bookingStatus,
        TransferTool transfer,
        ILogger<AgentToolbox> logger)
    {
        _tools = [search, quoteLink, agencyInfo, bookingStatus, transfer];
        _logger = logger;
        Declarations = [.. _tools.Select(t => (AITool)AIFunctionFactory.CreateDeclaration(t.Name, t.Description, JsonDocument.Parse(t.InputSchema).RootElement.Clone()))];
    }

    /// <summary>Names of the tools, never to be seen by a customer.</summary>
    public static IReadOnlyList<string> ToolNames { get; } =
        [SearchAvailabilityTool.ToolName, QuoteLinkTool.ToolName, AgencyInfoTool.ToolName, BookingStatusTool.ToolName, TransferTool.ToolName];

    public IList<AITool> Declarations { get; }

    /// <summary>Runs one call of the model; never throws for the call itself (see <see cref="IAgentTool.RunAsync"/>).</summary>
    public async Task<(FunctionResultContent Result, AgentToolOutcome Outcome, bool Unexpected)> RunAsync(
        FunctionCallContent call,
        AgentTurnContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        context.ToolCalls++;
        var tool = _tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.Ordinal));
        AgentToolOutcome outcome;
        var unexpected = false;
        if (tool is null)
        {
            outcome = AgentToolOutcome.Error($"Outil inconnu : utilise seulement {string.Join(", ", ToolNames)}.");
        }
        else if (call.Exception is not null || ToolArguments.Parse(call.Arguments, tool.InputSchema) is not { } arguments)
        {
            outcome = AgentToolOutcome.Error("Arguments illisibles : envoie un objet JSON conforme au schéma de l'outil.");
        }
        else if (arguments.Errors.Count > 0)
        {
            outcome = AgentToolOutcome.Error("Arguments invalides.", arguments.Errors);
        }
        else
        {
            try
            {
                outcome = await tool.RunAsync(arguments, context, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogToolFailed(exception, tool.Name);
                outcome = AgentToolOutcome.Error("Erreur technique de l'outil : propose au client de passer la main à un agent.");
                unexpected = true;
            }
        }

        if (outcome.IsError)
        {
            LogToolError(call.Name);
        }

        var json = AgentJson.Serialize(outcome.Result);
        var result = new FunctionResultContent(call.CallId, json);
        if (outcome.IsError)
        {
            // The Claude adapter marks the tool result as an error (is_error) when an exception is attached.
            result.Exception = new AgentToolFailure("The tool returned an error.");
        }

        return (result, outcome, unexpected);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp agent tool {Tool} returned an error to the model.")]
    private partial void LogToolError(string tool);

    [LoggerMessage(Level = LogLevel.Error, Message = "WhatsApp agent tool {Tool} failed unexpectedly.")]
    private partial void LogToolFailed(Exception exception, string tool);
}
