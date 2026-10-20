using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// A payer: either an individual, linked to a <see cref="Person"/> (name and address come from the person), or a company
/// with its legal name, tax identifier (matricule fiscal) and billing address. Bookings and invoices reference it by id.
/// </summary>
public sealed class Customer : AggregateRoot
{
    public const int LegalNameMaxLength = 200;
    public const int ContactNameMaxLength = 200;

    private Customer()
    {
    }

    private Customer(Guid id, CustomerKind kind, DateTimeOffset now)
        : base(id)
    {
        Kind = kind;
        CreatedAtUtc = now;
        LastModifiedAtUtc = now;
    }

    public CustomerKind Kind { get; private set; }

    /// <summary>The person of an individual customer (one individual customer per person).</summary>
    public Guid? PersonId { get; private set; }

    public string? LegalName { get; private set; }

    /// <summary>As displayed, e.g. <c>1234567/A/M/000</c>.</summary>
    public string? TaxIdentifier { get; private set; }

    /// <summary>Deduplication key of <see cref="TaxIdentifier"/> (letters and digits only).</summary>
    public string? TaxIdentifierKey { get; private set; }

    public PostalAddress? BillingAddress { get; private set; }

    public string? ContactName { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset LastModifiedAtUtc { get; private set; }

    public static Result<Customer> ForPerson(Guid personId, DateTimeOffset now)
    {
        if (personId == Guid.Empty)
        {
            return CustomersErrors.PersonRequired;
        }

        var customer = new Customer(NewId(), CustomerKind.Individual, now) { PersonId = personId };
        customer.Raise(new CustomerRegistered(customer.Id, CustomerKind.Individual));
        return customer;
    }

    public static Result<Customer> ForCompany(CompanyDetails details, DateTimeOffset now)
    {
        var facts = CompanyFacts.From(details);
        if (facts.IsFailure)
        {
            return facts.Error;
        }

        var customer = new Customer(NewId(), CustomerKind.Company, now);
        customer.Apply(facts.Value);
        customer.Raise(new CustomerRegistered(customer.Id, CustomerKind.Company));
        return customer;
    }

    public Result UpdateCompany(CompanyDetails details, DateTimeOffset now)
    {
        if (Kind != CustomerKind.Company)
        {
            return CustomersErrors.NotACompany;
        }

        var facts = CompanyFacts.From(details);
        if (facts.IsFailure)
        {
            return facts.Error;
        }

        Apply(facts.Value);
        LastModifiedAtUtc = now;
        Raise(new CompanyCustomerUpdated(Id));
        return Result.Success();
    }

    private void Apply(CompanyFacts facts)
    {
        LegalName = facts.LegalName;
        TaxIdentifier = facts.TaxIdentifier;
        TaxIdentifierKey = TaxIdentifiers.KeyOf(facts.TaxIdentifier);
        BillingAddress = facts.BillingAddress;
        ContactName = facts.ContactName;
        Email = facts.Email;
        Phone = facts.Phone;
    }

    private sealed record CompanyFacts(
        string LegalName,
        string TaxIdentifier,
        PostalAddress BillingAddress,
        string? ContactName,
        string? Email,
        string? Phone)
    {
        public static Result<CompanyFacts> From(CompanyDetails details)
        {
            ArgumentNullException.ThrowIfNull(details);
            var legalName = TextRules.Clean(details.LegalName);
            if (legalName is null || legalName.Length > LegalNameMaxLength)
            {
                return CustomersErrors.LegalNameInvalid;
            }

            var taxIdentifier = TaxIdentifiers.Clean(details.TaxIdentifier);
            if (taxIdentifier.IsFailure)
            {
                return taxIdentifier.Error;
            }

            if (details.BillingAddress is null)
            {
                return CustomersErrors.BillingAddressRequired;
            }

            var contactName = TextRules.Clean(details.ContactName);
            if (contactName?.Length > ContactNameMaxLength)
            {
                return CustomersErrors.ContactNameInvalid;
            }

            string? email = null;
            if (!string.IsNullOrWhiteSpace(details.Email))
            {
                var normalized = EmailAddresses.Normalize(details.Email);
                if (normalized.IsFailure)
                {
                    return normalized.Error;
                }

                email = normalized.Value;
            }

            string? phone = null;
            if (!string.IsNullOrWhiteSpace(details.Phone))
            {
                var normalized = PhoneNumbers.Normalize(details.Phone);
                if (normalized.IsFailure)
                {
                    return normalized.Error;
                }

                phone = normalized.Value;
            }

            return new CompanyFacts(legalName, taxIdentifier.Value, details.BillingAddress, contactName, email, phone);
        }
    }
}

/// <summary>What the agent enters about a company payer.</summary>
public sealed record CompanyDetails(
    string LegalName,
    string TaxIdentifier,
    PostalAddress BillingAddress,
    string? ContactName = null,
    string? Email = null,
    string? Phone = null);
