using System.ComponentModel.DataAnnotations.Schema;
using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>A postal address (person's address, billing address of a company): street line, city and country required.</summary>
// [ComplexType] (BCL annotation): a value stored in the columns of its owner, never a table of its own.
[ComplexType]
public sealed record PostalAddress
{
    public const int LineMaxLength = 200;
    public const int PostalCodeMaxLength = 16;
    public const int CityMaxLength = 100;

    private PostalAddress(string line1, string? line2, string? postalCode, string city, string country)
    {
        Line1 = line1;
        Line2 = line2;
        PostalCode = postalCode;
        City = city;
        Country = country;
    }

    public string Line1 { get; private init; }

    public string? Line2 { get; private init; }

    public string? PostalCode { get; private init; }

    public string City { get; private init; }

    /// <summary>ISO 3166-1 alpha-2 code.</summary>
    public string Country { get; private init; }

    public static Result<PostalAddress> Create(string? line1, string? line2, string? postalCode, string? city, string? country)
    {
        var cleanLine1 = TextRules.Clean(line1);
        var cleanLine2 = TextRules.Clean(line2);
        var cleanPostalCode = TextRules.Clean(postalCode);
        var cleanCity = TextRules.Clean(city);
        if (cleanLine1 is null || cleanLine1.Length > LineMaxLength
            || cleanLine2?.Length > LineMaxLength
            || cleanPostalCode?.Length > PostalCodeMaxLength
            || cleanCity is null || cleanCity.Length > CityMaxLength)
        {
            return CustomersErrors.AddressInvalid;
        }

        var countryCode = CountryCodes.Normalize(country);
        if (countryCode.IsFailure)
        {
            return countryCode.Error;
        }

        return new PostalAddress(cleanLine1, cleanLine2, cleanPostalCode, cleanCity, countryCode.Value);
    }

    public override string ToString() =>
        string.Join(", ", new[] { Line1, Line2, string.Join(' ', new[] { PostalCode, City }.Where(p => p is not null)), Country }.Where(p => !string.IsNullOrEmpty(p)));
}
