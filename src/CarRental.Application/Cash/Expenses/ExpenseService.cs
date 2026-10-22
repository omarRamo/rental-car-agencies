using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Accounting.Contracts;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Cash.Expenses;

/// <summary>
/// The expenses of the back office (<c>/admin/expenses</c>, permission <c>expenses.manage</c>, ADR-0063): record, change and
/// delete (logically, with a reason) the expenses of an agency until their month is closed (<see cref="IAccountingPeriods"/>);
/// an expense paid in cash is an outflow of the open cash session of the user (also <c>cash.own</c>), recorded in the same
/// transaction, and its deletion puts the cash back in a session; the receipt (photo or PDF, real type checked, 10 MB at
/// most) goes to the private container <c>invoices</c> under <c>expenses/</c> — accounting pieces, written once and kept
/// like the invoices (ADR-0061). Every change is audited automatically and kept by the temporal history.
/// </summary>
public sealed class ExpenseService
{
    /// <summary>The receipts are accounting pieces: kept with the invoices, under their own prefix.</summary>
    public const string ReceiptContainer = FileContainers.Invoices;

    /// <summary>Largest receipt accepted (10 MB, like the files of the claims).</summary>
    public const long MaxReceiptBytes = ExpenseReceiptFiles.MaxBytes;

    private const int SaveAttempts = 3;

    private readonly IAppDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserContext _user;
    private readonly IReferentialQueries _referential;
    private readonly IFleetQueries _fleet;
    private readonly IFleetCostRecords _fleetCosts;
    private readonly IBillingAccountingQueries _billing;
    private readonly IAccountingPeriods _periods;
    private readonly IExpenseNumberLock _numbers;
    private readonly IFileStorage _storage;
    private readonly TimeProvider _clock;

    internal ExpenseService(
        IAppDbContext db,
        IUnitOfWork unitOfWork,
        IUserContext user,
        IReferentialQueries referential,
        IFleetQueries fleet,
        IFleetCostRecords fleetCosts,
        IBillingAccountingQueries billing,
        IAccountingPeriods periods,
        IExpenseNumberLock numbers,
        IFileStorage storage,
        TimeProvider clock)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _user = user;
        _referential = referential;
        _fleet = fleet;
        _fleetCosts = fleetCosts;
        _billing = billing;
        _periods = periods;
        _numbers = numbers;
        _storage = storage;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.ExpensesManage);

    private DateOnly Today => TunisTime.Today(_clock);

    /// <summary>The expenses of an agency, the latest first, with the totals of the filtered list (deleted ones never count).</summary>
    public async Task<Result<ExpenseList>> ListAsync(ExpenseFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var query = filter.IncludeDeleted
            ? _db.Set<Expense>().AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete])
            : _db.Set<Expense>().AsNoTracking().Where(e => !e.IsDeleted);
        query = query.Where(e => e.AgencyId == filter.AgencyId);
        if (filter.From is { } from)
        {
            query = query.Where(e => e.Date >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(e => e.Date <= to);
        }

        if (filter.Category is { } category)
        {
            var wanted = category.ToDomain();
            query = query.Where(e => e.Category == wanted);
        }

        if (filter.PaymentMethod is { } method)
        {
            var wanted = method.ToDomain();
            query = query.Where(e => e.PaymentMethod == wanted);
        }

        if (filter.VehicleId is { } vehicleId)
        {
            query = query.Where(e => e.VehicleId == vehicleId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            query = query.Where(e => e.Supplier.Contains(text) || e.Number.Contains(text) || (e.Reference != null && e.Reference.Contains(text)) || (e.Description != null && e.Description.Contains(text)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.Year)
            .ThenByDescending(e => e.Sequence)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);
        var amounts = await query
            .Where(e => !e.IsDeleted)
            .Select(e => new { e.ExclVat, e.Vat, e.InclVat })
            .ToListAsync(cancellationToken);
        var closed = await ClosedMonthsAsync(filter.AgencyId, cancellationToken);
        return new ExpenseList(
            new PagedResult<ExpenseView>([.. items.Select(e => e.ToView(closed.Contains(MonthOf(e.Date))))], total, page.Page, page.PageSize),
            amounts.Count,
            amounts.Select(a => a.ExclVat).Sum(),
            amounts.Select(a => a.Vat).Sum(),
            amounts.Select(a => a.InclVat).Sum());
    }

    /// <summary>An expense (a deleted one too, with its deletion).</summary>
    public async Task<Result<ExpenseView>> GetAsync(Guid expenseId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var expense = await _db.Set<Expense>().AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete]).FirstOrDefaultAsync(e => e.Id == expenseId, cancellationToken);
        if (expense is null)
        {
            return ExpenseErrors.NotFound;
        }

        var closed = await ClosedMonthsAsync(expense.AgencyId, cancellationToken);
        return expense.ToView(closed.Contains(MonthOf(expense.Date)));
    }

    /// <summary>The vehicles of an agency (sold ones excepted), by plate: those an expense of the fleet may name.</summary>
    public async Task<Result<IReadOnlyList<ExpenseVehicleOption>>> GetVehiclesAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var options = new List<ExpenseVehicleOption>();
        foreach (var category in await _fleet.GetCategoriesAsync(agencyId, includeInactive: true, cancellationToken))
        {
            options.AddRange((await _fleet.GetVehiclesAsync(category.Id, cancellationToken))
                .Select(v => new ExpenseVehicleOption(v.Id, v.PlateNumber, $"{v.PlateNumber} — {v.Make} {v.Model}")));
        }

        return Result.Success<IReadOnlyList<ExpenseVehicleOption>>([.. options.OrderBy(o => o.PlateNumber, StringComparer.Ordinal)]);
    }

    /// <summary>The maintenances and the claims of a vehicle of the agency, which an expense may pay.</summary>
    public async Task<Result<ExpenseLinkOptions>> GetLinksAsync(Guid agencyId, Guid vehicleId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var vehicle = await _fleet.GetVehicleAsync(vehicleId, cancellationToken);
        if (vehicle is null || vehicle.AgencyId != agencyId)
        {
            return ExpenseErrors.VehicleNotFound;
        }

        var maintenances = await _fleetCosts.GetMaintenancesAsync(vehicleId, cancellationToken);
        var claims = await _billing.GetClaimsAsync(agencyId, vehicleId, cancellationToken);
        return new ExpenseLinkOptions(maintenances, claims);
    }

    /// <summary>
    /// Records an expense; paid in cash, its outflow goes into the open cash session of the user in the same transaction
    /// (refused when the session does not hold the cash). Refused in a closed month.
    /// </summary>
    public async Task<Result<Guid>> RecordAsync(ExpenseForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        var details = await PrepareAsync(form, form.AgencyId, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error;
        }

        var paidInCash = details.Value.PaymentMethod == ExpensePaymentMethod.Cash;
        if (paidInCash && !_user.HasPermission(Permissions.CashOwn))
        {
            return CashErrors.Forbidden;
        }

        return await SaveWithRetryAsync<Guid>(
            async ct =>
            {
                var open = await _periods.EnsureOpenAsync(details.Value.AgencyId, details.Value.Date, ct);
                if (open.IsFailure)
                {
                    return open.Error;
                }

                CashSession? session = null;
                if (paidInCash)
                {
                    var own = await OwnOpenSessionAsync(details.Value.AgencyId, ct);
                    if (own.IsFailure)
                    {
                        return own.Error;
                    }

                    session = own.Value;
                }

                // One expense of the agency numbered at a time: no collision on the counter of the year.
                var numbering = await _numbers.AcquireAsync(details.Value.AgencyId, details.Value.Date.Year, ct);
                if (numbering.IsFailure)
                {
                    return numbering.Error;
                }

                var number = await NextNumberAsync(details.Value.AgencyId, details.Value.Date.Year, ct);
                var actor = _user.ToAuditActor();
                var now = _clock.GetUtcNow();
                var recorded = Expense.Record(details.Value, number, session?.Id, Today, actor, now);
                if (recorded.IsFailure)
                {
                    return recorded.Error;
                }

                var expense = recorded.Value;
                if (session is not null)
                {
                    var paid = session.RecordExpensePaid(expense.Id, expense.InclVat, $"{expense.Number} {expense.Supplier}", actor, now);
                    if (paid.IsFailure)
                    {
                        return paid.Error;
                    }
                }

                _db.Set<Expense>().Add(expense);
                return expense.Id;
            },
            cancellationToken);
    }

    /// <summary>
    /// Changes an expense shown in version <paramref name="revision"/>: refused when its month or the month of its new date
    /// is closed; an expense paid in cash keeps its date, amounts and means of payment.
    /// </summary>
    public async Task<Result> ChangeAsync(Guid expenseId, int revision, ExpenseForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var current = await _db.Set<Expense>()
            .AsNoTracking()
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(e => e.Id == expenseId)
            .Select(e => new { e.AgencyId, e.IsDeleted })
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return ExpenseErrors.NotFound;
        }

        if (current.IsDeleted)
        {
            return ExpenseErrors.Deleted;
        }

        var details = await PrepareAsync(form, current.AgencyId, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error;
        }

        var saved = await SaveWithRetryAsync<bool>(
            async ct =>
            {
                var expense = await _db.Set<Expense>().FirstOrDefaultAsync(e => e.Id == expenseId, ct);
                if (expense is null)
                {
                    return ExpenseErrors.NotFound;
                }

                var before = await _periods.EnsureOpenAsync(expense.AgencyId, expense.Date, ct);
                if (before.IsFailure)
                {
                    return before.Error;
                }

                var after = await _periods.EnsureOpenAsync(expense.AgencyId, details.Value.Date, ct);
                if (after.IsFailure)
                {
                    return after.Error;
                }

                var changed = expense.Change(details.Value, revision, Today, _user.ToAuditActor(), _clock.GetUtcNow());
                return changed.IsSuccess ? true : changed.Error;
            },
            cancellationToken,
            retryOnConflict: false);
        return saved.IsSuccess ? Result.Success() : saved.Error;
    }

    /// <summary>
    /// Deletes an expense with a reason (logical deletion: it stays visible as deleted, audited); a cash expense puts its
    /// cash back into the open cash session of the user. Refused in a closed month.
    /// </summary>
    public async Task<Result> DeleteAsync(Guid expenseId, string? reason, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        // The reason first: nothing is put back in a cash session for a deletion that would be refused.
        var reasonChecked = Expense.CheckDeletionReason(reason);
        if (reasonChecked.IsFailure)
        {
            return reasonChecked;
        }

        var deleted = await SaveWithRetryAsync<bool>(
            async ct =>
            {
                var expense = await _db.Set<Expense>().FirstOrDefaultAsync(e => e.Id == expenseId, ct);
                if (expense is null)
                {
                    return await _db.Set<Expense>().IgnoreQueryFilters([QueryFilters.SoftDelete]).AnyAsync(e => e.Id == expenseId, ct) ? ExpenseErrors.Deleted : ExpenseErrors.NotFound;
                }

                if (expense.IsDeleted)
                {
                    return ExpenseErrors.Deleted;
                }

                var open = await _periods.EnsureOpenAsync(expense.AgencyId, expense.Date, ct);
                if (open.IsFailure)
                {
                    return open.Error;
                }

                var actor = _user.ToAuditActor();
                var now = _clock.GetUtcNow();
                Guid? reversedIn = null;
                if (expense.IsPaidInCash)
                {
                    if (!_user.HasPermission(Permissions.CashOwn))
                    {
                        return CashErrors.Forbidden;
                    }

                    var session = await OwnOpenSessionAsync(expense.AgencyId, ct);
                    if (session.IsFailure)
                    {
                        return session.Error;
                    }

                    var reversed = session.Value.RecordExpenseReversed(expense.Id, expense.InclVat, $"{expense.Number} {expense.Supplier}", expense.CashSessionId, actor, now);
                    if (reversed.IsFailure)
                    {
                        return reversed.Error;
                    }

                    reversedIn = session.Value.Id;
                }

                var outcome = expense.Delete(reason, reversedIn, actor, now);
                return outcome.IsSuccess ? true : outcome.Error;
            },
            cancellationToken,
            retryOnConflict: false);
        return deleted.IsSuccess ? Result.Success() : deleted.Error;
    }

    /// <summary>
    /// Stores the receipt of an expense (its real type read from its first bytes: JPEG, PNG, WebP, PDF; 10 MB at most) at a
    /// path of its own, never overwritten; a replaced receipt stays in the container. A receipt may arrive after the closing
    /// of the month: it changes no amount.
    /// </summary>
    public async Task<Result> UploadReceiptAsync(Guid expenseId, string? fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var bytes = await ExpenseReceiptFiles.ReadBoundedAsync(content, ExpenseReceiptFiles.MaxBytes, cancellationToken);
        if (bytes is null || ExpenseReceiptFiles.TypeOf(bytes) is not { } type)
        {
            return ExpenseErrors.ReceiptInvalid;
        }

        var existing = await _db.Set<Expense>().AsNoTracking().Where(e => e.Id == expenseId).Select(e => new { e.Year }).FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return ExpenseErrors.NotFound;
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"expenses/{existing.Year:D4}/{expenseId:N}/{Guid.CreateVersion7():N}.{type.Extension}");
        await using (var stream = new MemoryStream(bytes, writable: false))
        {
            await _storage.UploadAsync(ReceiptContainer, path, stream, type.ContentType, cancellationToken);
        }

        var receipt = new ExpenseReceipt(path, type.ContentType, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), ExpenseReceiptFiles.NameOf(fileName));
        try
        {
            var expense = await _db.Set<Expense>().FirstOrDefaultAsync(e => e.Id == expenseId, cancellationToken);
            if (expense is null)
            {
                return ExpenseErrors.NotFound;
            }

            var attached = expense.AttachReceipt(receipt, _user.ToAuditActor(), _clock.GetUtcNow());
            if (attached.IsSuccess)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            // A refused receipt stays in the container (written once, like the invoices): it is referenced by nothing.
            return attached;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ExpenseErrors.Stale;
        }
    }

    /// <summary>The receipt of an expense, to stream to the back office.</summary>
    public async Task<Result<ExpenseReceiptFile>> FindReceiptAsync(Guid expenseId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var receipt = await _db.Set<Expense>()
            .AsNoTracking()
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(e => e.Id == expenseId)
            .Select(e => new { e.Number, e.ReceiptPath, e.ReceiptContentType, e.ReceiptHash, e.ReceiptFileName })
            .FirstOrDefaultAsync(cancellationToken);
        if (receipt is null)
        {
            return ExpenseErrors.NotFound;
        }

        if (receipt.ReceiptPath is not { } path || receipt.ReceiptContentType is not { } contentType)
        {
            return ExpenseErrors.ReceiptNotFound;
        }

        var hash = receipt.ReceiptHash ?? path;
        var extension = Path.GetExtension(path);
        return new ExpenseReceiptFile(ReceiptContainer, path, contentType, hash.Length > 16 ? hash[..16] : hash, receipt.ReceiptFileName ?? $"{receipt.Number}{extension}");
    }

    public Task<FileDownload?> OpenReceiptAsync(ExpenseReceiptFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return _storage.OpenReadAsync(file.Container, file.Path, cancellationToken);
    }

    private static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>Checks the permission, the agency, the amounts, the details and the links of the vehicle against the other modules.</summary>
    private async Task<Result<ExpenseDetails>> PrepareAsync(ExpenseForm form, Guid agencyId, CancellationToken cancellationToken)
    {
        if (!CanManage)
        {
            return ExpenseErrors.Forbidden;
        }

        var agencies = await _referential.GetAgenciesAsync(cancellationToken);
        if (!agencies.Any(a => a.Id == agencyId && a.IsActive))
        {
            return ExpenseErrors.AgencyNotFound;
        }

        if (!Enum.IsDefined(form.AmountMode))
        {
            return ExpenseErrors.AmountInvalid;
        }

        var amounts = ExpenseMapping.AmountsOf(form);
        if (amounts.IsFailure)
        {
            return amounts.Error;
        }

        var details = Expense.Validate(
            new ExpenseDetails(
                agencyId,
                form.Date,
                form.Category.ToDomain(),
                form.Supplier,
                amounts.Value,
                form.PaymentMethod.ToDomain(),
                form.PaymentReference,
                form.SupplierTaxId,
                form.Reference,
                form.Description,
                form.VehicleId,
                form.MaintenanceEventId,
                form.ClaimId),
            Today);
        if (details.IsFailure)
        {
            return details.Error;
        }

        var links = await CheckLinksAsync(details.Value, cancellationToken);
        return links.IsFailure ? links.Error : details;
    }

    private async Task<Result> CheckLinksAsync(ExpenseDetails details, CancellationToken cancellationToken)
    {
        if (details.VehicleId is not { } vehicleId)
        {
            return Result.Success();
        }

        var vehicle = await _fleet.GetVehicleAsync(vehicleId, cancellationToken);
        if (vehicle is null || vehicle.AgencyId != details.AgencyId)
        {
            return ExpenseErrors.VehicleNotFound;
        }

        if (details.MaintenanceEventId is { } maintenanceId
            && !(await _fleetCosts.GetMaintenancesAsync(vehicleId, cancellationToken)).Any(m => m.Id == maintenanceId))
        {
            return ExpenseErrors.MaintenanceNotFound;
        }

        if (details.ClaimId is { } claimId
            && !(await _billing.GetClaimsAsync(details.AgencyId, vehicleId, cancellationToken)).Any(c => c.Id == claimId))
        {
            return ExpenseErrors.ClaimNotFound;
        }

        return Result.Success();
    }

    /// <summary>The open cash session of the user, which must belong to the agency and to an open month.</summary>
    private async Task<Result<CashSession>> OwnOpenSessionAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        if (_user.UserId is not { } userId)
        {
            return CashErrors.NoOpenSession;
        }

        var session = await _db.Set<CashSession>().FirstOrDefaultAsync(s => s.CashierId == userId && s.Status == CashSessionStatus.Open, cancellationToken);
        if (session is null)
        {
            return CashErrors.NoOpenSession;
        }

        if (session.AgencyId != agencyId)
        {
            return CashErrors.SessionAgencyMismatch;
        }

        var open = await _periods.EnsureOpenAsync(agencyId, session.BusinessDate, cancellationToken);
        return open.IsFailure ? open.Error : session;
    }

    private async Task<ExpenseNumber> NextNumberAsync(Guid agencyId, int year, CancellationToken cancellationToken)
    {
        var sequence = await _db.Set<ExpenseSequence>().FirstOrDefaultAsync(s => s.AgencyId == agencyId && s.Year == year, cancellationToken);
        if (sequence is null)
        {
            sequence = ExpenseSequence.Start(agencyId, year);
            _db.Set<ExpenseSequence>().Add(sequence);
        }

        return sequence.Next();
    }

    private async Task<HashSet<DateOnly>> ClosedMonthsAsync(Guid agencyId, CancellationToken cancellationToken) =>
        [.. await _periods.GetClosedMonthsAsync(agencyId, cancellationToken)];

    /// <summary>
    /// Runs the operation in a unit of work; a cash session changed meanwhile (or, should the numbering lock not hold, two
    /// expenses numbered at once) makes it run again, a few times.
    /// </summary>
    private async Task<Result<T>> SaveWithRetryAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken, bool retryOnConflict = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _unitOfWork.ExecuteAsync(operation, cancellationToken: cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (retryOnConflict && attempt < SaveAttempts)
            {
                // Another expense took the number (or the cash session changed): the operation runs again on fresh data.
            }
            catch (DbUpdateConcurrencyException)
            {
                return retryOnConflict ? ExpenseErrors.ConcurrentUpdate : ExpenseErrors.Stale;
            }
            catch (DbUpdateException) when (retryOnConflict && attempt < SaveAttempts)
            {
                // The first expense of the year was numbered twice at once (unique index of the counter): run again.
            }
        }
    }
}
