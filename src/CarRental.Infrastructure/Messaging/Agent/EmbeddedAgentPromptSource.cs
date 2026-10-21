using System.Reflection;
using CarRental.Application.Messaging.Agent;

namespace CarRental.Infrastructure.Messaging.Agent;

/// <summary>
/// The system prompt of the WhatsApp agent from the embedded resources of <c>Messaging/Prompts</c>: <c>whatsapp-agent.md</c>
/// (PROMPT.md annexe B, verbatim — a test compares them) and <c>whatsapp-agent-addendum.md</c>. Read once.
/// </summary>
internal sealed class EmbeddedAgentPromptSource : IAgentPromptSource
{
    public const string TemplateResource = "CarRental.Infrastructure.Messaging.Prompts.whatsapp-agent.md";
    public const string AddendumResource = "CarRental.Infrastructure.Messaging.Prompts.whatsapp-agent-addendum.md";

    private static readonly Lazy<string> Template = new(() => Read(TemplateResource));
    private static readonly Lazy<string> AddendumText = new(() => Read(AddendumResource));

    public string SystemPromptTemplate => Template.Value;

    public string Addendum => AddendumText.Value;

    internal static string Read(string resource)
    {
        using var stream = typeof(EmbeddedAgentPromptSource).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The embedded prompt {resource} is missing from {Assembly.GetExecutingAssembly().GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
