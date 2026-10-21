using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// Checks of a driver typed by a visitor (ADR-0050), on the typed values only: every field and every document is
/// validated (all the errors, for a form), then the eligibility rules run on a transient person that is never stored
/// — so the answer never depends on, nor reveals, a person already known.
/// </summary>
public static class DriverSubmissionRules
{
    /// <summary>The errors of the details (<c>Document</c> null) and of each document, in field order.</summary>
    public static IReadOnlyList<DriverSubmissionIssue> Validate(PersonDetails details, IReadOnlyList<IdentityDocumentDetails> documents, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(documents);
        var issues = new List<DriverSubmissionIssue>();
        void Add(Error error, IdentityDocumentType? document = null) => issues.Add(new DriverSubmissionIssue(document, error));

        if (!TextRules.IsValidName(TextRules.Clean(details.FirstName), Person.NameMaxLength))
        {
            Add(CustomersErrors.FirstNameInvalid);
        }

        if (!TextRules.IsValidName(TextRules.Clean(details.LastName), Person.NameMaxLength))
        {
            Add(CustomersErrors.LastNameInvalid);
        }

        if (!string.IsNullOrWhiteSpace(details.Nationality) && CountryCodes.Normalize(details.Nationality) is { IsFailure: true } nationality)
        {
            Add(nationality.Error);
        }

        if (details.BirthDate is { } birthDate && (birthDate > today || birthDate < today.AddYears(-120)))
        {
            Add(CustomersErrors.BirthDateInvalid);
        }

        if (!string.IsNullOrWhiteSpace(details.Phone) && PhoneNumbers.Normalize(details.Phone) is { IsFailure: true } phone)
        {
            Add(phone.Error);
        }

        if (!string.IsNullOrWhiteSpace(details.Email) && EmailAddresses.Normalize(details.Email) is { IsFailure: true } email)
        {
            Add(email.Error);
        }

        var seen = new HashSet<IdentityDocumentType>();
        foreach (var document in documents)
        {
            if (!seen.Add(document.Type))
            {
                Add(CustomersErrors.DocumentTypeInvalid, document.Type);
                continue;
            }

            if (IdentityDocumentFacts.From(document, today) is { IsFailure: true } facts)
            {
                Add(facts.Error, document.Type);
            }
        }

        return issues;
    }

    /// <summary>
    /// The eligibility of valid typed details for a rental (<see cref="Validate"/> found nothing): evaluated on a transient
    /// person built from them, never stored.
    /// </summary>
    public static Result<DriverEligibilityReport> Evaluate(
        PersonDetails details,
        IReadOnlyList<IdentityDocumentDetails> documents,
        Period rental,
        EligibilityRules rules,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var person = Person.Register(details, now);
        if (person.IsFailure)
        {
            return person.Error;
        }

        foreach (var document in documents)
        {
            var added = person.Value.AddDocument(document, now);
            if (added.IsFailure)
            {
                return added.Error;
            }
        }

        return DriverEligibility.Evaluate(person.Value, rental, rules);
    }
}

/// <summary>A problem of a typed driver: the document concerned (<see langword="null"/> for the details) and the error.</summary>
public sealed record DriverSubmissionIssue(IdentityDocumentType? Document, Error Error);
