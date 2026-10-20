using System.Globalization;
using System.Text.RegularExpressions;

namespace CarRental.Domain.Common;

/// <summary>
/// A readable document number: prefix, fiscal year and a sequence padded to six digits,
/// e.g. <c>LOC-2027-000123</c>, <c>FAC-2027-000045</c>, <c>AV-2027-000003</c>.
/// </summary>
public sealed partial record DocumentNumber
{
    private DocumentNumber(DocumentType type, int year, long sequence)
    {
        Type = type;
        Year = year;
        Sequence = sequence;
        Value = string.Create(CultureInfo.InvariantCulture, $"{PrefixOf(type)}-{year:D4}-{sequence:D6}");
    }

    public DocumentType Type { get; }

    /// <summary>Fiscal year (calendar year in Africa/Tunis).</summary>
    public int Year { get; }

    /// <summary>Position in the yearly sequence, starting at 1.</summary>
    public long Sequence { get; }

    public string Value { get; }

    public static string PrefixOf(DocumentType type) => type switch
    {
        DocumentType.Booking => "LOC",
        DocumentType.Invoice => "FAC",
        DocumentType.CreditNote => "AV",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown document type."),
    };

    public static Result<DocumentNumber> Create(DocumentType type, int year, long sequence)
    {
        if (!Enum.IsDefined(type))
        {
            return DocumentNumberErrors.UnknownType;
        }

        if (year is < 2000 or > 9999)
        {
            return DocumentNumberErrors.InvalidYear;
        }

        if (sequence < 1)
        {
            return DocumentNumberErrors.InvalidSequence;
        }

        return new DocumentNumber(type, year, sequence);
    }

    public static Result<DocumentNumber> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DocumentNumberErrors.InvalidFormat;
        }

        var match = Pattern().Match(value.Trim());
        if (!match.Success)
        {
            return DocumentNumberErrors.InvalidFormat;
        }

        var type = match.Groups["prefix"].Value switch
        {
            "LOC" => DocumentType.Booking,
            "FAC" => DocumentType.Invoice,
            _ => DocumentType.CreditNote,
        };

        return Create(
            type,
            int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
            long.Parse(match.Groups["sequence"].Value, CultureInfo.InvariantCulture));
    }

    public override string ToString() => Value;

    [GeneratedRegex("^(?<prefix>LOC|FAC|AV)-(?<year>[0-9]{4})-(?<sequence>[0-9]{6,18})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
