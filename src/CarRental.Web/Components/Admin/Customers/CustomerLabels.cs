using System.Collections.Concurrent;
using System.Globalization;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using CarRental.Web.Resources.Admin.Customers;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Customers;

/// <summary>Localized labels and display formats of the customers screens.</summary>
public static class CustomerLabels
{
    /// <summary>Countries proposed first in the pickers: Tunisia, then the usual origins of the customers.</summary>
    public static IReadOnlyList<string> FrequentCountries { get; } = ["TN", "FR", "DE", "IT", "DZ", "LY", "BE", "CH", "GB", "CA", "US", "MA"];

    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> CountriesByCulture = new(StringComparer.Ordinal);

    public static string DocumentType(IStringLocalizer<CustomersAdminResources> localizer, IdentityDocumentTypeInfo type)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[$"DocumentType.{type}"];
    }

    public static string Purpose(IStringLocalizer<CustomersAdminResources> localizer, ConsentPurposeInfo purpose)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[$"Consent.Purpose.{purpose}"];
    }

    public static string Channel(IStringLocalizer<CustomersAdminResources> localizer, ConsentChannelInfo channel)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer[$"Consent.Channel.{channel}"];
    }

    /// <summary>The sentence explaining an eligibility issue.</summary>
    public static string Issue(IStringLocalizer<CustomersAdminResources> localizer, EligibilityIssueInfo issue)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(issue);
        var document = issue.DocumentType is { } type ? DocumentType(localizer, type) : string.Empty;
        var date = issue.Date is { } value ? Date(value) : string.Empty;
        return localizer[$"Eligibility.Issue.{issue.Kind}", document, date, issue.Required ?? 0, issue.Actual ?? 0];
    }

    /// <summary>The name of a country in the current UI language, with its code, e.g. "Tunisie (TN)".</summary>
    public static string Country(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "—";
        }

        return $"{CountryName(code)} ({code})";
    }

    public static string CountryName(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    /// <summary>Every accepted country code, frequent ones first, then by name in the current UI language (cached per language).</summary>
    public static IReadOnlyList<string> Countries() =>
        CountriesByCulture.GetOrAdd(
            CultureInfo.CurrentUICulture.Name,
            static _ => [.. FrequentCountries, .. CountryCodes.All
                .Except(FrequentCountries, StringComparer.Ordinal)
                .OrderBy(CountryName, StringComparer.CurrentCultureIgnoreCase)]);

    /// <summary>A Tunisian number as "+216 98 123 456"; other numbers as stored (E.164).</summary>
    public static string Phone(string? e164)
    {
        if (string.IsNullOrEmpty(e164))
        {
            return "—";
        }

        return e164.StartsWith("+216", StringComparison.Ordinal) && e164.Length == 12
            ? $"+216 {e164[4..6]} {e164[6..9]} {e164[9..]}"
            : e164;
    }

    public static string Date(DateOnly? date) => date is { } value ? value.ToString("d", CultureInfo.CurrentCulture) : "—";

    /// <summary>An instant shown in Tunis time.</summary>
    public static string Instant(DateTimeOffset? instant) =>
        instant is { } value ? TunisTime.ToTunis(value).ToString("g", CultureInfo.CurrentCulture) : "—";

    /// <summary>Age in completed years on <paramref name="today"/>.</summary>
    public static int Age(DateOnly birthDate, DateOnly today) => DriverEligibility.CompletedYears(birthDate, today);

    /// <summary>A file size in kilobytes or megabytes, e.g. "245 Ko".</summary>
    public static string FileSize(IStringLocalizer<CustomersAdminResources> localizer, long bytes)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return bytes < 1024 * 1024
            ? localizer["FileSize.Kilobytes", Math.Max(1, (int)Math.Round(bytes / 1024d))]
            : localizer["FileSize.Megabytes", Math.Round(bytes / (1024d * 1024d), 1)];
    }
}
