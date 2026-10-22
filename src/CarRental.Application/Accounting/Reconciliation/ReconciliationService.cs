using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Accounting.Reconciliation;

/// <summary>
/// The reconciliations of a month (<c>/admin/reconciliation</c>, permission <c>reconciliation.manage</c>, ADR-0064):
/// <list type="bullet">
/// <item>cash: the closed cash sessions against the cash payments recorded in them, and their counted differences;</item>
/// <item>Clic to Pay: the online payments against the statuses the gateway reported (flags of the catch-up and of the daily
/// reconciliation, package 2B) and against the merchant statement — imported (generic CSV, TODO(spec): the format of SMT)
/// or typed by hand —: matched, missing, extra lines;</item>
/// <item>Booking.com: the settlements of the channels computed by their reconciliation screen (package 5A, not duplicated):
/// payout against what the channel should pay.</item>
/// </list>
/// Each difference can be justified (reason, user, instant); justifications are only added and stay visible. Unjustified
/// differences block the monthly closing.
/// </summary>
public sealed class ReconciliationService
{
    private const long MaxStatementBytes = 2 * 1024 * 1024;

    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _user;
    private readonly ICashQueries _cash;
    private readonly IPaymentsAccountingQueries _payments;
    private readonly IChannelPayouts _channels;
    private readonly IBookingNumbers _bookings;
    private readonly IAccountingPeriods _periods;
    private readonly IEnumerable<IMerchantStatementImporter> _importers;
    private readonly TimeProvider _clock;

    internal ReconciliationService(
        IAppDbContext db,
        IUnitOfWork unitOfWork,
        IUserContext user,
        ICashQueries cash,
        IPaymentsAccountingQueries payments,
        IChannelPayouts channels,
        IBookingNumbers bookings,
        IAccountingPeriods periods,
        IEnumerable<IMerchantStatementImporter> importers,
        TimeProvider clock)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _user = user;
        _cash = cash;
        _payments = payments;
        _channels = channels;
        _bookings = bookings;
        _periods = periods;
        _importers = importers;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.ReconciliationManage);

    /// <summary>The formats of the statement importers.</summary>
    public IReadOnlyList<string> ImportFormats => [.. _importers.Select(i => i.Format)];

    public async Task<Result<CashReconciliationView>> GetCashAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var justifications = await JustificationsAsync(agencyId, month, cancellationToken);
        return await CashAsync(agencyId, month, justifications, cancellationToken);
    }

    public async Task<Result<ClicToPayReconciliationView>> GetClicToPayAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var justifications = await JustificationsAsync(agencyId, month, cancellationToken);
        return await ClicToPayAsync(agencyId, month, justifications, cancellationToken);
    }

    public async Task<Result<ChannelsReconciliationView>> GetChannelsAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var justifications = await JustificationsAsync(agencyId, month, cancellationToken);
        return await ChannelsAsync(agencyId, month, justifications, cancellationToken);
    }

    /// <summary>Justifies a difference of the month as it is now (its key and current amount), with a reason.</summary>
    public async Task<Result<Guid>> JustifyAsync(Guid agencyId, AccountingMonth month, string differenceKey, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(differenceKey);
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        // The differences of the agency the operation works for only (ADR-0067).
        var difference = !_user.IsAcrossAgencies && _user.AgencyId != agencyId
            ? null
            : (await DifferencesAsync(agencyId, month, cancellationToken)).FirstOrDefault(d => d.Key == differenceKey);
        if (difference is null)
        {
            return AccountingErrors.DifferenceNotFound;
        }

        var justified = ReconciliationJustification.Justify(agencyId, month, difference.Area, difference.Key, difference.Amount, reason, _user.ToAuditActor(), _clock.GetUtcNow());
        if (justified.IsFailure)
        {
            return justified.Error;
        }

        _db.Set<ReconciliationJustification>().Add(justified.Value);
        await _db.SaveChangesAsync(cancellationToken);
        return justified.Value.Id;
    }

    /// <summary>The statements brought in for the agency, the latest first.</summary>
    public async Task<Result<IReadOnlyList<StatementView>>> ListStatementsAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var statements = await _db.Set<MerchantStatement>().AsNoTracking().Where(s => s.AgencyId == agencyId).ToListAsync(cancellationToken);
        var closed = (await _periods.GetClosedMonthsAsync(agencyId, cancellationToken)).ToHashSet();
        return Result.Success<IReadOnlyList<StatementView>>(
        [
            .. statements
                .OrderByDescending(s => s.ImportedAtUtc)
                .Select(s => new StatementView(
                    s.Id,
                    s.Origin,
                    s.FileName,
                    s.Format,
                    s.ImportedAtUtc,
                    s.ImportedByName,
                    s.Lines.Count,
                    s.Lines.Min(l => l.TransactionDate),
                    s.Lines.Max(l => l.TransactionDate),
                    s.Days().Any(d => closed.Contains(new DateOnly(d.Year, d.Month, 1))))),
        ]);
    }

    /// <summary>
    /// Imports a merchant statement file with the importer of <paramref name="format"/>: every line is read first (nothing is
    /// recorded when one is invalid: the outcome lists them); refused when a day of it is in a closed month.
    /// </summary>
    public async Task<Result<StatementImportOutcome>> ImportStatementAsync(Guid agencyId, string format, string? fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var importer = _importers.FirstOrDefault(i => string.Equals(i.Format, format, StringComparison.OrdinalIgnoreCase));
        if (importer is null)
        {
            return AccountingErrors.StatementInvalid;
        }

        var bytes = await ReadBoundedAsync(content, cancellationToken);
        if (bytes is null)
        {
            return AccountingErrors.StatementTooLarge;
        }

        var read = importer.Read(bytes);
        if (!read.IsValid)
        {
            return read.Lines.Count == 0 && read.InvalidLines.Count == 0 && !read.HeaderInvalid
                ? AccountingErrors.StatementEmpty
                : new StatementImportOutcome(null, read.Lines.Count, read.InvalidLines, read.HeaderInvalid);
        }

        var recorded = await RecordStatementAsync(agencyId, StatementOrigin.Import, fileName, importer.Format, read.Lines, cancellationToken);
        return recorded.IsSuccess ? new StatementImportOutcome(recorded.Value, read.Lines.Count, [], false) : recorded.Error;
    }

    /// <summary>Records a line typed by hand from the merchant portal (a statement of one line).</summary>
    public async Task<Result<Guid>> AddManualLineAsync(Guid agencyId, StatementLineForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var amount = Money.FromExact(form.Amount, Currency.Tnd);
        Money? fee = null;
        if (form.Fee is { } feeValue)
        {
            var parsedFee = Money.FromExact(feeValue, Currency.Tnd);
            if (parsedFee.IsFailure)
            {
                return AccountingErrors.StatementLineInvalid;
            }

            fee = parsedFee.Value;
        }

        if (amount.IsFailure)
        {
            return AccountingErrors.StatementLineInvalid;
        }

        var line = new StatementLineInput(form.TransactionDate, form.Reference?.Trim() ?? string.Empty, form.Kind, amount.Value, fee, form.SettledOn, form.Note);
        return line.IsValid
            ? await RecordStatementAsync(agencyId, StatementOrigin.Manual, null, "manual", [line], cancellationToken)
            : AccountingErrors.StatementLineInvalid;
    }

    /// <summary>Deletes a statement brought in by mistake; refused when a day of it is in a closed month.</summary>
    public async Task<Result> DeleteStatementAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        return await _unitOfWork.ExecuteAsync(
            async ct =>
            {
                var statement = await _db.Set<MerchantStatement>().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == statementId, ct);
                if (statement is null)
                {
                    return AccountingErrors.StatementNotFound;
                }

                foreach (var day in statement.Days())
                {
                    var open = await _periods.EnsureOpenAsync(statement.AgencyId, day, ct);
                    if (open.IsFailure)
                    {
                        return open;
                    }
                }

                _db.Set<MerchantStatement>().Remove(statement);
                return Result.Success();
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Every difference of the month, in every area, with its justifications (the closing checks them).</summary>
    internal async Task<IReadOnlyList<DifferenceView>> DifferencesAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken)
    {
        var justifications = await JustificationsAsync(agencyId, month, cancellationToken);
        var cash = await CashAsync(agencyId, month, justifications, cancellationToken);
        var online = await ClicToPayAsync(agencyId, month, justifications, cancellationToken);
        var channels = await ChannelsAsync(agencyId, month, justifications, cancellationToken);
        return [.. cash.Differences, .. online.Differences, .. channels.Differences];
    }

    private static DifferenceView Difference(
        ILookup<string, JustificationView> justifications,
        string key,
        ReconciliationArea area,
        DifferenceKind kind,
        Money amount,
        string subject,
        DateOnly? date) =>
        new(key, area, kind, amount, subject, date, [.. justifications[key]]);

    private static bool Matches(string reference, string orderNumber, string? gatewayOrderId) =>
        string.Equals(reference, orderNumber, StringComparison.OrdinalIgnoreCase)
        || (gatewayOrderId is not null && string.Equals(reference, gatewayOrderId, StringComparison.OrdinalIgnoreCase));

    private async Task<ILookup<string, JustificationView>> JustificationsAsync(Guid agencyId, AccountingMonth month, CancellationToken cancellationToken)
    {
        var rows = await _db.Set<ReconciliationJustification>()
            .AsNoTracking()
            .Where(j => j.AgencyId == agencyId && j.Year == month.Year && j.Month == month.Month)
            .ToListAsync(cancellationToken);
        return rows
            .OrderByDescending(j => j.JustifiedAtUtc)
            .ToLookup(j => j.DifferenceKey, j => new JustificationView(j.Id, j.Reason, j.Amount, j.JustifiedAtUtc, j.JustifiedByName), StringComparer.Ordinal);
    }

    private async Task<CashReconciliationView> CashAsync(Guid agencyId, AccountingMonth month, ILookup<string, JustificationView> justifications, CancellationToken cancellationToken)
    {
        var sessions = await _cash.GetSessionTotalsAsync(month.FirstDay, month.LastDay, agencyId, cancellationToken);
        var recorded = (await _payments.GetCashPaymentsOfSessionsAsync([.. sessions.Select(s => s.SessionId)], cancellationToken))
            .ToLookup(p => p.CashSessionId!.Value);
        var views = new List<CashSessionReconciliationView>(sessions.Count);
        var differences = new List<DifferenceView>();
        foreach (var session in sessions)
        {
            var payments = recorded[session.SessionId].ToList();
            var receivedRecorded = payments.Where(p => p.Direction == PaymentDirectionInfo.Incoming).Select(p => p.Amount).Sum();
            var refundsRecorded = payments.Where(p => p.Direction == PaymentDirectionInfo.Refund).Select(p => p.Amount).Sum();
            views.Add(new CashSessionReconciliationView(
                session.SessionId,
                session.BusinessDate,
                session.CashierName,
                session.Status,
                receivedRecorded,
                session.Totals.PaymentsReceived,
                refundsRecorded,
                session.Totals.RefundsPaid,
                session.Totals.ExpensesPaid - session.Totals.ExpensesReversed,
                session.RevenueVariance,
                session.DepositVariance,
                session.VarianceJustification));

            var mismatch = (receivedRecorded - refundsRecorded) - (session.Totals.PaymentsReceived - session.Totals.RefundsPaid);
            var subject = $"{session.BusinessDate:dd/MM/yyyy} {session.CashierName}";
            if (!mismatch.IsZero)
            {
                differences.Add(Difference(justifications, $"cash:receipts:{session.SessionId:N}", ReconciliationArea.Cash, DifferenceKind.CashReceiptsMismatch, mismatch, subject, session.BusinessDate));
            }

            if (session.Status != CashSessionStatusInfo.Open && session.RevenueVariance is { IsZero: false } revenue)
            {
                differences.Add(Difference(justifications, $"cash:variance:{session.SessionId:N}:revenue", ReconciliationArea.Cash, DifferenceKind.CashVariance, revenue, subject, session.BusinessDate));
            }

            if (session.Status != CashSessionStatusInfo.Open && session.DepositVariance is { IsZero: false } deposit)
            {
                differences.Add(Difference(justifications, $"cash:variance:{session.SessionId:N}:deposit", ReconciliationArea.Cash, DifferenceKind.CashVariance, deposit, subject, session.BusinessDate));
            }
        }

        return new CashReconciliationView(agencyId, month, views, differences);
    }

    private async Task<ClicToPayReconciliationView> ClicToPayAsync(Guid agencyId, AccountingMonth month, ILookup<string, JustificationView> justifications, CancellationToken cancellationToken)
    {
        var online = await _payments.GetOnlinePaymentsAsync(agencyId, month.FirstDay, month.LastDay, cancellationToken);
        var settled = online
            .Where(p => p.SettledAtUtc is { } at && month.Contains(TunisTime.DateOf(at)) && p.CapturedAmount.IsPositive)
            .ToList();
        var lines = await _db.Set<MerchantStatement>()
            .AsNoTracking()
            .Where(s => s.AgencyId == agencyId)
            .SelectMany(s => s.Lines)
            .Where(l => l.TransactionDate >= month.FirstDay && l.TransactionDate <= month.LastDay)
            .ToListAsync(cancellationToken);
        var numbers = await _bookings.GetNumbersAsync([.. online.Select(p => p.BookingId).Distinct()], cancellationToken);
        var byId = online.ToDictionary(p => p.PaymentId);
        var hasStatement = lines.Count > 0;
        var differences = new List<DifferenceView>();

        // Each statement line is matched once with a payment (a refund line names the order of the payment it refunds).
        var matchedLines = new Dictionary<Guid, Guid>();
        var statementAmount = new Dictionary<Guid, Money>();
        foreach (var line in lines.OrderBy(l => l.TransactionDate).ThenBy(l => l.Position))
        {
            var candidates = settled
                .Where(p => !statementAmount.ContainsKey(p.PaymentId))
                .Where(p => line.Kind == StatementLineKind.Payment
                    ? p.Direction == PaymentDirectionInfo.Incoming && Matches(line.Reference, p.OrderNumber, p.GatewayOrderId)
                    : p.Direction == PaymentDirectionInfo.Refund && p.OriginalPaymentId is { } original
                      && (Matches(line.Reference, original.ToString("N"), byId.GetValueOrDefault(original)?.GatewayOrderId) || Matches(line.Reference, p.OrderNumber, p.GatewayOrderId)))
                .OrderBy(p => p.CapturedAmount == line.Amount ? 0 : 1)
                .ThenBy(p => p.SettledAtUtc)
                .ToList();
            if (candidates.FirstOrDefault() is { } payment)
            {
                matchedLines[line.Id] = payment.PaymentId;
                statementAmount[payment.PaymentId] = line.Amount;
            }
            else
            {
                differences.Add(Difference(
                    justifications,
                    $"clictopay:extra:{line.Id:N}",
                    ReconciliationArea.ClicToPay,
                    DifferenceKind.StatementExtra,
                    line.SignedAmount,
                    line.Reference,
                    line.TransactionDate));
            }
        }

        var payments = new List<OnlinePaymentReconciliationView>(settled.Count);
        foreach (var payment in settled)
        {
            var signed = payment.Direction == PaymentDirectionInfo.Incoming ? payment.CapturedAmount : payment.CapturedAmount.Negate();
            var booking = numbers.GetValueOrDefault(payment.BookingId);
            var subject = $"{booking ?? payment.BookingId.ToString("N")[..8]} · {payment.OrderNumber[^8..].ToUpperInvariant()}";
            StatementMatch match;
            Money? onStatement = null;
            if (!hasStatement)
            {
                match = StatementMatch.NoStatement;
            }
            else if (statementAmount.TryGetValue(payment.PaymentId, out var amount))
            {
                onStatement = amount;
                match = amount == payment.CapturedAmount ? StatementMatch.Matched : StatementMatch.AmountMismatch;
                if (match == StatementMatch.AmountMismatch)
                {
                    var gap = payment.Direction == PaymentDirectionInfo.Incoming ? amount - payment.CapturedAmount : payment.CapturedAmount - amount;
                    differences.Add(Difference(justifications, $"clictopay:amount:{payment.PaymentId:N}", ReconciliationArea.ClicToPay, DifferenceKind.StatementAmountMismatch, gap, subject, TunisTime.DateOf(payment.SettledAtUtc!.Value)));
                }
            }
            else
            {
                match = StatementMatch.Missing;
                differences.Add(Difference(justifications, $"clictopay:missing:{payment.PaymentId:N}", ReconciliationArea.ClicToPay, DifferenceKind.StatementMissing, signed, subject, TunisTime.DateOf(payment.SettledAtUtc!.Value)));
            }

            payments.Add(new OnlinePaymentReconciliationView(
                payment.PaymentId,
                booking,
                payment.Direction,
                payment.Status,
                payment.CapturedAmount,
                payment.OrderNumber,
                payment.GatewayOrderId,
                payment.GatewayStatusCode,
                payment.Attention,
                TunisTime.DateOf(payment.SettledAtUtc!.Value),
                match,
                onStatement));
        }

        // The flags of the gateway checks (2B) on the payments of the month, settled or not.
        foreach (var flagged in online.Where(p => p.Attention is not null))
        {
            var booking = numbers.GetValueOrDefault(flagged.BookingId);
            var subject = $"{booking ?? flagged.BookingId.ToString("N")[..8]} · {flagged.OrderNumber[^8..].ToUpperInvariant()}";
            differences.Add(Difference(
                justifications,
                $"clictopay:attention:{flagged.PaymentId:N}",
                ReconciliationArea.ClicToPay,
                DifferenceKind.GatewayAttention,
                flagged.Direction == PaymentDirectionInfo.Incoming ? flagged.Amount : flagged.Amount.Negate(),
                subject,
                TunisTime.DateOf(flagged.SettledAtUtc ?? flagged.RecordedAtUtc)));
        }

        return new ClicToPayReconciliationView(
            agencyId,
            month,
            hasStatement,
            payments,
            [.. lines.OrderBy(l => l.TransactionDate).ThenBy(l => l.Position).Select(l => new StatementLineView(l.Id, l.StatementId, l.TransactionDate, l.Reference, l.Kind, l.Amount, l.Fee, l.SettledOn, matchedLines.ContainsKey(l.Id), l.Note))],
            differences);
    }

    private async Task<ChannelsReconciliationView> ChannelsAsync(Guid agencyId, AccountingMonth month, ILookup<string, JustificationView> justifications, CancellationToken cancellationToken)
    {
        var settlements = await _channels.GetSettlementsAsync(agencyId, month.Year, month.Month, cancellationToken);
        var differences = new List<DifferenceView>();
        foreach (var settlement in settlements)
        {
            if (settlement.Difference is { IsZero: false } difference)
            {
                differences.Add(Difference(justifications, $"channel:payout:{settlement.ChannelId:N}", ReconciliationArea.BookingCom, DifferenceKind.PayoutDifference, difference, settlement.ChannelName, null));
            }
            else if (settlement.Payout is null && !settlement.ExpectedSettlement.IsZero)
            {
                differences.Add(Difference(justifications, $"channel:missing:{settlement.ChannelId:N}", ReconciliationArea.BookingCom, DifferenceKind.PayoutMissing, settlement.ExpectedSettlement, settlement.ChannelName, null));
            }
        }

        return new ChannelsReconciliationView(agencyId, month, settlements, differences);
    }

    private async Task<Result<Guid>> RecordStatementAsync(
        Guid agencyId,
        StatementOrigin origin,
        string? fileName,
        string format,
        IReadOnlyCollection<StatementLineInput> lines,
        CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteAsync<Guid>(
            async ct =>
            {
                // An operation of one agency records the statements of that agency only (ADR-0067).
                if (!_user.IsAcrossAgencies && _user.AgencyId != agencyId)
                {
                    return AccountingErrors.AgencyNotFound;
                }

                var days = lines.Select(l => l.TransactionDate).Concat(lines.Where(l => l.SettledOn is not null).Select(l => l.SettledOn!.Value)).Distinct();
                foreach (var day in days)
                {
                    var open = await _periods.EnsureOpenAsync(agencyId, day, ct);
                    if (open.IsFailure)
                    {
                        return open.Error;
                    }
                }

                var statement = MerchantStatement.Record(agencyId, origin, fileName, format, lines, _user.ToAuditActor(), _clock.GetUtcNow());
                if (statement.IsFailure)
                {
                    return statement.Error;
                }

                _db.Set<MerchantStatement>().Add(statement.Value);
                return statement.Value.Id;
            },
            cancellationToken: cancellationToken);

    private static async Task<byte[]?> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxStatementBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }
}
