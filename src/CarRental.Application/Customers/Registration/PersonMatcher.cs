using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Customers;

namespace CarRental.Application.Customers.Registration;

/// <summary>
/// Deduplication of persons (ADR-0027). A current document with the same type, issuing country and number is the same
/// person, whatever the name typed. A phone number or an e-mail only identifies a person together with the same
/// normalized name, because relatives share them; and never when the candidate holds another document of the type
/// given (father and son with the same name and phone). Wrongly merging two drivers is worse than a duplicate.
/// </summary>
internal static class PersonMatcher
{
    public static PersonMatch Match(Person probe, IdentityDocument? probeDocument, IReadOnlyList<Person> candidates)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(candidates);

        if (probeDocument is not null)
        {
            var byDocument = Oldest(candidates.Where(c => c.CurrentDocument(probeDocument.Type) is { } document
                                                          && string.Equals(document.NumberKey, probeDocument.NumberKey, StringComparison.Ordinal)
                                                          && string.Equals(document.IssuingCountry, probeDocument.IssuingCountry, StringComparison.Ordinal)));
            if (byDocument is not null)
            {
                var nameDiffers = !string.Equals(byDocument.SearchName, probe.SearchName, StringComparison.Ordinal);
                return Found(byDocument, PersonMatchKindInfo.Document, nameDiffers, candidates);
            }
        }

        bool SamePerson(Person candidate) =>
            string.Equals(candidate.SearchName, probe.SearchName, StringComparison.Ordinal)
            && (probeDocument is null || candidate.CurrentDocument(probeDocument.Type) is null);

        if (probe.Phone is { } phone
            && Oldest(candidates.Where(c => string.Equals(c.Phone, phone, StringComparison.Ordinal) && SamePerson(c))) is { } byPhone)
        {
            return Found(byPhone, PersonMatchKindInfo.Phone, nameDiffers: false, candidates);
        }

        if (probe.Email is { } email
            && Oldest(candidates.Where(c => string.Equals(c.Email, email, StringComparison.Ordinal) && SamePerson(c))) is { } byEmail)
        {
            return Found(byEmail, PersonMatchKindInfo.Email, nameDiffers: false, candidates);
        }

        return new PersonMatch(null, PersonMatchKindInfo.Created, NameDiffers: false, [.. candidates.Select(c => c.Id)]);
    }

    private static Person? Oldest(IEnumerable<Person> persons) =>
        persons.OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).FirstOrDefault();

    private static PersonMatch Found(Person person, PersonMatchKindInfo kind, bool nameDiffers, IReadOnlyList<Person> candidates) =>
        new(person, kind, nameDiffers, [.. candidates.Where(c => c.Id != person.Id).Select(c => c.Id)]);
}

/// <summary>The person found (or none), how, and the other candidates sharing a key.</summary>
internal sealed record PersonMatch(Person? Person, PersonMatchKindInfo Kind, bool NameDiffers, IReadOnlyList<Guid> SimilarPersonIds);
