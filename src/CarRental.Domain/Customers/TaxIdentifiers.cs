using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// Tax identifier of a company payer: the Tunisian "matricule fiscal" (mandatory on invoices) or a foreign identifier.
/// </summary>
/// <remarks>
/// TODO(spec): the exact format of the matricule fiscal (7 digits, control letter, VAT code, category code and
/// secondary establishment number, e.g. <c>1234567/A/M/000</c>) and of foreign identifiers must be confirmed with the
/// chartered accountant (invoice mentions, TTN). Until then the check is loose: 5 to 20 letters or digits with at
/// least one digit, separators / - . and spaces allowed.
/// </remarks>
public static class TaxIdentifiers
{
    public const int MaxLength = 32;
    private const int KeyMinLength = 5;
    private const int KeyMaxLength = 20;

    /// <summary>The identifier as displayed (trimmed, upper case), or an error when it does not look like one.</summary>
    public static Result<string> Clean(string? value)
    {
        var cleaned = TextRules.Clean(value)?.ToUpperInvariant();
        if (cleaned is null
            || cleaned.Length > MaxLength
            || !cleaned.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '/' or '-' or '.'))
        {
            return CustomersErrors.TaxIdentifierInvalid;
        }

        var key = KeyOf(cleaned);
        if (key.Length is < KeyMinLength or > KeyMaxLength || !key.Any(char.IsAsciiDigit))
        {
            return CustomersErrors.TaxIdentifierInvalid;
        }

        return cleaned;
    }

    /// <summary>Deduplication key: ASCII letters and digits only, upper case.</summary>
    public static string KeyOf(string? value) => DocumentNumbers.KeyOf(value);
}
