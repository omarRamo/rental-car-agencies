using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Eligibility;

/// <summary>Loads the person and the eligibility settings, then applies the pure <see cref="DriverEligibility"/> rules.</summary>
internal sealed class DriverEligibilityChecker(IAppDbContext db, ISettingsReader settings, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

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

        var rules = await RulesAsync(agencyId, cancellationToken);
        var report = DriverEligibility.Evaluate(person, rental.Value, rules);
        return ToInfo(person.Id, report, rules);
    }

    /// <summary>
    /// A driver typed by a visitor (ADR-0050): every error of the details and documents, then the eligibility of the typed
    /// values for the rental — nothing stored is read.
    /// </summary>
    public async Task<SubmittedDriverCheck> CheckSubmittedAsync(
        SubmittedDriverDetails driver,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? agencyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driver);
        var now = _clock.GetUtcNow();
        var details = new PersonDetails(driver.FirstName, driver.LastName, driver.Nationality, driver.BirthDate, driver.Phone, driver.Email);
        var documents = (driver.Documents ?? []).Select(d => d.ToDomain()).ToList();
        var issues = DriverSubmissionRules.Validate(details, documents, TunisTime.DateOf(now));
        if (issues.Count > 0)
        {
            return new SubmittedDriverCheck([.. issues.Select(i => new SubmittedDriverError(i.Document?.ToInfo(), i.Error.Code))], null);
        }

        var rental = Period.Create(pickupAt, returnAt);
        if (rental.IsFailure)
        {
            return new SubmittedDriverCheck([new SubmittedDriverError(null, CustomersErrors.RentalPeriodInvalid.Code)], null);
        }

        var rules = await RulesAsync(agencyId, cancellationToken);
        var report = DriverSubmissionRules.Evaluate(details, documents, rental.Value, rules, now);
        return report.IsFailure
            ? new SubmittedDriverCheck([new SubmittedDriverError(null, report.Error.Code)], null)
            : new SubmittedDriverCheck([], ToInfo(Guid.Empty, report.Value, rules));
    }

    private static DriverEligibilityInfo ToInfo(Guid personId, DriverEligibilityReport report, EligibilityRules rules) => new(
        personId,
        report.IsEligible,
        [.. report.Issues.Select(issue => issue.ToInfo())],
        rules.MinimumAge,
        rules.MinimumLicenseYears);

    private async Task<EligibilityRules> RulesAsync(Guid? agencyId, CancellationToken cancellationToken) => new(
        await settings.GetAsync(CustomerSettings.DriverMinimumAge, agencyId, cancellationToken),
        await settings.GetAsync(CustomerSettings.DriverMinimumLicenseYears, agencyId, cancellationToken));
}
