using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Tenancy;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Accounting.Chart;

/// <summary>An account of the chart as the accounting screen shows it, with the operation types mapped to it.</summary>
public sealed record AccountView(
    Guid Id,
    string Number,
    string Label,
    int Class,
    bool IsActive,
    bool IsDefault,
    bool NeedsValidation,
    DateTimeOffset ChangedAtUtc,
    string? ChangedBy,
    IReadOnlyList<string> MappedKeys);

/// <summary>An operation type of the mapping and its account (and the default account of the catalogue).</summary>
public sealed record MappingView(
    string Key,
    MappingGroup Group,
    string AccountNumber,
    string? AccountLabel,
    bool AccountNeedsValidation,
    string DefaultAccount,
    DateTimeOffset? ChangedAtUtc,
    string? ChangedBy);

/// <summary>
/// The chart of accounts and the mapping of the operation types of the agency of the operation (<c>/admin/accounting</c>,
/// permission <c>accounting.manage</c>, ADR-0065 and ADR-0067): each agency has its own chart, provisioned with a default
/// inspired by the Tunisian accounting system "to be validated by the chartered accountant"
/// (<see cref="AccountingAgencyInitializer"/>). An account number never changes; an account mapped to an operation type
/// stays active. An operation that works for no agency (the support outside any agency) has no chart.
/// </summary>
public sealed class ChartOfAccountsService
{
    private readonly IAppDbContext _db;
    private readonly IUserContext _user;
    private readonly TimeProvider _clock;

    internal ChartOfAccountsService(IAppDbContext db, IUserContext user, TimeProvider clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    private bool CanManage => _user.HasPermission(Permissions.AccountingManage);

    public async Task<Result<IReadOnlyList<AccountView>>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agencyId = _user.AgencyId!.Value;
        var accounts = await _db.Set<LedgerAccount>().AsNoTracking().Where(a => a.AgencyId == agencyId).ToListAsync(cancellationToken);
        var mappings = await _db.Set<AccountMapping>().AsNoTracking().Where(m => m.AgencyId == agencyId).ToListAsync(cancellationToken);
        var keys = mappings.ToLookup(m => m.AccountNumber, m => m.Key, StringComparer.Ordinal);
        return Result.Success<IReadOnlyList<AccountView>>(
        [
            .. accounts
                .OrderBy(a => a.Number, StringComparer.Ordinal)
                .Select(a => new AccountView(a.Id, a.Number, a.Label, a.Class, a.IsActive, a.IsDefault, a.NeedsValidation, a.ChangedAtUtc, a.ChangedBy, [.. keys[a.Number].Order(StringComparer.Ordinal)])),
        ]);
    }

    public async Task<Result<IReadOnlyList<MappingView>>> ListMappingAsync(CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agencyId = _user.AgencyId!.Value;
        var accounts = (await _db.Set<LedgerAccount>().AsNoTracking().Where(a => a.AgencyId == agencyId).ToListAsync(cancellationToken))
            .ToDictionary(a => a.Number, StringComparer.Ordinal);
        var mappings = (await _db.Set<AccountMapping>().AsNoTracking().Where(m => m.AgencyId == agencyId).ToListAsync(cancellationToken))
            .ToDictionary(m => m.Key, StringComparer.Ordinal);
        return Result.Success<IReadOnlyList<MappingView>>(
        [
            .. AccountingMappingCatalog.All.Select(definition =>
            {
                var mapping = mappings.GetValueOrDefault(definition.Key);
                var number = mapping?.AccountNumber ?? definition.DefaultAccount;
                var account = accounts.GetValueOrDefault(number);
                return new MappingView(definition.Key, definition.Group, number, account?.Label, account?.NeedsValidation ?? false, definition.DefaultAccount, mapping?.ChangedAtUtc, mapping?.ChangedBy);
            }),
        ]);
    }

    /// <summary>Adds an account to the chart of the agency (its number is unique in the agency and never changes).</summary>
    public async Task<Result<Guid>> CreateAccountAsync(string number, string label, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agencyId = _user.AgencyId!.Value;
        var created = LedgerAccount.Create(agencyId, number, label, isDefault: false, _user.ToAuditActor(), _clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await _db.Set<LedgerAccount>().AnyAsync(a => a.AgencyId == agencyId && a.Number == created.Value.Number, cancellationToken))
        {
            return AccountingErrors.AccountNumberTaken;
        }

        _db.Set<LedgerAccount>().Add(created.Value);
        return await SaveAsync(created.Value.Id, AccountingErrors.AccountNumberTaken, cancellationToken);
    }

    public Task<Result> RenameAccountAsync(Guid accountId, string label, CancellationToken cancellationToken = default) =>
        ChangeAccountAsync(accountId, (account, actor, now) => account.Rename(label, actor, now), cancellationToken);

    /// <summary>Confirms a default account as validated by the chartered accountant.</summary>
    public Task<Result> ConfirmAccountAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        ChangeAccountAsync(accountId, (account, actor, now) => account.Confirm(actor, now), cancellationToken);

    /// <summary>Activates or deactivates an account; an account mapped to an operation type stays active.</summary>
    public async Task<Result> SetAccountActiveAsync(Guid accountId, bool active, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agencyId = _user.AgencyId!.Value;
        var account = await _db.Set<LedgerAccount>().FirstOrDefaultAsync(a => a.Id == accountId && a.AgencyId == agencyId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        if (!active && await IsMappedAsync(agencyId, account.Number, cancellationToken))
        {
            return AccountingErrors.AccountInUse;
        }

        var changed = account.SetActive(active, _user.ToAuditActor(), _clock.GetUtcNow());
        return changed.IsFailure ? changed : await SaveAsync(cancellationToken);
    }

    /// <summary>Maps an operation type to an active account of the chart of the agency.</summary>
    public async Task<Result> MapAsync(string key, string accountNumber, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        if (!AccountingMappingCatalog.IsKnown(key))
        {
            return AccountingErrors.MappingKeyUnknown;
        }

        var agencyId = _user.AgencyId!.Value;
        var number = LedgerAccount.ValidateNumber(accountNumber);
        if (number.IsFailure || !await _db.Set<LedgerAccount>().AnyAsync(a => a.AgencyId == agencyId && a.Number == number.Value && a.IsActive, cancellationToken))
        {
            return AccountingErrors.MappingAccountInvalid;
        }

        var actor = _user.ToAuditActor();
        var now = _clock.GetUtcNow();
        var mapping = await _db.Set<AccountMapping>().FirstOrDefaultAsync(m => m.AgencyId == agencyId && m.Key == key, cancellationToken);
        if (mapping is null)
        {
            var created = AccountMapping.Create(agencyId, key, number.Value, actor, now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            _db.Set<AccountMapping>().Add(created.Value);
        }
        else
        {
            var changed = mapping.MapTo(number.Value, actor, now);
            if (changed.IsFailure)
            {
                return changed;
            }
        }

        return await SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The account of every operation type of <paramref name="agencyId"/> for the generation of its journals (mapped and
    /// active accounts of its own chart only).
    /// </summary>
    internal static async Task<AccountResolver> ResolverAsync(IAppDbContext db, Guid agencyId, CancellationToken cancellationToken)
    {
        var active = (await db.Set<LedgerAccount>().AsNoTracking().Where(a => a.AgencyId == agencyId && a.IsActive).Select(a => a.Number).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var mapped = await db.Set<AccountMapping>()
            .AsNoTracking()
            .Where(m => m.AgencyId == agencyId)
            .ToDictionaryAsync(m => m.Key, m => m.AccountNumber, StringComparer.Ordinal, cancellationToken);
        return new AccountResolver(mapped.Where(m => active.Contains(m.Value)).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal));
    }

    /// <summary>The labels of the accounts of the chart of <paramref name="agencyId"/>, by number.</summary>
    internal static async Task<IReadOnlyDictionary<string, string>> LabelsAsync(IAppDbContext db, Guid agencyId, CancellationToken cancellationToken) =>
        await db.Set<LedgerAccount>()
            .AsNoTracking()
            .Where(a => a.AgencyId == agencyId)
            .ToDictionaryAsync(a => a.Number, a => a.Label, StringComparer.Ordinal, cancellationToken);

    /// <summary>Refused without the permission, or for an operation that works for no agency (the chart is the agency's).</summary>
    private Error? Denied() =>
        !CanManage ? AccountingErrors.Forbidden
        : _user.AgencyId is null ? AccountingErrors.AgencyNotFound
        : null;

    private async Task<bool> IsMappedAsync(Guid agencyId, string number, CancellationToken cancellationToken) =>
        await _db.Set<AccountMapping>().AnyAsync(m => m.AgencyId == agencyId && m.AccountNumber == number, cancellationToken);

    private async Task<Result> ChangeAccountAsync(Guid accountId, Func<LedgerAccount, AuditActor, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agencyId = _user.AgencyId!.Value;
        var account = await _db.Set<LedgerAccount>().FirstOrDefaultAsync(a => a.Id == accountId && a.AgencyId == agencyId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        var changed = change(account, _user.ToAuditActor(), _clock.GetUtcNow());
        return changed.IsFailure ? changed : await SaveAsync(cancellationToken);
    }

    private async Task<Result> SaveAsync(CancellationToken cancellationToken)
    {
        var saved = await SaveAsync(true, AccountingErrors.ConcurrentUpdate, cancellationToken);
        return saved.IsSuccess ? Result.Success() : saved.Error;
    }

    private async Task<Result<T>> SaveAsync<T>(T value, Error duplicate, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return value;
        }
        catch (DbUpdateConcurrencyException)
        {
            return AccountingErrors.ConcurrentUpdate;
        }
        catch (DbUpdateException)
        {
            // A concurrent creation won the unique index (account number, mapping key of the agency).
            return duplicate;
        }
    }
}

/// <summary>
/// The account of each operation type for a generation of the journals: the mapping restricted to active accounts. A key
/// without account is remembered (the generation then fails with <c>accounting.mapping.missing</c> and lists the keys).
/// </summary>
internal sealed class AccountResolver(IReadOnlyDictionary<string, string> accounts)
{
    private readonly SortedSet<string> _missing = new(StringComparer.Ordinal);

    /// <summary>Keys asked for without an account.</summary>
    public IReadOnlyCollection<string> Missing => _missing;

    /// <summary>
    /// The account of <paramref name="key"/>; a missing one is remembered and a valid placeholder is returned so that the
    /// generation goes on and lists every missing key at once (its entries are then never used).
    /// </summary>
    public string Resolve(string key)
    {
        if (accounts.TryGetValue(key, out var number))
        {
            return number;
        }

        _missing.Add(key);
        return "99";
    }
}

/// <summary>
/// Provisions the chart of accounts of an agency (ADR-0067): the default chart and the mapping of every operation type,
/// when the support registers the agency and at every start for each agency. Idempotent: it only adds the accounts and the
/// keys the agency is missing (a key added by a later version of the catalogue gets its default account), never changes
/// what the accountant changed.
/// </summary>
internal sealed class AccountingAgencyInitializer(IAppDbContext db, TimeProvider clock) : IAgencyInitializer
{
    public const string ActorName = "system:accounting-chart";

    public int Order => 600;

    public async Task InitializeAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var actor = AuditActor.System(ActorName);
        var now = clock.GetUtcNow();
        var numbers = (await db.Set<LedgerAccount>().Where(a => a.AgencyId == agencyId).Select(a => a.Number).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var keys = (await db.Set<AccountMapping>().Where(m => m.AgencyId == agencyId).Select(m => m.Key).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var accounts = AccountingMappingCatalog.DefaultChart.Where(a => !numbers.Contains(a.Number)).ToList();
        var definitions = AccountingMappingCatalog.All.Where(d => !keys.Contains(d.Key)).ToList();
        if (accounts.Count == 0 && definitions.Count == 0)
        {
            return;
        }

        foreach (var account in accounts)
        {
            db.Set<LedgerAccount>().Add(LedgerAccount.Create(agencyId, account.Number, account.Label, isDefault: true, actor, now).Value);
        }

        foreach (var definition in definitions)
        {
            db.Set<AccountMapping>().Add(AccountMapping.Create(agencyId, definition.Key, definition.DefaultAccount, actor, now).Value);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
