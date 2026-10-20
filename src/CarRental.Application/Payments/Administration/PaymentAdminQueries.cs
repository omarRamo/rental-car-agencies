using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments.Administration;

/// <summary>
/// Lists of the payments screen (<c>/admin/payments</c>, permission <c>payments.read</c>): payments and deposits filtered
/// by period (Tunis days of recording), means or mode, status and booking, newest first, with the totals of the whole
/// filtered list.
/// </summary>
public sealed class PaymentAdminQueries(IAppDbContext db, IUserContext userContext)
{
    public async Task<Result<PaymentList>> ListPaymentsAsync(PaymentListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.PaymentsRead))
        {
            return PaymentsErrors.Forbidden;
        }

        var query = db.Set<Payment>().AsNoTracking();
        if (filter.From is { } from)
        {
            var start = TunisTime.StartOfDay(from);
            query = query.Where(p => p.RecordedAtUtc >= start);
        }

        if (filter.To is { } to)
        {
            var end = TunisTime.StartOfDay(to.AddDays(1));
            query = query.Where(p => p.RecordedAtUtc < end);
        }

        if (filter.Method is { } method)
        {
            var wanted = method.ToDomain();
            query = query.Where(p => p.Method == wanted);
        }

        if (filter.Status is { } status)
        {
            var wanted = status.ToDomain();
            query = query.Where(p => p.Status == wanted);
        }

        if (filter.Direction is { } direction)
        {
            var wanted = direction.ToDomain();
            query = query.Where(p => p.Direction == wanted);
        }

        if (filter.BookingId is { } bookingId)
        {
            query = query.Where(p => p.BookingId == bookingId);
        }

        if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(p => p.AgencyId == agencyId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.RecordedAtUtc)
            .ThenByDescending(p => p.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var amounts = await query
            .Select(p => new PaymentAmounts(p.Direction, p.Status, p.Amount, p.CapturedAmount))
            .ToListAsync(cancellationToken);

        return new PaymentList(
            new PagedResult<PaymentInfo>([.. items.Select(p => p.ToInfo())], total, page.Page, page.PageSize),
            PaymentMapping.TotalsOf(amounts));
    }

    public async Task<Result<DepositList>> ListDepositsAsync(DepositListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!userContext.HasPermission(Permissions.PaymentsRead))
        {
            return PaymentsErrors.Forbidden;
        }

        var query = db.Set<DepositHold>().AsNoTracking();
        if (filter.From is { } from)
        {
            var start = TunisTime.StartOfDay(from);
            query = query.Where(d => d.TakenAtUtc >= start);
        }

        if (filter.To is { } to)
        {
            var end = TunisTime.StartOfDay(to.AddDays(1));
            query = query.Where(d => d.TakenAtUtc < end);
        }

        if (filter.Mode is { } mode)
        {
            var wanted = mode.ToDomain();
            query = query.Where(d => d.Mode == wanted);
        }

        if (filter.Status is { } status)
        {
            var wanted = status.ToDomain();
            query = query.Where(d => d.Status == wanted);
        }

        if (filter.BookingId is { } bookingId)
        {
            query = query.Where(d => d.BookingId == bookingId);
        }

        if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(d => d.AgencyId == agencyId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(d => d.TakenAtUtc)
            .ThenByDescending(d => d.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var amounts = await query
            .Select(d => new DepositAmounts(d.Status, d.Amount, d.RetainedAmount))
            .ToListAsync(cancellationToken);

        return new DepositList(
            new PagedResult<DepositInfo>([.. items.Select(d => d.ToInfo())], total, page.Page, page.PageSize),
            PaymentMapping.TotalsOf(amounts));
    }
}

/// <summary>Filter of the payment list: Tunis days of recording between <c>From</c> and <c>To</c> (included), means, status, direction, booking, agency.</summary>
public sealed record PaymentListFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    PaymentMethodInfo? Method = null,
    PaymentStatusInfo? Status = null,
    PaymentDirectionInfo? Direction = null,
    Guid? BookingId = null,
    Guid? AgencyId = null);

/// <summary>A page of payments and the totals of the whole filtered list.</summary>
public sealed record PaymentList(PagedResult<PaymentInfo> Page, PaymentTotals Totals);

/// <summary>Filter of the deposit list: Tunis days of the taking between <c>From</c> and <c>To</c> (included), mode, status, booking, agency.</summary>
public sealed record DepositListFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    DepositModeInfo? Mode = null,
    DepositStatusInfo? Status = null,
    Guid? BookingId = null,
    Guid? AgencyId = null);

/// <summary>A page of deposits and the totals of the whole filtered list.</summary>
public sealed record DepositList(PagedResult<DepositInfo> Page, DepositTotals Totals);
