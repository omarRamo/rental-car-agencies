using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Reporting.Snapshots;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Reporting;

/// <summary>The recomputation from the reports screen refuses what it must not compute (ADR-0045).</summary>
public sealed class SnapshotServiceTests
{
    private static readonly DateTimeOffset Now = TunisTime.FromLocal(new DateOnly(2026, 10, 21), new TimeOnly(9, 0));
    private static readonly AgencyInfo Agency = new(Guid.CreateVersion7(), "Agence de test", "Tunis", null, null, true);

    [Fact]
    public async Task Without_access_to_the_reports_nothing_is_computed()
    {
        var result = await Service(Roles.Agent).RecomputeAsync(Agency.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ReportingErrors.Forbidden);
    }

    [Theory]
    [InlineData("2026-10-21", "2026-10-21")]
    [InlineData("2026-10-15", "2026-10-25")]
    public async Task Today_and_the_days_to_come_are_not_computed(string from, string to)
    {
        var result = await Service(Roles.Accountant).RecomputeAsync(Agency.Id, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ReportingErrors.SnapshotFutureDay);
    }

    [Fact]
    public async Task A_period_is_valid_and_a_year_at_most()
    {
        var service = Service(Roles.Manager);
        var ct = TestContext.Current.CancellationToken;

        (await service.RecomputeAsync(Agency.Id, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 1), ct)).Error.ShouldBe(ReportingErrors.PeriodInvalid);
        (await service.RecomputeAsync(Agency.Id, new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 1), ct)).Error.ShouldBe(ReportingErrors.PeriodTooLong);
    }

    [Fact]
    public async Task An_unknown_agency_is_not_found()
    {
        var result = await Service(Roles.Manager).RecomputeAsync(Guid.CreateVersion7(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ReportingErrors.AgencyNotFound);
    }

    private static SnapshotService Service(string role)
    {
        var clock = new FakeTimeProvider(Now);
        var loader = new SnapshotInputsLoader(new FakeFleetReporting(), new FakeBookingsReporting(), [], new FakeCommissions());
        var writer = new SnapshotWriter(new CountingDbContext(), new InlineUnitOfWork(), loader, clock, NullLogger<SnapshotWriter>.Instance);
        return new SnapshotService(TestUsers.ContextFor(Guid.CreateVersion7(), "user@tests.local", role), new FakeAgencies(Agency), writer, new RecordingAuditTrail(), clock);
    }
}
