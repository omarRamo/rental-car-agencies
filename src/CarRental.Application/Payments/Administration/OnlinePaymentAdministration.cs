using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Payments.Administration;

/// <summary>
/// Online payments in the back office (<c>/admin/payments</c>): lists with the gateway status and the flags, the detail
/// of a payment with its call history, and the actions — re-check the status, capture, reverse, refund with a reason,
/// approve or reject a refund above the threshold, create a payment link, mark a flagged payment as checked. Reads need
/// <c>payments.read</c>, actions <c>payments.manage</c>, approvals <c>payments.refunds.approve</c>.
/// </summary>
public sealed class OnlinePaymentAdministration(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    IOnlinePayments online,
    ISettingsReader settings)
{
    /// <summary>Calls shown in the history of a payment (most recent first).</summary>
    public const int CallHistoryLength = 200;

    private bool CanRead => userContext.HasPermission(Permissions.PaymentsRead);

    private bool CanManage => userContext.HasPermission(Permissions.PaymentsManage);

    private bool CanApprove => userContext.HasPermission(Permissions.PaymentsRefundsApprove);

    public async Task<Result<OnlinePaymentList>> ListAsync(OnlinePaymentListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!CanRead)
        {
            return PaymentsErrors.Forbidden;
        }

        var awaitingApproval = AwaitingApproval();
        var query = Online();
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

        if (filter.Status is { } status)
        {
            var wanted = status.ToDomain();
            query = query.Where(p => p.Status == wanted);
        }

        if (filter.BookingId is { } bookingId)
        {
            query = query.Where(p => p.BookingId == bookingId);
        }

        if (filter.NeedsAttention)
        {
            query = query.Where(p => p.GatewayAttention != null);
        }

        if (filter.AwaitingApproval)
        {
            query = query.Where(p => awaitingApproval.Any(r => r.OriginalPaymentId == p.Id));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.RecordedAtUtc)
            .ThenByDescending(p => p.Id)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var ids = items.Select(p => p.Id).ToList();
        var refunds = await OnlineRefunds().Where(r => r.OriginalPaymentId != null && ids.Contains(r.OriginalPaymentId.Value)).ToListAsync(cancellationToken);
        var attention = await Online().CountAsync(p => p.GatewayAttention != null, cancellationToken);
        var approvals = await awaitingApproval.CountAsync(cancellationToken);
        return new OnlinePaymentList(
            new PagedResult<OnlinePaymentInfo>([.. items.Select(p => p.ToOnlineInfo(refunds))], total, page.Page, page.PageSize),
            attention,
            approvals);
    }

    /// <summary>A payment with its online refunds and the calls made to the gateway for its order.</summary>
    public async Task<Result<OnlinePaymentView>> GetAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return PaymentsErrors.Forbidden;
        }

        var payment = await online.FindAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return PaymentsErrors.OnlinePaymentNotFound;
        }

        var calls = await db.Set<GatewayCallLog>()
            .AsNoTracking()
            .Where(c => c.PaymentId == paymentId)
            .OrderByDescending(c => c.StartedAtUtc)
            .ThenByDescending(c => c.Id)
            .Take(CallHistoryLength)
            .ToListAsync(cancellationToken);
        return new OnlinePaymentView(payment, [.. calls.Select(c => c.ToInfo())]);
    }

    /// <summary>The online payments of a booking (payments panel of the booking).</summary>
    public async Task<Result<IReadOnlyList<OnlinePaymentInfo>>> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        CanRead ? Result.Success(await online.GetBookingOnlinePaymentsAsync(bookingId, cancellationToken)) : PaymentsErrors.Forbidden;

    public Task<Result<OnlinePaymentInfo>> RefreshAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        CanManage ? online.RefreshStatusAsync(paymentId, cancellationToken) : Task.FromResult<Result<OnlinePaymentInfo>>(PaymentsErrors.Forbidden);

    public Task<Result<OnlinePaymentInfo>> CaptureAsync(Guid paymentId, decimal? amount, CancellationToken cancellationToken = default) =>
        CanManage ? online.CaptureAsync(paymentId, amount, cancellationToken) : Task.FromResult<Result<OnlinePaymentInfo>>(PaymentsErrors.Forbidden);

    public Task<Result<OnlinePaymentInfo>> ReverseAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        CanManage ? online.ReverseAsync(paymentId, cancellationToken) : Task.FromResult<Result<OnlinePaymentInfo>>(PaymentsErrors.Forbidden);

    public Task<Result<OnlineRefundOutcome>> RefundAsync(OnlineRefundRequest request, CancellationToken cancellationToken = default) =>
        CanManage ? online.RefundAsync(request, cancellationToken) : Task.FromResult<Result<OnlineRefundOutcome>>(PaymentsErrors.Forbidden);

    public Task<Result<OnlineRefundOutcome>> ApproveRefundAsync(Guid refundId, CancellationToken cancellationToken = default) =>
        CanApprove ? online.ApproveRefundAsync(refundId, cancellationToken) : Task.FromResult<Result<OnlineRefundOutcome>>(PaymentsErrors.RefundApprovalForbidden);

    public Task<Result> RejectRefundAsync(Guid refundId, string reason, CancellationToken cancellationToken = default) =>
        CanApprove ? online.RejectRefundAsync(refundId, reason, cancellationToken) : Task.FromResult<Result>(PaymentsErrors.RefundApprovalForbidden);

    /// <summary>The validity a new payment link of the agency gets by default (<c>payments.payment_link_validity</c>).</summary>
    public async Task<Result<TimeSpan>> GetPaymentLinkValidityAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        CanManage ? await settings.GetAsync(PaymentSettings.PaymentLinkValidity, agencyId, cancellationToken) : PaymentsErrors.Forbidden;

    /// <summary>Creates a one-step payment link for a booking (to copy; e-mail and WhatsApp sending come later).</summary>
    public Task<Result<OnlinePaymentRedirect>> CreatePaymentLinkAsync(OnlinePaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CanManage
            ? online.StartPaymentAsync(request with { IsLink = true }, cancellationToken)
            : Task.FromResult<Result<OnlinePaymentRedirect>>(PaymentsErrors.Forbidden);
    }

    /// <summary>Someone checked a flagged payment (in the merchant portal of Clic to Pay): the flag is cleared.</summary>
    public async Task<Result> MarkReviewedAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return PaymentsErrors.Forbidden;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var payment = await db.FindPaymentAsync(paymentId, ct);
                return payment is null ? PaymentsErrors.OnlinePaymentNotFound : payment.ClearGatewayAttention();
            },
            cancellationToken);
    }

    private IQueryable<Payment> Online() =>
        db.Set<Payment>().AsNoTracking().Where(p => p.Method == PaymentMethod.ClicToPay && p.Direction == PaymentDirection.Incoming);

    private IQueryable<Payment> OnlineRefunds() =>
        db.Set<Payment>().AsNoTracking().Where(r => r.Method == PaymentMethod.ClicToPay && r.Direction == PaymentDirection.Refund);

    private IQueryable<Payment> AwaitingApproval() =>
        OnlineRefunds().Where(r => r.Status == PaymentStatus.Pending && r.RequiresApproval && r.ApprovedAtUtc == null);
}

/// <summary>Filter of the online payments list: Tunis days of recording, status, booking, flagged only, with a refund awaiting approval only.</summary>
public sealed record OnlinePaymentListFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    PaymentStatusInfo? Status = null,
    Guid? BookingId = null,
    bool NeedsAttention = false,
    bool AwaitingApproval = false);

/// <summary>A page of online payments, with the number of flagged payments and of refunds awaiting approval (whole platform).</summary>
public sealed record OnlinePaymentList(PagedResult<OnlinePaymentInfo> Page, int AttentionCount, int AwaitingApprovalCount);

/// <summary>An online payment and the calls made to the gateway for its order, most recent first.</summary>
public sealed record OnlinePaymentView(OnlinePaymentInfo Payment, IReadOnlyList<GatewayCallInfo> Calls);
