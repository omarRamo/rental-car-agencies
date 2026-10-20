using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// E-mail addresses, trimmed and lower-cased (the key used to find a person again). The check is structural and
/// deliberately simple (one '@', a local part of at most 64 characters, a dotted domain): delivery proves the rest.
/// </summary>
public static class EmailAddresses
{
    public const int MaxLength = 254;
    private const int LocalPartMaxLength = 64;

    public static Result<string> Normalize(string? value)
    {
        var email = value?.Trim().ToLowerInvariant();
        if (email is null || !IsValid(email))
        {
            return CustomersErrors.EmailInvalid;
        }

        return email;
    }

    private static bool IsValid(string email)
    {
        if (email.Length is < 3 or > MaxLength || email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or '"' or '(' or ')' or '[' or ']' or '\\'))
        {
            return false;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != email.LastIndexOf('@') || at > LocalPartMaxLength)
        {
            return false;
        }

        var domain = email[(at + 1)..];
        return domain.Length > 2
            && domain.Contains('.', StringComparison.Ordinal)
            && !domain.StartsWith('.')
            && !domain.EndsWith('.')
            && !domain.StartsWith('-')
            && !domain.Contains("..", StringComparison.Ordinal);
    }
}
