using CarRental.Application.Accounting.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Accounting.Fakes;

/// <summary>
/// The monthly closing hook of the tests: every month is open until a test closes it (<see cref="Close"/>); each check is
/// recorded, so that a test sees which write path asked for which day.
/// </summary>
internal sealed class FakeAccountingPeriods : IAccountingPeriods
{
    private readonly HashSet<(Guid AgencyId, AccountingMonth Month)> _closed = [];

    public List<(Guid AgencyId, DateOnly Day)> Checks { get; } = [];

    public FakeAccountingPeriods Close(Guid agencyId, DateOnly anyDayOfTheMonth)
    {
        _closed.Add((agencyId, AccountingMonth.Of(anyDayOfTheMonth)));
        return this;
    }

    public Task<Result> EnsureOpenAsync(Guid agencyId, DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        Checks.Add((agencyId, businessDate));
        return Task.FromResult(_closed.Contains((agencyId, AccountingMonth.Of(businessDate))) ? Result.Failure(AccountingErrors.PeriodClosed) : Result.Success());
    }

    public Task<IReadOnlyList<DateOnly>> GetClosedMonthsAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DateOnly>>([.. _closed.Where(c => c.AgencyId == agencyId).Select(c => c.Month.FirstDay).Order()]);
}
