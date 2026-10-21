using System.Globalization;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Customers.Contracts;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Resources;
using CarRental.Web.Resources.Public;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Public.Booking;

/// <summary>
/// Texts of the field errors of step 2 and of "Ma réservation": the funnel's own codes (<c>funnel.required</c>,
/// <c>funnel.date_invalid</c>, <c>funnel.eligibility.{kind}</c> with invariant arguments) and the business error codes
/// of the Customers and Bookings modules (<c>Error.{code}</c> of the shared texts). An unknown code gets a generic text:
/// a page never shows a code nor anything about a person on file.
/// </summary>
public static class FunnelTexts
{
    private const string EligibilityPrefix = "funnel.eligibility.";

    public static string Message(IStringLocalizer<BookingResources> texts, IStringLocalizer<SharedResources> shared, FunnelFieldError error)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(error);
        switch (error.Code)
        {
            case "funnel.required":
                return texts["Field.Error.Required"];
            case "funnel.date_invalid":
                return texts["Field.Error.Date"];
        }

        if (error.Code.StartsWith(EligibilityPrefix, StringComparison.Ordinal))
        {
            object[] arguments = [.. (error.Arguments ?? []).Select(argument => Argument(texts, argument))];
            var text = texts[$"Eligibility.{error.Code[EligibilityPrefix.Length..]}", arguments];
            return text.ResourceNotFound ? texts["Eligibility.Generic"] : text.Value;
        }

        var business = shared[$"Error.{error.Code}"];
        return business.ResourceNotFound ? texts["Error.public.unexpected"] : business.Value;
    }

    /// <summary>The label of the field of an error key (<c>main.birth_date</c> → "Date de naissance").</summary>
    public static string FieldLabel(IStringLocalizer<BookingResources> texts, string field)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(field);
        var name = field.Contains('.', StringComparison.Ordinal) ? field[(field.IndexOf('.', StringComparison.Ordinal) + 1)..] : field;
        var label = texts[$"Field.{name.Replace('.', '_')}"];
        return label.ResourceNotFound ? string.Empty : label.Value;
    }

    /// <summary>The element id of a field key: <c>main.birth_date</c> → <c>main-birth-date</c>.</summary>
    public static string FieldId(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Replace('.', '-').Replace('_', '-');
    }

    /// <summary>A document type name, a date (<c>yyyy-MM-dd</c>) or a number, written for the page.</summary>
    private static string Argument(IStringLocalizer<BookingResources> texts, string argument)
    {
        if (!argument.All(char.IsAsciiDigit) && Enum.TryParse<IdentityDocumentTypeInfo>(argument, ignoreCase: false, out var type))
        {
            return texts[$"DocumentType.{type}"].Value;
        }

        if (DateOnly.TryParseExact(argument, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        return argument;
    }

    /// <summary>The Tunis date and time of an instant, written for the page (<c>24 octobre 2026 à 10:00</c>).</summary>
    public static string Instant(IStringLocalizer<BookingResources> texts, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts["Summary.DateTime", PublicFormat.TunisLocal(instant)];
    }
}
