namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// <c>transferer_a_un_agent</c>: the model hands the conversation over to a human (PROMPT.md annexe B, « Passer la main
/// à un agent »): a reason category and a short summary for the agent become the hand-over reason shown in the shared
/// inbox; the reply of the turn tells the customer that an agent answers soon, then the bot keeps quiet.
/// </summary>
internal sealed class TransferTool : IAgentTool
{
    public const string ToolName = "transferer_a_un_agent";

    public const int SummaryMaxLength = 300;

    /// <summary>The reason categories and their label in the shared inbox (French, like the back office).</summary>
    public static IReadOnlyDictionary<string, string> Categories { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["reclamation"] = "Réclamation",
        ["accident_panne"] = "Accident ou panne",
        ["amende"] = "Amende",
        ["litige_caution_dommages"] = "Litige sur une caution ou des dommages",
        ["modification_annulation"] = "Modification ou annulation d'une réservation",
        ["negociation_prix"] = "Négociation de prix",
        ["demande_professionnelle"] = "Demande professionnelle (société, longue durée, plusieurs véhicules)",
        ["client_mecontent"] = "Client mécontent",
        ["incomprehension"] = "Incompréhensions répétées",
        ["incertitude"] = "Réponse incertaine",
        ["autre"] = "Autre demande",
    };

    public string Name => ToolName;

    public string Description =>
        "Passe la conversation à un agent humain de l'agence ; le bot se tait ensuite. À utiliser pour : réclamation, accident ou "
        + "panne, amende, litige sur une caution ou des dommages, modification ou annulation d'une réservation, négociation de prix, "
        + "demande professionnelle (société, longue durée, plusieurs véhicules), client mécontent, deux incompréhensions de suite, "
        + "ou dès que tu n'es pas sûr de la bonne réponse.";

    public string InputSchema => """
        {"type":"object","properties":{"motif":{"type":"string","enum":["reclamation","accident_panne","amende","litige_caution_dommages","modification_annulation","negociation_prix","demande_professionnelle","client_mecontent","incomprehension","incertitude","autre"],"description":"Catégorie du motif du transfert."},"resume":{"type":"string","maxLength":300,"description":"Résumé court de la demande pour l'agent, sans donnée de carte ni de pièce d'identité."}},"required":["motif","resume"],"additionalProperties":false}
        """;

    public Task<AgentToolOutcome> RunAsync(ToolArguments arguments, AgentTurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var category = Categories.GetValueOrDefault(arguments.String("motif") ?? "autre", Categories["autre"]);
        var summary = string.Join(' ', (arguments.String("resume") ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        context.Transfer = new AgentTransfer(category, summary.Length <= SummaryMaxLength ? summary : summary[..SummaryMaxLength]);
        context.Progress = true;
        return Task.FromResult(new AgentToolOutcome(new
        {
            statut = "ok",
            consigne = "La conversation passe à un agent. Préviens le client en une phrase, dans sa langue, qu'un agent de l'agence lui répond bientôt. N'ajoute aucune autre information.",
        }));
    }
}
