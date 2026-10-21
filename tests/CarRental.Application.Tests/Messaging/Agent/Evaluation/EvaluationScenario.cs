using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarRental.Application.Tests.Messaging.Agent.Evaluation;

/// <summary>
/// A scripted conversation of the evaluation of the WhatsApp agent (README.md of this folder): the customer's turns, the
/// answers of the scripted model for the CI replay, and the expectations checked against the scripted model as against
/// the real one.
/// </summary>
internal sealed record EvaluationScenario
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary><c>fr</c>, <c>ar</c>, <c>darija</c> or <c>en</c>.</summary>
    public required string Language { get; init; }

    /// <summary>What the scenario covers (recherche, transfert, garde-fou…), for the report.</summary>
    public IReadOnlyList<string> Covers { get; init; } = [];

    /// <summary>The WhatsApp number of the customer (E.164); one of its own by default.</summary>
    public string? Number { get; init; }

    public ScenarioWorld World { get; init; } = new();

    public required IReadOnlyList<ScenarioTurn> Turns { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The scenarios shipped next to the tests (<c>Evaluation/Scenarios/*.json</c>), by file name.</summary>
    public static IReadOnlyList<(string File, EvaluationScenario Scenario)> LoadAll()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Messaging", "Agent", "Evaluation", "Scenarios");
        return
        [
            .. Directory.EnumerateFiles(folder, "*.json")
                .Order(StringComparer.Ordinal)
                .Select(path => (Path.GetFileName(path), Load(path))),
        ];
    }

    public static EvaluationScenario Load(string path) =>
        JsonSerializer.Deserialize<EvaluationScenario>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} holds no scenario.");
}

/// <summary>
/// The agency of the scenario (the <c>AgentWorld</c> of the tests: AGC, TUN, NBE, five categories) and what differs:
/// categories available, dates with availability only, a minimum duration, a pricing failure, knowledge entries,
/// rental files.
/// </summary>
internal sealed record ScenarioWorld
{
    /// <summary>ACRISS codes available (all by default).</summary>
    public IReadOnlyList<string>? AvailableCategories { get; init; }

    /// <summary>Nothing is available but stays starting at this Tunis date and time (<c>2027-07-02T10:00</c>).</summary>
    public string? AvailableOnlyFrom { get; init; }

    public int? MinimumDays { get; init; }

    public bool PricingFails { get; init; }

    public IReadOnlyList<ScenarioKnowledge> Knowledge { get; init; } = [];

    /// <summary>Rental files, numbered LOC-2027-000001, LOC-2027-000002… in this order.</summary>
    public IReadOnlyList<ScenarioBooking> Bookings { get; init; } = [];
}

/// <summary>An answer of the knowledge base (validated by default).</summary>
internal sealed record ScenarioKnowledge
{
    public required string Topic { get; init; }

    public required string Title { get; init; }

    public string? Keywords { get; init; }

    public required string French { get; init; }

    public required string English { get; init; }

    public string? Arabic { get; init; }

    public bool Validated { get; init; } = true;
}

/// <summary>A rental file: its state and whether its main driver has the number of the customer (<c>self</c>) or another one.</summary>
internal sealed record ScenarioBooking
{
    public required string Status { get; init; }

    public string Driver { get; init; } = "self";

    public required string Pickup { get; init; }

    public required string Return { get; init; }
}

/// <summary>A customer turn: a text, a chosen button or attachments (with a text or not), the script of the turn and its expectations.</summary>
internal sealed record ScenarioTurn
{
    public string? Customer { get; init; }

    public ScenarioTap? Tap { get; init; }

    /// <summary>Kinds of attachments sent in the turn (<c>image</c>, <c>document</c>, <c>audio</c>, <c>video</c>, <c>sticker</c>).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>The answers of the scripted model in this turn (CI replay), in order.</summary>
    public IReadOnlyList<ScenarioStep> Script { get; init; } = [];

    /// <summary>Checked in both modes.</summary>
    public TurnExpectation Expect { get; init; } = new();

    /// <summary>Checked against the scripted model only (the replay plays a misbehaving model, for instance).</summary>
    public TurnExpectation? ExpectScripted { get; init; }

    /// <summary>Checked against the real model only.</summary>
    public TurnExpectation? ExpectLive { get; init; }
}

internal sealed record ScenarioTap(string Id, string Title);

/// <summary>One answer of the scripted model: a tool call, several, a final text, a refusal or a reply cut by the output limit.</summary>
internal sealed record ScenarioStep
{
    public string? Call { get; init; }

    public JsonElement? Arguments { get; init; }

    public IReadOnlyList<ScenarioCall>? Calls { get; init; }

    /// <summary>A final text; it may quote the tool results of the turn (<c>{{rechercher_disponibilites:options[0].total_ttc}}</c>).</summary>
    public string? Reply { get; init; }

    public bool Refusal { get; init; }

    public string? Truncated { get; init; }
}

internal sealed record ScenarioCall(string Tool, JsonElement Arguments);

/// <summary>What a turn must show; an absent field is not checked.</summary>
internal sealed record TurnExpectation
{
    /// <summary>Tools the model must call in the turn.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Tools the model must not call in the turn.</summary>
    public IReadOnlyList<string>? NoTools { get; init; }

    /// <summary>Arguments the first call of a tool must hold (subset), by tool name.</summary>
    public IReadOnlyDictionary<string, JsonElement>? ToolArguments { get; init; }

    /// <summary>Whether the turn hands the conversation over to an agent.</summary>
    public bool? HandOver { get; init; }

    /// <summary>Words of the hand-over reason (the category of a transfer: « Réclamation », « Accident ou panne »…).</summary>
    public string? HandOverReasonContains { get; init; }

    /// <summary>The reply offers reply buttons.</summary>
    public bool? Buttons { get; init; }

    /// <summary>The reply holds the link of the booking funnel with a locked quote (<c>devis=…&amp;src=whatsapp</c>).</summary>
    public bool? Link { get; init; }

    /// <summary>Model calls of the turn (0: answered by the code alone).</summary>
    public int? ModelCalls { get; init; }

    /// <summary>The reply is in one of these languages (<c>fr</c>, <c>en</c>, <c>ar</c>, <c>darija</c>).</summary>
    public IReadOnlyList<string>? ReplyLanguage { get; init; }

    /// <summary>The reply contains each of these texts (case and accents ignored).</summary>
    public IReadOnlyList<string>? ReplyContains { get; init; }

    /// <summary>The reply is a fixed message of the platform: <c>handOver</c>, <c>attachment</c>, <c>documents</c>, <c>refusal</c>.</summary>
    public string? ReplyIs { get; init; }

    /// <summary>No reply at all (the conversation is with an agent).</summary>
    public bool? Silent { get; init; }

    /// <summary>The outcome recorded for the turn (<c>Replied</c>, <c>AttachmentReply</c>, <c>BlockedByGuardrail</c>…).</summary>
    public string? Outcome { get; init; }

    /// <summary>The reply may report a booking confirmed by the agency (status of the customer's own file).</summary>
    public bool AllowConfirmation { get; init; }
}
