using System.Text.RegularExpressions;

namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// Detects a reply that promises or states the confirmation of a booking (« votre réservation est confirmée », « c'est
/// confirmé », « booking confirmed », « تم تأكيد الحجز », « réservation mte3ek t'confirmat »…): the bot never confirms,
/// an agent validates the request (PROMPT annexe B). A negated statement (« n'est pas encore confirmée », « not
/// confirmed yet », « لم يتم الحجز ») is no promise, nor a future one under a condition (« sera confirmée après
/// validation par un agent »).
/// </summary>
public static partial class ConfirmationPromises
{
    private static readonly string[] Negations =
    [
        " pas ", " pas.", " non ", " jamais", "n'est", "n'a ", "ne sera", "n'ont", "n'etait", "en attente", " not ", "n't", " never",
        " yet", "pending", "ليس", "لم ", " لا ", "غير", "mouch", "mech ", "mazel", "machi", "mahich", " ma t", "ma 7", "ma h",
    ];

    private static readonly string[] Conditions =
    [
        "apres", "une fois", " si ", "des que", "lorsque", "quand", "after", "once", " if ", " when", "بعد", "عند", "حال", "ba3d", " ki ", " kif ",
    ];

    /// <summary>The first promise of a confirmation in <paramref name="text"/>, or <see langword="null"/>.</summary>
    public static string? Find(string? text)
    {
        var normalized = " " + AgentText.ForMatching(text) + " ";
        if (normalized.Length <= 2)
        {
            return null;
        }

        foreach (var (pattern, future) in Patterns())
        {
            foreach (Match match in pattern.Matches(normalized))
            {
                if (IsNegated(normalized, match) || (future && IsConditioned(normalized, match)))
                {
                    continue;
                }

                return match.Value.Trim();
            }
        }

        return null;
    }

    public static bool Contains(string? text) => Find(text) is not null;

    private static bool IsNegated(string text, Match match)
    {
        var start = Math.Max(0, match.Index - 15);
        var window = text[start..(match.Index + match.Length)];
        return Negations.Any(n => window.Contains(n, StringComparison.Ordinal));
    }

    private static bool IsConditioned(string text, Match match)
    {
        var end = Math.Min(text.Length, match.Index + match.Length + 45);
        var window = text[match.Index..end];
        return Conditions.Any(c => window.Contains(c, StringComparison.Ordinal));
    }

    private static IEnumerable<(Regex Pattern, bool Future)> Patterns()
    {
        yield return (FrenchStatePresent(), false);
        yield return (FrenchStateFuture(), true);
        yield return (FrenchValidatedPresent(), false);
        yield return (FrenchItIs(), false);
        yield return (FrenchFirstPerson(), false);
        yield return (FrenchHeadline(), false);
        yield return (FrenchYouAre(), false);
        yield return (EnglishStatePresent(), false);
        yield return (EnglishStateFuture(), true);
        yield return (EnglishYouAre(), false);
        yield return (EnglishItIs(), false);
        yield return (EnglishFirstPerson(), false);
        yield return (ArabicDone(), false);
        yield return (ArabicConfirmed(), false);
        yield return (ArabicFuture(), true);
        yield return (DarijaState(), false);
        yield return (DarijaItIs(), false);
        yield return (DarijaFirstPerson(), false);
        yield return (DarijaArabicScript(), false);
    }

    private const string FrenchSubject = @"(?<![\p{L}])(?:reservation|location|demande|commande|voiture|vehicule|dossier)(?![\p{L}])[^.!?\n]{0,40}?(?<![\p{L}])";

    [GeneratedRegex(FrenchSubject + @"(?:est|a ete|sont|ont ete|etait)\s+(?:(?:bien|maintenant|desormais|deja|definitivement|officiellement)\s+)?(?:confirmee?s?|acceptee?s?|reservee?s?|bloquee?s?)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchStatePresent();

    [GeneratedRegex(FrenchSubject + @"(?:sera|seront)\s+(?:(?:bien|definitivement|automatiquement)\s+)?(?:confirmee?s?|acceptee?s?|reservee?s?|bloquee?s?)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchStateFuture();

    [GeneratedRegex(FrenchSubject + @"(?:est|a ete|sont|ont ete)\s+(?:bien\s+)?validee?s?(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchValidatedPresent();

    [GeneratedRegex(@"(?<![\p{L}])c'?est\s+(?:bien\s+|tout\s+)?(?:confirme|reserve|bon pour (?:la|votre) reservation)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchItIs();

    [GeneratedRegex(@"(?<![\p{L}])(?:j'?ai\s+(?:bien\s+)?(?:confirme|reserve|bloque|valide)|je\s+(?:vous\s+)?(?:confirme|reserve|bloque))\s+(?:votre|la|ta|cette|une|le)\s+(?:reservation|location|voiture|demande|vehicule)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchFirstPerson();

    [GeneratedRegex(@"(?<![\p{L}])reservation\s+(?:confirmee|validee|acceptee)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchHeadline();

    [GeneratedRegex(@"(?<![\p{L}])vous\s+etes\s+(?:bien\s+)?(?:reserve|inscrit|confirme)e?s?(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex FrenchYouAre();

    private const string EnglishSubject = @"(?<![\p{L}])(?:booking|reservation|rental|request|car|vehicle)(?![\p{L}])[^.!?\n]{0,40}?";

    [GeneratedRegex(EnglishSubject + @"(?:(?<![\p{L}])(?:is|has been|was|are|have been)\s+|'s\s+)(?:(?:now|already|all|fully|officially)\s+)?(?:confirmed|booked|reserved|secured|approved|accepted)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishStatePresent();

    [GeneratedRegex(EnglishSubject + @"(?<![\p{L}])will be\s+(?:(?:fully|automatically)\s+)?(?:confirmed|booked|reserved|secured|approved|accepted)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishStateFuture();

    [GeneratedRegex(@"(?<![\p{L}])you(?:'re| are)\s+(?:all\s+)?(?:booked|set|confirmed)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishYouAre();

    [GeneratedRegex(@"(?<![\p{L}])(?:it|that)(?:'s| is)\s+(?:confirmed|booked|reserved)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishItIs();

    [GeneratedRegex(@"(?<![\p{L}])i(?:'ve| have)?\s+(?:confirmed|booked|reserved|secured)\s+(?:your|the|it|a|this)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishFirstPerson();

    // Arabic, normalized by AgentText.ForMatching: hamza seats are plain letters (تأكيد → تاكيد, مؤكد → موكد).
    [GeneratedRegex(@"(?<![\p{L}])تم\s+(?:ال)?(?:تاكيد\s+(?:ال)?(?:حجز|طلب)|حجز)", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicDone();

    [GeneratedRegex(@"(?:حجزك|حجزكم|الحجز|طلبك)\s+(?:موكد|اكيد|تم تاكيده)|(?:اكدت|قمت\s+بتاكيد)\s+(?:ال)?(?:حجز|طلب)|(?:السيارة|الكرهبة)\s+(?:محجوزة|محجوزه)", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicConfirmed();

    [GeneratedRegex(@"(?<![\p{L}])سيتم\s+(?:ال)?تاكيد\s+(?:ال)?(?:حجز|طلب)", RegexOptions.CultureInvariant)]
    private static partial Regex ArabicFuture();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:reservation|lkarhba|el karhba|karhba|demande)(?![\p{L}])[^.!?\n]{0,30}?(?:t'?confirm(?:at|et)|confirmat|mconfirm(?:ya|ia|i|e)|t'?7ajz(?:et|at)|t'?hajz(?:et|at)|m7jouza|ma7jouza|mahjouza|t'?valid(?:at|et))(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex DarijaState();

    [GeneratedRegex(@"(?<![\p{L}])(?:c'?est|saye|ca y est|yezzi)\s+(?:bon\s+)?(?:confirme|reserve|7jaztlek|hjaztlek)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex DarijaItIs();

    [GeneratedRegex(@"(?<![\p{L}])(?:(?:rani|ana|ena)\s+(?:7jazt|hjazt|confirmit|confirmet|3malt|3mlt)(?:lek)?\s+(?:el\s+|l')?(?:reservation|karhba)|7jaztlek|hjaztlek|7jaztelek|confirmitlek)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex DarijaFirstPerson();

    [GeneratedRegex(@"(?:الحجز|حجزك|الكرهبة)\s+(?:تحجزت|تاكدت|تكونفيرمات)", RegexOptions.CultureInvariant)]
    private static partial Regex DarijaArabicScript();
}

/// <summary>
/// Detects a reply asking the customer to send a photo or a scan of an identity document, a driving licence or a bank
/// card on WhatsApp: documents are uploaded on the secure link only (invariant 9, PROMPT annexe B).
/// </summary>
public static partial class DocumentRequests
{
    public static bool Contains(string? text)
    {
        var normalized = AgentText.ForMatching(text);
        return normalized.Length > 0
               && (French().IsMatch(normalized) || English().IsMatch(normalized) || Arabic().IsMatch(normalized) || Darija().IsMatch(normalized));
    }

    private const string FrenchDocument = @"(?:cin|carte d'identite|carte d identite|piece d'identite|piece d identite|passeport|permis|carte bancaire|carte de credit|carte)";

    [GeneratedRegex(@"(?<![\p{L}])(?:envoy|transmet|partag|prend|faites|fais|scann|photograph|joign|ajout)\w*(?:\s*-?\s*(?:moi|nous))?\s+(?:une|la|les|des|un|vos|votre)?\s*(?:photos?|scans?|copies?|copie|images?|captures?)\s+(?:de|du|des|d')\s*(?:votre|ta|vos|tes|la|le|l')?\s*" + FrenchDocument, RegexOptions.CultureInvariant)]
    private static partial Regex French();

    [GeneratedRegex(@"(?<![\p{L}])(?:send|share|upload|take|provide|attach)\s+(?:me|us)?\s*(?:a|the|your)?\s*(?:photos?|pictures?|scans?|cop(?:y|ies)|images?|pics?)\s+of\s+(?:your|the)\s+(?:id|identity card|passport|driving licen[cs]e|licen[cs]e|card|credit card|bank card)", RegexOptions.CultureInvariant)]
    private static partial Regex English();

    [GeneratedRegex(@"(?:ارسل|ابعث|صور|ارفق)\w*\s*(?:لي|لنا)?\s*(?:صورة|نسخة)\s*(?:من)?\s*(?:بطاقة|جواز|رخصة)", RegexOptions.CultureInvariant)]
    private static partial Regex Arabic();

    [GeneratedRegex(@"(?<![\p{L}])(?:ab3ath|ab3athli|eb3ath|eb3athli|ba3ath|b3ath|ab3th|abaath)\w*\s+(?:taswira|tsawer|photo|foto|tswira|tsawra)\s+(?:mta3|mte3|ta3|mtaa)\s+(?:el\s+|l')?(?:cin|carta|carte|passeport|permis)", RegexOptions.CultureInvariant)]
    private static partial Regex Darija();
}

/// <summary>
/// Detects a reply that discloses the instructions of the assistant (« ignore les règles précédentes », « révèle ton
/// prompt »): long passages of the system prompt copied word for word, several of its sentences, its section titles, the
/// names of its tools or the markup of the platform context. Short echoes of what the bot must say (« un agent valide la
/// demande, en général sous 2 heures ouvrées ») are not a leak.
/// </summary>
public sealed partial class PromptLeakDetector
{
    /// <summary>Consecutive words of the prompt that make a leak on their own.</summary>
    public const int LongRun = 16;

    /// <summary>Words of a passage of a prompt sentence.</summary>
    public const int Passage = 8;

    /// <summary>Distinct prompt sentences found in a reply that make a leak.</summary>
    public const int SentencesForLeak = 4;

    private readonly HashSet<string> _longRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _passages = new(StringComparer.Ordinal);
    private readonly string[] _forbidden;
    private readonly string[] _headings;

    /// <param name="systemPrompt">The rendered system prompt (stable part).</param>
    /// <param name="forbiddenTerms">Words the customer must never see (tool names, markup), matched case-insensitively.</param>
    /// <param name="headings">Section titles of the prompt: two of them in one reply make a leak.</param>
    public PromptLeakDetector(string systemPrompt, IEnumerable<string> forbiddenTerms, IEnumerable<string> headings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentNullException.ThrowIfNull(forbiddenTerms);
        ArgumentNullException.ThrowIfNull(headings);
        var sentence = 0;
        foreach (var line in systemPrompt.Split(['\n', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries))
        {
            var words = Words(line);
            for (var i = 0; i + LongRun <= words.Count; i++)
            {
                _longRuns.Add(string.Join(' ', words.Skip(i).Take(LongRun)));
            }

            for (var i = 0; i + Passage <= words.Count; i++)
            {
                _passages.TryAdd(string.Join(' ', words.Skip(i).Take(Passage)), sentence);
            }

            sentence++;
        }

        // Runs that cross sentence boundaries too: a leak copies the prompt as a whole.
        var all = Words(systemPrompt);
        for (var i = 0; i + LongRun <= all.Count; i++)
        {
            _longRuns.Add(string.Join(' ', all.Skip(i).Take(LongRun)));
        }

        _forbidden = [.. forbiddenTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(AgentText.ForMatching)];
        _headings = [.. headings.Where(h => !string.IsNullOrWhiteSpace(h)).Select(AgentText.ForMatching)];
    }

    public bool Leaks(string? reply)
    {
        var normalized = AgentText.ForMatching(reply);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (_forbidden.Any(term => normalized.Contains(term, StringComparison.Ordinal))
            || _headings.Count(heading => normalized.Contains(heading, StringComparison.Ordinal)) >= 2)
        {
            return true;
        }

        var words = Words(reply ?? string.Empty);
        var sentences = new HashSet<int>();
        for (var i = 0; i + Passage <= words.Count; i++)
        {
            if (i + LongRun <= words.Count && _longRuns.Contains(string.Join(' ', words.Skip(i).Take(LongRun))))
            {
                return true;
            }

            if (_passages.TryGetValue(string.Join(' ', words.Skip(i).Take(Passage)), out var sentence))
            {
                sentences.Add(sentence);
            }
        }

        return sentences.Count >= SentencesForLeak;
    }

    private static List<string> Words(string text) =>
        [.. WordPattern().Matches(AgentText.ForMatching(text)).Select(m => m.Value)];

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();
}
