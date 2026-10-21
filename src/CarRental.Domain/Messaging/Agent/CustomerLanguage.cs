using System.Collections.Frozen;

namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// The languages the WhatsApp agent answers in (PROMPT.md annexe B: « français, arabe, darija tunisienne ou anglais »).
/// Darija is written in Latin letters with digits for some sounds (<c>3</c>, <c>7</c>, <c>9</c>); darija in Arabic
/// letters counts as <see cref="Arabic"/>.
/// </summary>
public enum AgentLanguage
{
    French = 1,
    English = 2,
    Arabic = 3,
    Darija = 4,
}

/// <summary>
/// Guesses the language of customer messages from their script and their most common words, for the deterministic
/// replies of the agent (attachments, hand-overs, refusals) and for the evaluation of the replies. The model itself
/// answers in the customer's language; this is only a fallback, French by default.
/// </summary>
public static class CustomerLanguageDetector
{
    private static readonly FrozenSet<string> FrenchWords = new[]
    {
        "je", "j'ai", "j'aimerais", "une", "un", "voiture", "bonjour", "bonsoir", "salut", "pour", "le", "la", "les", "est", "vous",
        "merci", "louer", "location", "aeroport", "combien", "disponible", "du", "au", "des", "et", "avec", "mon", "ma", "mes",
        "nous", "quel", "quelle", "prix", "reserver", "reservation", "demain", "jours", "semaine", "boite", "automatique", "manuelle",
        "personnes", "places", "oui", "non", "svp", "s'il", "plait", "c'est", "il", "y", "pas", "besoin", "veux", "voudrais",
        "souhaite", "qui", "que", "quoi", "comment", "est-ce", "depart", "retour", "agence", "caution", "papiers", "permis", "quels",
        "quelles", "faut", "faut-il", "votre", "vos", "notre", "ou",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> EnglishWords = new[]
    {
        "the", "please", "car", "cars", "rent", "rental", "hello", "hi", "hey", "i", "i'm", "i'd", "want", "need", "how", "much",
        "available", "airport", "thanks", "thank", "what", "is", "are", "do", "you", "your", "for", "from", "to", "and", "price",
        "book", "booking", "pick", "up", "return", "days", "week", "can", "would", "like", "automatic", "manual", "there", "we",
        "my", "me", "have", "get", "when", "where", "which", "deposit", "licence", "license", "tomorrow", "people", "papers",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> DarijaWords = new[]
    {
        "nheb", "n7eb", "nhebb", "n7ebb", "nhab", "7ab", "karhba", "karhaba", "kar7ba", "barcha", "behi", "bahi", "yezzi", "mte3",
        "mta3", "mtaa", "mte3ek", "mta3ek", "mte3i", "mta3i", "3andek", "andek", "3andkom", "3andi", "famma", "fama", "chniya",
        "chnowa", "chnoua", "chnia", "9adech", "kadech", "9adeh", "adeh", "9adach", "ch7al", "chhal", "kifech", "kifeh", "kifesh",
        "wakt", "waqt", "ena", "enti", "inti", "houwa", "nchallah", "inchallah", "salam", "aslema", "asslema", "ahla", "marhba",
        "labes", "labess", "choufli", "a3tini", "atini", "3aychek", "ya3tik", "sa7a", "saha", "mrigel", "mriguel", "nzid", "taw",
        "tawa", "bech", "bich", "bch", "nekri", "nkri", "lkarhba", "nhar", "ayem", "ayam", "jom3a", "sbe7", "sbah", "5ater", "khater",
        "zeda", "zada", "brabi", "3lech", "alech", "3la", "lel", "mouch", "mech", "mafamech", "famech", "9oli", "9olli", "kolli",
        "goli", "sayara", "sayyara", "tomobil", "tonobil", "ma3neha", "chbik", "rani", "nesta7a9", "n7ebbek", "baba", "ya", "khouya",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static AgentLanguage Detect(string? text) => Detect([text]);

    /// <summary>The most likely language of <paramref name="texts"/> taken together; French when nothing tells.</summary>
    public static AgentLanguage Detect(IEnumerable<string?> texts) => TryDetect(texts) ?? AgentLanguage.French;

    /// <summary>The most likely language of <paramref name="texts"/>, or <see langword="null"/> when no word nor script tells (« ok », « 10h »).</summary>
    public static AgentLanguage? TryDetect(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        int arabicLetters = 0, latinLetters = 0, french = 0, english = 0, darija = 0;
        foreach (var raw in texts)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var character in raw)
            {
                if (AgentText.IsArabicLetter(character))
                {
                    arabicLetters++;
                }
                else if (char.IsAsciiLetter(character) || character is >= 'À' and <= 'ɏ')
                {
                    latinLetters++;
                }
            }

            foreach (var word in Words(AgentText.ForMatching(raw)))
            {
                if (DarijaWords.Contains(word) || IsArabizi(word))
                {
                    darija += 2;
                }

                if (FrenchWords.Contains(word))
                {
                    french++;
                }

                if (EnglishWords.Contains(word))
                {
                    english++;
                }
            }
        }

        if (arabicLetters >= 2 && arabicLetters >= latinLetters)
        {
            return AgentLanguage.Arabic;
        }

        if (darija >= 2 && darija >= french)
        {
            return AgentLanguage.Darija;
        }

        if (french == 0 && english == 0)
        {
            return null;
        }

        return english > french ? AgentLanguage.English : AgentLanguage.French;
    }

    /// <summary>
    /// Arabic chat alphabet: a word mixing Latin letters and the digits used for Arabic sounds (<c>3andek</c>,
    /// <c>n7eb</c>, <c>9adech</c>), never a number nor an amount (<c>10h</c>, <c>2a</c> are too short).
    /// </summary>
    private static bool IsArabizi(string word)
    {
        if (word.Length < 3 || !word.Any(char.IsAsciiLetter))
        {
            return false;
        }

        var arabicSoundDigits = word.Count(c => c is '2' or '3' or '5' or '7' or '8' or '9');
        var otherDigits = word.Count(c => char.IsAsciiDigit(c) && c is not ('2' or '3' or '5' or '7' or '8' or '9'));
        return arabicSoundDigits > 0 && otherDigits == 0 && !word.EndsWith('h') && !word.EndsWith("km", StringComparison.Ordinal);
    }

    private static IEnumerable<string> Words(string normalized)
    {
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var inWord = i < normalized.Length && (char.IsLetterOrDigit(normalized[i]) || normalized[i] is '\'' or '-');
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                yield return normalized[start..i].Trim('\'', '-');
                start = -1;
            }
        }
    }
}
