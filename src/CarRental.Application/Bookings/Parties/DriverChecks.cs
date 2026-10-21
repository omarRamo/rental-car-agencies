using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Parties;

/// <summary>
/// The checks of the drivers of a file, through the Customers contract: eligibility for the rental (age, licence
/// seniority, documents valid until the return) and the internal customer check (blacklist), whose outcome is never
/// shown to the applicant and only shown to agents as a generic "customer check".
/// </summary>
internal sealed class DriverChecks(ICustomersQueries customers)
{
    /// <summary>True when one of the drivers fails the internal customer check (blacklist).</summary>
    public async Task<bool> AnyFailsCustomerCheckAsync(IEnumerable<Guid> driverIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driverIds);
        foreach (var driverId in driverIds.Distinct())
        {
            if (await customers.IsBlacklistedAsync(driverId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The eligibility of each driver for the rental, with the settings of the agency.</summary>
    public async Task<Result<IReadOnlyList<DriverEligibilityInfo>>> EligibilityAsync(
        IEnumerable<Guid> driverIds,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid agencyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driverIds);
        var reports = new List<DriverEligibilityInfo>();
        foreach (var driverId in driverIds.Distinct())
        {
            var report = await customers.CheckDriverEligibilityAsync(driverId, pickupAt, returnAt, agencyId, cancellationToken);
            if (report.IsFailure)
            {
                return report.Error;
            }

            reports.Add(report.Value);
        }

        return reports;
    }
}
