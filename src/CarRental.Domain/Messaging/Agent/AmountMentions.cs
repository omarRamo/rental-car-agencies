using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// An amount of money found in a text: the number as written, where, the values it may stand for (<c>681,400</c> is
/// 681.4 dinars in Tunisia or 681 400 elsewhere), and whether a currency marker came with it — a foreign one (euro,
/// dollar) can never match a price of the engine, which quotes in TND.
/// </summary>
public sealed record AmountMention(string Text, int Index, IReadOnlyList<decimal> Candidates, bool HasCurrencyMarker, bool IsForeignCurrency);

/// <summary>
/// Finds the amounts of money in a reply of the WhatsApp agent (French, Arabic — with Arabic-Indic digits and
/// <c>د.ت</c> —, darija in Latin letters, English): numbers next to a currency marker (TND, DT, dinars, millimes,
/// دينار, د.ت, €, EUR, $), and numbers of 20 or more in a price context (prix, total, caution, price, deposit, سعر,
/// المبلغ, 9adech…). Dates, times, phone numbers, booking numbers, links, percentages and counted quantities
/// (5 jours, 4 personnes, 2 valises, 200 km) are never amounts.
/// </summary>
/// <remarks>Amounts written in words are not detected (the model writes the figures returned by its tools).</remarks>
public static partial class AmountMentions
{
    /// <summary>A number of a price context without currency marker counts from this value (car rental amounts).</summary>
    public const decimal MinimumContextAmount = 20m;

    /// <summary>Replaces what can never be an amount (a private-use character no pattern matches).</summary>
    private const char Mask = '';

    public static IReadOnlyList<AmountMention> Find(string? text)
    {
        var normalized = AgentText.NormalizeDigits(text);
        if (normalized.Length == 0)
        {
            return [];
        }

        var masked = new StringBuilder(normalized);
        foreach (var pattern in new[]
                 {
                     UrlPattern(), EmailPattern(), BookingNumberPattern(), IsoDatePattern(), SlashDatePattern(), DottedDatePattern(),
                     DateRangePattern(), DayMonthPattern(), TimePattern(), PhonePattern(), PercentPattern(),
                 })
        {
            MaskMatches(masked, pattern);
        }

        var mentions = new List<AmountMention>();
        var current = masked.ToString();
        foreach (var pattern in new[] { AmountBeforeMarkerPattern(), MarkerBeforeAmountPattern() })
        {
            foreach (Match match in pattern.Matches(current))
            {
                var number = match.Groups["number"];
                var marker = match.Groups["marker"].Value;
                var millimes = MillimesMarker().IsMatch(marker);
                var foreign = ForeignMarker().IsMatch(marker);
                var candidates = Candidates(number.Value, millimes);
                if (candidates.Count > 0)
                {
                    mentions.Add(new AmountMention(number.Value.Trim(), number.Index, candidates, HasCurrencyMarker: true, foreign));
                }

                for (var i = match.Index; i < match.Index + match.Length; i++)
                {
                    masked[i] = Mask;
                }
            }

            current = masked.ToString();
        }

        MaskMatches(masked, QuantityPattern());
        MaskMatches(masked, ListMarkerPattern(), group: "number");
        MaskMatches(masked, NumberedThingPattern(), group: "number");
        current = masked.ToString();

        foreach (Match match in NumberPattern().Matches(current))
        {
            if (!IsInPriceContext(normalized, match.Index, match.Length))
            {
                continue;
            }

            var candidates = Candidates(match.Value, millimes: false);
            if (candidates.Count == 0 || candidates.Max() < MinimumContextAmount || IsYear(match.Value))
            {
                continue;
            }

            mentions.Add(new AmountMention(match.Value.Trim(), match.Index, candidates, HasCurrencyMarker: false, IsForeignCurrency: false));
        }

        return [.. mentions.OrderBy(m => m.Index)];
    }

    /// <summary>
    /// The values a written number may stand for: <c>681,400</c> and <c>681.400</c> (three decimals: TND) are 681.4 or
    /// 681 400; <c>681 400</c> is 681 400 or 681.4 (dinars and millimes); <c>681400</c> is 681 400 or 681.4 (millimes
    /// without separator); <c>1.250,500</c> is 1 250.5. Zero is never an amount.
    /// </summary>
    public static IReadOnlyList<decimal> Candidates(string written, bool millimes)
    {
        ArgumentNullException.ThrowIfNull(written);
        var token = written.Trim();
        var values = new HashSet<decimal>();
        ParseVariants(token.Replace(" ", string.Empty, StringComparison.Ordinal), values);
        var groups = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length > 1)
        {
            // A list number or a quantity may stand before the amount: "2 681,400" also reads 681,400.
            for (var start = 1; start < groups.Length; start++)
            {
                ParseVariants(string.Concat(groups[start..]), values);
            }

            // "681 400": 681 dinars and 400 millimes.
            if (groups.Length == 2 && groups[1].Length == 3 && groups[1].All(char.IsAsciiDigit) && groups[0].All(char.IsAsciiDigit)
                && decimal.TryParse($"{groups[0]}.{groups[1]}", NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dinars))
            {
                values.Add(dinars);
            }
        }

        var result = millimes ? values.Select(v => v / 1000m) : values.Concat(values.Where(IsWholeWithFourDigitsOrMore).Select(v => v / 1000m));
        return [.. result.Where(v => v > 0m).Distinct().Order()];
    }

    private static bool IsWholeWithFourDigitsOrMore(decimal value) => value == decimal.Truncate(value) && value >= 1000m;

    private static void ParseVariants(string token, HashSet<decimal> values)
    {
        if (token.Length == 0 || !token.All(c => char.IsAsciiDigit(c) || c is '.' or ','))
        {
            return;
        }

        var dots = token.Count(c => c == '.');
        var commas = token.Count(c => c == ',');
        if (dots == 0 && commas == 0)
        {
            Add(token, values);
            return;
        }

        if (dots > 0 && commas > 0)
        {
            // The last separator is the decimal one, the others group thousands: 1.250,500 or 1,250.500.
            var last = Math.Max(token.LastIndexOf('.'), token.LastIndexOf(','));
            var integer = token[..last].Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
            Add($"{integer}.{token[(last + 1)..]}", values);
            return;
        }

        var separator = dots > 0 ? '.' : ',';
        var parts = token.Split(separator);
        if (parts.Any(p => p.Length == 0))
        {
            return;
        }

        if (parts.Length == 2)
        {
            Add($"{parts[0]}.{parts[1]}", values);
            if (parts[1].Length == 3)
            {
                Add(parts[0] + parts[1], values);
            }

            return;
        }

        if (parts.Skip(1).All(p => p.Length == 3))
        {
            Add(string.Concat(parts), values);
        }
    }

    private static void Add(string invariant, HashSet<decimal> values)
    {
        if (decimal.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            values.Add(value);
        }
    }

    private static bool IsYear(string written) =>
        int.TryParse(written.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2100;

    private static bool IsInPriceContext(string text, int index, int length)
    {
        var start = Math.Max(0, index - 40);
        var end = Math.Min(text.Length, index + length + 20);
        return PriceContextPattern().IsMatch(AgentText.ForMatching(text[start..end]));
    }

    private static void MaskMatches(StringBuilder text, Regex pattern, string? group = null)
    {
        foreach (Match match in pattern.Matches(text.ToString()))
        {
            var target = group is null ? (Capture)match : match.Groups[group];
            for (var i = target.Index; i < target.Index + target.Length; i++)
            {
                text[i] = Mask;
            }
        }
    }

    // A number as written: grouped by spaces (681 400, 1 250,500), by dots or commas (1.250.000, 1,250.500), or plain
    // with up to three decimals (681,400, 681.4, 681400). Never glued to other digits or separators.
    private const string Number =
        @"(?<![\d.,])(?:\d{1,3}(?: \d{3})+(?:[.,]\d{1,3})?|\d{1,3}(?:[.,]\d{3}){2,}(?:[.,]\d{1,3})?|\d+(?:[.,]\d{1,3})?)(?![\d]|[.,]\d)";

    private const string Marker =
        @"TND|DT|D\.T\.?|dinars?|millimes?|mill?|€|EUR|euros?|\$|USD|dollars?|د\.?\s?ت\.?|دينارا?|دنانير|مليم";

    [GeneratedRegex(@"(?<number>" + Number + @")\s?(?<marker>" + Marker + @")(?![\p{L}\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmountBeforeMarkerPattern();

    [GeneratedRegex(@"(?<![\p{L}\d])(?<marker>TND|DT|D\.T\.?|€|EUR|\$|USD|د\.?\s?ت\.?|دينار)\s?(?<number>" + Number + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerBeforeAmountPattern();

    [GeneratedRegex(Number, RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^(?:millimes?|mill?|مليم)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MillimesMarker();

    [GeneratedRegex(@"^(?:€|EUR|euros?|\$|USD|dollars?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForeignMarker();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[A-Z]{2,4}-\d{4}-\d{3,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BookingNumberPattern();

    [GeneratedRegex(@"\b\d{4}-\d{1,2}-\d{1,2}(?:[T ]\d{1,2}:\d{2}(?::\d{2})?)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDatePattern();

    [GeneratedRegex(@"(?<![\d.,])\d{1,2}[/\-]\d{1,2}(?:[/\-]\d{2,4})?(?![\d.,]?\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SlashDatePattern();

    [GeneratedRegex(@"(?<![\d.,])\d{1,2}\.\d{1,2}\.\d{2,4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DottedDatePattern();

    [GeneratedRegex(
        @"(?<![\d.,])\d{1,2}(?:er|e|st|nd|rd|th)?\s+(?:janvier|f[ée]vrier|mars|avril|mai|juin|juillet|ao[uû]t|septembre|octobre|novembre|d[ée]cembre|janv|f[ée]vr|avr|juil|d[ée]c|january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept?|oct|nov|dec|جانفي|فيفري|مارس|أفريل|افريل|ماي|جوان|جويلية|أوت|اوت|سبتمبر|أكتوبر|اكتوبر|نوفمبر|ديسمبر|يناير|فبراير|أبريل|ابريل|مايو|يونيو|يوليو|أغسطس|اغسطس)(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthPattern();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:du|from|between|entre|من|men|mel)\s+\d{1,2}\s+(?:au|to|and|et|à|a|-|إلى|الى|lel|l)\s+\d{1,2}(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRangePattern();

    [GeneratedRegex(@"(?<![\d.,])\d{1,2}\s?[:hH]\s?\d{2}(?!\d)|(?<![\d.,])\d{1,2}\s?(?:h|am|pm)(?![\p{L}\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"(?<![\d.,])(?:\+|00)?\d(?:[ .\-]?\d){7,}(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?\s?%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();

    [GeneratedRegex(
        @"(?<![\d.,])\d+(?:[.,]\d+)?\s?(?:jours?|j|nuits?|nuitées?|places?|personnes?|pers|passagers?|adultes?|enfants?|valises?|bagages?|km|kms|kilom[eè]tres?|ans|ann[ée]es?|mois|semaines?|heures?|minutes?|min|portes?|days?|nights?|seats?|people|persons?|passengers?|adults?|kids?|children|bags?|suitcases?|luggage|doors?|years?|months?|weeks?|hours?|minutes?|ayem|ayam|nhar|يوم|أيام|ايام|ليال|ليلة|ليالي|مقاعد|مقعد|أشخاص|اشخاص|شخص|ركاب|راكب|حقائب|حقيبة|كلم|كم|سنة|سنوات|ساعة|ساعات|دقيقة|دقائق|أبواب|ابواب)(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantityPattern();

    [GeneratedRegex(@"(?m)^\s*(?<number>\d{1,2})\s?[.)\-]\s", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarkerPattern();

    [GeneratedRegex(
        @"(?:option|choix|n°|no\.?|num[ée]ro|number|#|cat[ée]gorie|category|[ée]tape|step|vol|flight|chambre|room|خيار|رقم|اختيار)\s?(?<number>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedThingPattern();

    // Words of a price context, matched on the text lowered without accents (AgentText.ForMatching): whole Latin words,
    // Arabic stems anywhere (Arabic attaches its prepositions and articles: بسعر, والمبلغ).
    [GeneratedRegex(
        @"(?<![\p{L}])(?:prix|tarifs?|total|totale|cout|couts|coute|coutent|montants?|cautions?|depots?|remises?|reductions?|payer|payez|paiement|acompte|frais|supplements?|price|prices|cost|costs|totals?|deposits?|fees?|pay|charge|charged|rates?|discounts?|soum|essoum|flous|9adech|kadech|ch7al|chhal|yeklef|tekhles)(?![\p{L}])|سعر|ثمن|مبلغ|تكلفة|كلفة|ضمان|كفالة|خصم|تخفيض|يكلف",
        RegexOptions.CultureInvariant)]
    private static partial Regex PriceContextPattern();
}

/// <summary>
/// The amount guardrail of the WhatsApp agent (PROMPT §6, invariant 10): every amount of a reply must be an amount the
/// tools returned in the conversation (prices, deposits, lines of a quote, amounts of validated answers). A mention
/// matches when one of the values it may stand for equals a known amount to the millime, or is that amount rounded to
/// the dinar (681 for 681.400). A foreign currency never matches: the engine quotes in TND.
/// </summary>
public static class AmountGuard
{
    private const decimal Tolerance = 0.0005m;

    /// <summary>The mentions of <paramref name="reply"/> that match no known amount (empty: the reply may leave).</summary>
    public static IReadOnlyList<AmountMention> Unknown(string? reply, IEnumerable<decimal> knownAmounts)
    {
        ArgumentNullException.ThrowIfNull(knownAmounts);
        var known = knownAmounts.Select(Math.Abs).Where(k => k > 0m).Distinct().ToList();
        return [.. AmountMentions.Find(reply).Where(m => !Matches(m, known))];
    }

    public static bool Matches(AmountMention mention, IReadOnlyCollection<decimal> knownAmounts)
    {
        ArgumentNullException.ThrowIfNull(mention);
        ArgumentNullException.ThrowIfNull(knownAmounts);
        if (mention.IsForeignCurrency)
        {
            return false;
        }

        foreach (var candidate in mention.Candidates)
        {
            foreach (var known in knownAmounts)
            {
                if (Math.Abs(candidate - known) <= Tolerance)
                {
                    return true;
                }

                if (candidate == decimal.Truncate(candidate) && known >= 1m
                    && (candidate == Math.Round(known, 0, MidpointRounding.AwayFromZero) || candidate == decimal.Floor(known)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
