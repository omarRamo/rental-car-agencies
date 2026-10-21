using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using CarRental.Web.Resources.Admin.Bookings;

namespace CarRental.Architecture.Tests.Bookings;

/// <summary>Every bookings error and every value shown by the bookings screens has a French and an English text.</summary>
public sealed class BookingsConventionTests
{
    private static readonly CultureInfo French = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static TheoryData<string> DisplayedKeys()
    {
        var keys = new TheoryData<string>();
        void Add<TEnum>(string prefix, params TEnum[] excluded)
            where TEnum : struct, Enum
        {
            foreach (var value in Enum.GetValues<TEnum>().Except(excluded))
            {
                keys.Add($"{prefix}.{value}");
            }
        }

        Add<BookingStatus>("Status");
        Add<SalesChannel>("Channel");
        Add<BookingEventType>("Event");
        Add<InformationRequestReason>("InformationReason");
        Add<InformationRequestReason>("InformationReason.Template");
        Add<QuoteOrigin>("Origin");
        Add<VehicleCandidateState>("Candidate");
        Add<BookingPartyRoleInfo>("Role");
        Add<DocumentsStateInfo>("Documents");
        Add<CustomerKindInfo>("Payer.Kind");
        Add<ValidationOutcome>("Validate");
        Add<ChecklistItemKind>("Check");
        Add<ChecklistState>("CheckState");
        Add<PromoCodeOutcome>("Promo", PromoCodeOutcome.None);
        Add<UnavailabilityReasonInfo>("Planning.Off");
        Add<VehicleStatusInfo>("VehicleStatus");
        Add<PersonMatchKindInfo>("Person.Match");
        foreach (var status in new[] { BookingStatus.PendingValidation, BookingStatus.InformationRequested, BookingStatus.OptionHeld, BookingStatus.AwaitingPayment })
        {
            keys.Add($"Deadline.{status}");
        }

        foreach (var legend in new[] { "pending", "option", "confirmed", "progress", "returned", "ended" })
        {
            keys.Add($"Legend.{legend}");
        }

        foreach (var reason in BookingReasons.All)
        {
            keys.Add($"Reason.{reason}");
        }

        foreach (var label in BookingQuoteLabels.All)
        {
            keys.Add(label);
        }

        foreach (var email in new[] { "Subject", "Greeting", "Body", "Line", "Action" })
        {
            keys.Add($"Email.Escalation.{email}");
        }

        return keys;
    }

    [Fact]
    public void Every_error_code_of_the_bookings_module_has_a_user_facing_text()
    {
        var manager = new ResourceManager("CarRental.Web.Resources.SharedResources", Assemblies.Web);
        var errors = Errors();

        errors.Count.ShouldBeGreaterThan(40);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("booking.");
            manager.GetString($"Error.{error.Code}", French).ShouldNotBeNullOrWhiteSpace(error.Code);
            manager.GetString($"Error.{error.Code}", English).ShouldNotBeNullOrWhiteSpace(error.Code);
        }
    }

    [Fact]
    public void Error_codes_of_the_bookings_module_are_unique()
    {
        var codes = Errors().Select(e => e.Code).ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }

    [Theory]
    [MemberData(nameof(DisplayedKeys))]
    public void Every_value_shown_by_the_bookings_screens_is_translated(string key)
    {
        var manager = new ResourceManager(typeof(BookingsResources).FullName!, Assemblies.Web);

        manager.GetString(key, French).ShouldNotBeNullOrWhiteSpace(key);
        manager.GetString(key, English).ShouldNotBeNullOrWhiteSpace(key);
    }

    private static List<Error> Errors() =>
        [.. typeof(BookingErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];
}
