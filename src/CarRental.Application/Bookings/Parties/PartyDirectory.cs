using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Bookings;

namespace CarRental.Application.Bookings.Parties;

/// <summary>
/// The persons and payers of rental files, read through the Customers contract and cached for the operation (a DI
/// scope): lists and plannings show many files of the same customers.
/// </summary>
internal sealed class PartyDirectory(ICustomersQueries customers)
{
    private readonly Dictionary<Guid, PersonInfo?> _persons = [];
    private readonly Dictionary<Guid, CustomerInfo?> _customers = [];

    public async Task<PersonInfo?> PersonAsync(Guid? personId, CancellationToken cancellationToken)
    {
        if (personId is not { } id)
        {
            return null;
        }

        if (!_persons.TryGetValue(id, out var person))
        {
            person = await customers.GetPersonAsync(id, cancellationToken);
            _persons[id] = person;
        }

        return person;
    }

    public async Task<CustomerInfo?> CustomerAsync(Guid? customerId, CancellationToken cancellationToken)
    {
        if (customerId is not { } id)
        {
            return null;
        }

        if (!_customers.TryGetValue(id, out var customer))
        {
            customer = await customers.GetCustomerAsync(id, cancellationToken);
            _customers[id] = customer;
        }

        return customer;
    }

    /// <summary>The display name of a person (empty when unknown or anonymized).</summary>
    public async Task<string> NameAsync(Guid? personId, CancellationToken cancellationToken) =>
        (await PersonAsync(personId, cancellationToken))?.FullName ?? string.Empty;

    /// <summary>The known birth dates of the drivers (young driver extra of the pricing engine).</summary>
    public async Task<IReadOnlyList<DateOnly>> BirthDatesAsync(BookingParties parties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parties);
        var dates = new List<DateOnly>();
        foreach (var driverId in parties.Drivers)
        {
            if ((await PersonAsync(driverId, cancellationToken))?.BirthDate is { } birthDate)
            {
                dates.Add(birthDate);
            }
        }

        return dates;
    }

    /// <summary>The state of the documents of the drivers until <paramref name="returnDate"/> (agenda, checklist).</summary>
    public async Task<DocumentsStateInfo> DocumentsStateAsync(BookingParties parties, DateOnly returnDate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parties);
        if (parties.Drivers.Count == 0)
        {
            return DocumentsStateInfo.Unknown;
        }

        var allVerified = true;
        foreach (var driverId in parties.Drivers)
        {
            var person = await PersonAsync(driverId, cancellationToken);
            if (person is null || person.IsAnonymized)
            {
                return DocumentsStateInfo.Missing;
            }

            var identity = person.Documents.FirstOrDefault(d => d.Type is IdentityDocumentTypeInfo.NationalId or IdentityDocumentTypeInfo.Passport);
            var license = person.Documents.FirstOrDefault(d => d.Type == IdentityDocumentTypeInfo.DrivingLicense);
            if (identity is null || license is null || !identity.IsValidUntil(returnDate) || !license.IsValidUntil(returnDate))
            {
                return DocumentsStateInfo.Missing;
            }

            allVerified &= identity.IsVerified && license.IsVerified;
        }

        return allVerified ? DocumentsStateInfo.Complete : DocumentsStateInfo.ToVerify;
    }
}
