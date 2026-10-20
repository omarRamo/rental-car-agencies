using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Eligibility;

/// <summary>Loads the person and the eligibility settings, then applies the pure <see cref="DriverEligibility"/> rules.</summary>
internal sealed class DriverEligibilityChecker(IAppDbContext db, ISettingsReader settings)
{
    public async Task<Result<DriverEligibilityInfo>> CheckAsync(
        Guid personId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? agencyId,
        CancellationToken cancellationToken)
    {
        var rental = Period.Create(pickupAt, returnAt);
        if (rental.IsFailure)
        {
            return CustomersErrors.RentalPeriodInvalid;
        }

        var person = await db.Set<Person>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
        if (person is null)
        {
            return CustomersErrors.PersonNotFound;
        }

        if (person.IsAnonymized)
        {
            return CustomersErrors.PersonAnonymized;
        }

        var rules = new EligibilityRules(
            await settings.GetAsync(CustomerSettings.DriverMinimumAge, agencyId, cancellationToken),
            await settings.GetAsync(CustomerSettings.DriverMinimumLicenseYears, agencyId, cancellationToken));
        var report = DriverEligibility.Evaluate(person, rental.Value, rules);
        return new DriverEligibilityInfo(
            person.Id,
            report.IsEligible,
            [.. report.Issues.Select(issue => issue.ToInfo())],
            rules.MinimumAge,
            rules.MinimumLicenseYears);
    }
}
