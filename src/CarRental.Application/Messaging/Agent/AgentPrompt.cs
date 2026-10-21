using System.Globalization;
using System.Text.RegularExpressions;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Messaging.Agent;

/// <summary>What the agent knows of its agency for a turn: the agency, its pick-up places, its opening hours and categories.</summary>
internal sealed record AgentAgencyContext(
    AgencyInfo Agency,
    IReadOnlyList<LocationInfo> Locations,
    OpeningHours OpeningHours,
    IReadOnlyList<VehicleCategoryInfo> Categories)
{
    /// <summary>The address given to customers: the agency's, else its office's, else its city.</summary>
    public string Address =>
        Agency.Address
        ?? Locations.FirstOrDefault(l => l.Kind == LocationKindInfo.AgencyOffice && !string.IsNullOrWhiteSpace(l.Address))?.Address
        ?? Agency.City;

    public string OpeningHoursText =>
        string.Create(CultureInfo.InvariantCulture, $"de {OpeningHours.OpensAt:HH\\:mm} à {OpeningHours.ClosesAt:HH\\:mm}");
}

/// <summary>
/// The system prompt of a turn, split for the prompt cache (ADR-0055): <see cref="SystemPrompt"/> is the stable prefix —
/// PROMPT.md annexe B with the agency's name, city, opening hours and address filled in, without the sentence of the
/// date, then the technical addendum — byte-identical from one turn and one day to the next; the date sentence of the
/// annexe (« Nous sommes le {{TODAY}} (heure de Tunis). ») is rendered in the variable context of each turn instead.
/// </summary>
internal sealed record AgentPrompt(string SystemPrompt, string TodaySentenceTemplate)
{
    public const string TodayVariable = "{{TODAY}}";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>The date sentence of the annexe for <paramref name="now"/>, Tunis time (« mardi 21 octobre 2026 à 14:35 »).</summary>
    public string TodaySentence(DateTimeOffset now) =>
        TodaySentenceTemplate.Replace(TodayVariable, TunisTime.ToTunis(now).ToString("dddd d MMMM yyyy 'à' HH:mm", French), StringComparison.Ordinal);

    /// <summary>Section titles of the annexe that must never reach a customer (two in a reply make a leak).</summary>
    public static IReadOnlyList<string> Headings { get; } =
        ["Langue et ton", "Ce qu'il te faut avant de proposer", "Passer la main à un agent (appelle", "Format d'une proposition"];
}

/// <summary>Renders <see cref="AgentPrompt"/> from the template of <see cref="IAgentPromptSource"/> and the agency.</summary>
internal sealed partial class AgentPromptRenderer(IAgentPromptSource source)
{
    public AgentPrompt Render(AgentAgencyContext agency)
    {
        ArgumentNullException.ThrowIfNull(agency);
        var template = source.SystemPromptTemplate.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var today = TodaySentencePattern().Match(template);
        var todaySentence = today.Success ? today.Value.Trim() : "Nous sommes le {{TODAY}} (heure de Tunis).";
        var stable = (today.Success ? template.Remove(today.Index, today.Length) : template)
            .Replace("{{AGENCY_NAME}}", agency.Agency.Name, StringComparison.Ordinal)
            .Replace("{{AGENCY_CITY}}", agency.Agency.City, StringComparison.Ordinal)
            .Replace("{{OPENING_HOURS}}", agency.OpeningHoursText, StringComparison.Ordinal)
            .Replace("{{AGENCY_ADDRESS}}", agency.Address, StringComparison.Ordinal);
        var addendum = source.Addendum.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return new AgentPrompt($"{stable}\n\n{addendum}", todaySentence);
    }

    /// <summary>The sentence holding <c>{{TODAY}}</c>, up to its full stop, with the spaces after it.</summary>
    [GeneratedRegex(@"[^\n.]*\{\{TODAY\}\}[^\n.]*\.[ \t]*", RegexOptions.CultureInvariant)]
    private static partial Regex TodaySentencePattern();
}
