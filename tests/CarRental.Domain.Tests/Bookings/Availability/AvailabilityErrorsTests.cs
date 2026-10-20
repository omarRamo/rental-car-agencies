using System.Reflection;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Bookings.Availability;

public sealed class AvailabilityErrorsTests
{
    [Fact]
    public void Error_codes_are_stable()
    {
        // Codes are a public contract (logs, API responses, resource keys Error.<code>): never rename one.
        var codes = typeof(AvailabilityErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null))
            .OfType<Error>()
            .ToDictionary(error => error.Code, error => error.Kind);

        codes.ShouldBe(
            new Dictionary<string, ErrorKind>
            {
                ["availability.not_available"] = ErrorKind.Conflict,
                ["availability.vehicle_overlap"] = ErrorKind.Conflict,
                ["availability.vehicle_not_compliant"] = ErrorKind.Conflict,
                ["availability.vehicle_unavailable"] = ErrorKind.Conflict,
                ["availability.vehicle_not_in_category"] = ErrorKind.Validation,
                ["availability.no_vehicle_in_service"] = ErrorKind.Conflict,
                ["availability.all_vehicles_occupied"] = ErrorKind.Conflict,
                ["availability.category_not_found"] = ErrorKind.NotFound,
            },
            ignoreOrder: true);
    }
}
