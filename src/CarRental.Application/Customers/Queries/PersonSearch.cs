using System.Linq.Expressions;
using CarRental.Domain.Customers;

namespace CarRental.Application.Customers.Queries;

/// <summary>
/// Turns what an agent types in the search box into one predicate on persons (so that each person is listed once):
/// e-mail (start of the address), phone (normalized E.164, or the last digits), document number (start of the key)
/// and name (every word contained in the normalized name). Anonymized persons never match.
/// </summary>
internal static class PersonSearch
{
    private const int MinPhoneDigits = 6;
    private const int MinDocumentKeyLength = 4;

    /// <summary>The predicate for <paramref name="term"/>, or <see langword="null"/> for a blank term.</summary>
    public static Expression<Func<Person, bool>>? PredicateFor(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var text = term.Trim();
        Expression<Func<Person, bool>> predicate = person => false;

        if (text.Contains('@', StringComparison.Ordinal))
        {
            var email = text.ToLowerInvariant();
            predicate = predicate.Or(person => person.Email != null && person.Email.StartsWith(email));
        }

        if (PhoneNumbers.Normalize(text) is { IsSuccess: true } phone)
        {
            var normalized = phone.Value;
            predicate = predicate.Or(person => person.Phone == normalized);
        }

        if (LooksLikePhoneDigits(text, out var digits))
        {
            predicate = predicate.Or(person => person.Phone != null && person.Phone.EndsWith(digits));
        }

        var key = DocumentNumbers.KeyOf(text);
        if (key.Length >= MinDocumentKeyLength && key.Any(char.IsAsciiDigit))
        {
#pragma warning disable CA1310 // Translated to SQL LIKE (EF Core does not translate a StringComparison); keys are upper-case ASCII.
            predicate = predicate.Or(person => person.Documents.Any(document => document.NumberKey.StartsWith(key)));
#pragma warning restore CA1310
        }

        var words = SearchText.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            Expression<Func<Person, bool>> name = person => true;
            foreach (var word in words)
            {
                name = name.And(person => person.SearchName.Contains(word));
            }

            predicate = predicate.Or(name);
        }

        return predicate.And(person => person.AnonymizedAtUtc == null);
    }

    private static bool LooksLikePhoneDigits(string text, out string digits)
    {
        digits = new string([.. text.Where(char.IsAsciiDigit)]);
        return digits.Length >= MinPhoneDigits && text.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '.' or '-' or '(' or ')');
    }
}

/// <summary>Combines predicates for LINQ providers (one parameter, no Invoke).</summary>
internal static class PredicateExtensions
{
    public static Expression<Func<T, bool>> Or<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right) =>
        Combine(left, right, Expression.OrElse);

    public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    private static Expression<Func<T, bool>> Combine<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> combine)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(combine(left.Body, rightBody), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
