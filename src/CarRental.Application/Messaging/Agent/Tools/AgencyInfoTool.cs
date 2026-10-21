using CarRental.Application.Common.Persistence;
using CarRental.Domain.Messaging.Agent;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging.Agent.Tools;

/// <summary>
/// <c>infos_agence</c>: the answers of the knowledge base <b>validated by a manager</b> that match the subject (topic,
/// keywords, title; in the customer's language when written), and the facts of the referential (name, address, phone,
/// opening hours, pick-up places) — plus the deposits of the categories when the subject is the deposit. A draft, a
/// changed answer not validated again or a deactivated one is never returned: nothing unvalidated reaches a customer.
/// </summary>
internal sealed class AgencyInfoTool(IAppDbContext db) : IAgentTool
{
    public const string ToolName = "infos_agence";

    public const int MaxAnswers = 3;

    /// <summary>Usual words of a subject, by topic key of the seeded entries (the keywords of an entry add their own).</summary>
    private static readonly (string Topic, string[] Words)[] Synonyms =
    [
        ("documents", ["document", "papier", "piece", "cin", "passeport", "permis", "licence", "license", "وثائق", "رخصة", "جواز", "بطاقة"]),
        ("caution", ["caution", "depot", "deposit", "garantie", "franchise", "ضمان", "كفالة"]),
        ("paiement", ["paiement", "payer", "carte", "payment", "pay", "card", "clic to pay", "دفع", "خلاص"]),
        ("confirmation", ["confirmation", "confirme", "valide", "validation", "confirmed", "تاكيد"]),
        ("annulation", ["annul", "cancel", "rembours", "refund", "الغاء"]),
        ("territoire", ["territoire", "frontiere", "algerie", "libye", "sortir", "border", "abroad", "خارج"]),
        ("aller_simple", ["aller simple", "one way", "autre lieu", "another location", "rendre ailleurs"]),
        ("prix", ["prix final", "ce qui est inclus", "inclus", "included", "tva", "contribution"]),
        ("age", ["age", "jeune conducteur", "young driver", "anciennete", "years of licence", "سن"]),
        ("conditions", ["condition", "kilometrage", "kilometre", "assurance", "insurance", "mileage", "carburant", "fuel", "retard", "late", "شروط", "تامين"]),
    ];

    private static readonly string[] DepositWords = ["caution", "depot", "deposit", "franchise", "ضمان", "كفالة"];

    public string Name => ToolName;

    public string Description =>
        "Donne les réponses de l'agence validées par le gérant sur un sujet (documents requis, caution, paiement, confirmation, "
        + "annulation, conditions, âge, sortie du territoire…) et les faits de l'agence (nom, adresse, téléphone, horaires, lieux "
        + "de prise en charge). Reformule fidèlement, sans rien ajouter ; si rien ne correspond, dis-le et propose un agent.";

    public string InputSchema => """
        {"type":"object","properties":{"sujet":{"type":"string","maxLength":200,"description":"Sujet ou question du client en quelques mots (documents, caution, horaires, adresse, lieux, paiement, annulation, conditions…)."}},"required":["sujet"],"additionalProperties":false}
        """;

    public async Task<AgentToolOutcome> RunAsync(ToolArguments arguments, AgentTurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(context);
        var subject = AgentText.ForMatching(arguments.String("sujet"));
        var entries = await db.Set<KnowledgeEntry>()
            .AsNoTracking()
            .Where(e => e.IsActive && e.IsValidated)
            .OrderBy(e => e.TopicKey)
            .ToListAsync(cancellationToken);
        var matches = entries
            .Select(e => (Entry: e, Score: Score(e, subject)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Entry.TopicKey, StringComparer.Ordinal)
            .Take(MaxAnswers)
            .Select(m => m.Entry)
            .ToList();

        foreach (var entry in matches)
        {
            context.Remember(AmountMentions.Find(entry.AnswerFor(context.Language)).Where(m => m.HasCurrencyMarker).SelectMany(m => m.Candidates));
        }

        var agency = context.Agency;
        var deposits = DepositWords.Any(w => subject.Contains(w, StringComparison.Ordinal))
            ? agency.Categories.Select(c => new
            {
                categorie = c.Name,
                caution = AgentJson.Amount(c.Deposit),
                caution_avec_rachat_de_franchise = AgentJson.Amount(c.ReducedDeposit),
            }).ToList()
            : null;
        if (deposits is not null)
        {
            context.Remember(agency.Categories.SelectMany(c => new[] { c.Deposit, c.ReducedDeposit }).ToArray());
        }

        context.Progress = true;
        return new AgentToolOutcome(new
        {
            faits_agence = new
            {
                nom = agency.Agency.Name,
                ville = agency.Agency.City,
                adresse = agency.Address,
                telephone = agency.Agency.Phone,
                email = agency.Agency.Email,
                horaires = agency.OpeningHoursText,
                lieux = agency.Locations.Select(l => new { code = l.Code, nom = l.Name, type = LocationResolver.KindName(l.Kind), adresse = l.Address }).ToList(),
            },
            reponses_validees = matches.Select(e => new { sujet = e.TopicKey, titre = e.Title, texte = e.AnswerFor(context.Language) }).ToList(),
            cautions_par_categorie = deposits,
            sujets_disponibles = matches.Count == 0 ? entries.Select(e => e.Title).ToList() : null,
            note = matches.Count == 0
                ? "Aucune réponse validée ne correspond : n'invente rien ; réponds avec les faits ci-dessus s'ils suffisent, sinon propose un agent."
                : "Réponses validées par l'agence : reformule-les fidèlement, sans rien ajouter.",
        });
    }

    private static int Score(KnowledgeEntry entry, string subject)
    {
        if (subject.Length == 0)
        {
            return 0;
        }

        var score = 0;
        var topic = entry.TopicKey.Replace('_', ' ');
        if (subject.Contains(topic, StringComparison.Ordinal) || topic.Contains(subject, StringComparison.Ordinal))
        {
            score += 10;
        }

        foreach (var (key, words) in Synonyms)
        {
            if (key == entry.TopicKey && words.Any(w => Mentions(subject, AgentText.ForMatching(w))))
            {
                score += 8;
            }
        }

        foreach (var keyword in (entry.Keywords ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = AgentText.ForMatching(keyword);
            if (normalized.Length >= 3 && subject.Contains(normalized, StringComparison.Ordinal))
            {
                score += 5;
            }
        }

        foreach (var word in AgentText.ForMatching(entry.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 5))
        {
            if (subject.Contains(word, StringComparison.Ordinal))
            {
                score++;
            }
        }

        return score;
    }

    /// <summary>
    /// Whether the subject mentions <paramref name="word"/>: a phrase anywhere, a short word (three letters or less) as a
    /// whole word (« âge », not « agence »), a longer one as the start of a word (« annul » in « annulation »).
    /// </summary>
    private static bool Mentions(string subject, string word)
    {
        // Phrases, and Arabic words (Arabic attaches its article and prepositions: الضمان, بالضمان), anywhere.
        if (word.Contains(' ', StringComparison.Ordinal) || word.Any(AgentText.IsArabicLetter))
        {
            return subject.Contains(word, StringComparison.Ordinal);
        }

        var tokens = subject.Split([' ', ',', '.', '?', '!', ';', ':', '\'', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        return word.Length <= 3
            ? tokens.Contains(word, StringComparer.Ordinal)
            : tokens.Any(t => t.StartsWith(word, StringComparison.Ordinal));
    }
}
