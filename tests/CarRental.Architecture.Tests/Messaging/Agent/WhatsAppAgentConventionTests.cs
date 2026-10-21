using System.Reflection;
using System.Text.RegularExpressions;
using CarRental.Application;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;
using CarRental.Web.Components.Public;
using CarRental.Web.Components.Public.Search;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Architecture.Tests.Messaging.Agent;

/// <summary>
/// What ties the WhatsApp agent to the rest of the repository: its system prompt is annex B of PROMPT.md word for word,
/// its deep link opens the booking funnel of the public site with the locked quote on the WhatsApp channel, its settings
/// are explained on the settings screen, and its logs carry ids and counts only (no text, number nor name).
/// </summary>
public sealed partial class WhatsAppAgentConventionTests
{
    private const string SettingLabels = "CarRental.Web.Resources.Admin.Settings.SettingLabels";

    /// <summary>Placeholders that would put a message, a prompt or a personal data in the logs.</summary>
    private static readonly string[] ForbiddenPlaceholders =
    [
        "Body", "Text", "Message", "Reply", "Content", "Prompt", "Arguments", "Answer", "Number", "Phone", "WhatsAppNumber", "ProfileName", "Name",
        "FullName", "Email", "Address",
    ];

    [Fact]
    public void The_system_prompt_of_the_agent_is_annex_B_of_PROMPT_md_word_for_word()
    {
        var root = RepositoryRoot();
        var instructions = Normalize(File.ReadAllText(Path.Combine(root, "docs", "PROMPT.md")));
        var annex = AnnexB().Match(instructions);
        annex.Success.ShouldBeTrue("PROMPT.md keeps annex B in a ```text block");

        var prompt = Normalize(File.ReadAllText(Path.Combine(root, "src", "CarRental.Infrastructure", "Messaging", "Prompts", "whatsapp-agent.md")));

        prompt.TrimEnd('\n').ShouldBe(annex.Groups["prompt"].Value.TrimEnd('\n'));
    }

    [Theory]
    [InlineData(AgentLanguage.French, "fr")]
    [InlineData(AgentLanguage.English, "en")]
    [InlineData(AgentLanguage.Arabic, "fr")]
    [InlineData(AgentLanguage.Darija, "fr")]
    public void The_deep_link_of_the_bot_opens_the_booking_funnel_with_the_locked_quote(AgentLanguage language, string culture)
    {
        var pickupAt = TunisTime.FromLocal(new DateTime(2027, 7, 1, 10, 0, 0));

        var link = BookingDeepLink.Build(new Uri("https://www.example.tn/"), language, "TUN", "NBE", pickupAt, pickupAt.AddDays(4), "MCMR", "Q1F3K9");

        link.AbsolutePath.ShouldBe(PublicRoutes.PathFor(PublicPage.Results, culture));
        var query = SearchQuery.Parse(QueryHelpers.ParseQuery(link.Query));
        query.PickupLocation.ShouldBe("TUN");
        query.ReturnLocation.ShouldBe("NBE");
        query.Pickup.ShouldBe("2027-07-01T10:00");
        query.Return.ShouldBe("2027-07-05T10:00");
        query.Category.ShouldNotBeNull().Value.ShouldBe("MCMR");
        query.QuoteCode.ShouldBe("Q1F3K9");
        query.Traffic.Channel.ShouldBe(SalesChannel.WhatsApp, "a link of the bot is priced and measured on the WhatsApp channel");
        BookingDeepLink.DateTimeFormat.ShouldBe(SearchParameters.DateTimeFormat);
        BookingDeepLink.Source.ShouldBe(TrafficSource.WhatsAppSource);
    }

    [Fact]
    public void Every_setting_of_the_agent_has_a_help_text_in_french_and_english()
    {
        using var provider = new ServiceCollection().AddApplication().BuildServiceProvider();
        var settings = provider.GetRequiredService<ISettingDefinitionCatalog>().All.Where(d => d.Key.StartsWith("ai.", StringComparison.Ordinal)).ToList();
        var french = ResourceCatalog.Entries(SettingLabels, ResourceCatalog.French);
        var english = ResourceCatalog.Entries(SettingLabels, ResourceCatalog.English);

        settings.Count.ShouldBe(13);
        settings.ShouldAllBe(s => s.Group == "ai");
        var missing = settings
            .Select(s => $"Setting.{s.Key}.Help")
            .Where(key => !french.ContainsKey(key) || !english.ContainsKey(key))
            .ToList();
        missing.ShouldBeEmpty();
    }

    [Fact]
    public void The_agent_logs_ids_and_counts_only()
    {
        var templates = new[] { Assemblies.Application, Assemblies.Infrastructure }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(t => t.Namespace is { } ns && (ns.StartsWith("CarRental.Application.Messaging.Agent", StringComparison.Ordinal)
                                                   || ns.StartsWith("CarRental.Infrastructure.Messaging.Agent", StringComparison.Ordinal)))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (Method: $"{m.DeclaringType!.Name}.{m.Name}", Template: m.GetCustomAttribute<LoggerMessageAttribute>()?.Message))
            .Where(m => m.Template is not null)
            .ToList();

        templates.Count.ShouldBeGreaterThan(10);
        var offenders = templates
            .SelectMany(t => Placeholder().Matches(t.Template!).Select(p => (t.Method, Placeholder: p.Groups["name"].Value)))
            .Where(p => ForbiddenPlaceholders.Contains(p.Placeholder, StringComparer.OrdinalIgnoreCase))
            .Select(p => $"{p.Method}: {{{p.Placeholder}}}")
            .ToList();
        offenders.ShouldBeEmpty("logs of the agent never carry a message body, a prompt, a phone number or a name");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The folder holding CarRental.slnx, above the test binaries.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CarRental.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"CarRental.slnx not found above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex("## Annexe B[^\\n]*\\n[\\s\\S]*?```text\\n(?<prompt>[\\s\\S]*?)```", RegexOptions.CultureInvariant)]
    private static partial Regex AnnexB();

    [GeneratedRegex("\\{(?<name>[A-Za-z0-9_]+)(?::[^}]*)?\\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
