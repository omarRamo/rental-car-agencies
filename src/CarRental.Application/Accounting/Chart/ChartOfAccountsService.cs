using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Seeding;
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
/// The chart of accounts and the mapping of the operation types (<c>/admin/accounting</c>, permission
/// <c>accounting.manage</c>, ADR-0065): one chart for the platform (TODO(spec): a chart per agency if the agencies are
/// distinct companies), seeded with a default inspired by the Tunisian accounting system "to be validated by the chartered
/// accountant". An account number never changes; an account mapped to an operation type stays active.
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
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var accounts = await _db.Set<LedgerAccount>().AsNoTracking().ToListAsync(cancellationToken);
        var mappings = await _db.Set<AccountMapping>().AsNoTracking().ToListAsync(cancellationToken);
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
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var accounts = (await _db.Set<LedgerAccount>().AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(a => a.Number, StringComparer.Ordinal);
        var mappings = (await _db.Set<AccountMapping>().AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(m => m.Key, StringComparer.Ordinal);
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

    /// <summary>Adds an account to the chart (its number is unique and never changes).</summary>
    public async Task<Result<Guid>> CreateAccountAsync(string number, string label, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var created = LedgerAccount.Create(number, label, isDefault: false, _user.ToAuditActor(), _clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (await _db.Set<LedgerAccount>().AnyAsync(a => a.Number == created.Value.Number, cancellationToken))
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
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var account = await _db.Set<LedgerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return AccountingErrors.AccountNotFound;
        }

        if (!active && await IsMappedAsync(account.Number, cancellationToken))
        {
            return AccountingErrors.AccountInUse;
        }

        var changed = account.SetActive(active, _user.ToAuditActor(), _clock.GetUtcNow());
        return changed.IsFailure ? changed : await SaveAsync(cancellationToken);
    }

    /// <summary>Maps an operation type to an active account of the chart.</summary>
    public async Task<Result> MapAsync(string key, string accountNumber, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        if (!AccountingMappingCatalog.IsKnown(key))
        {
            return AccountingErrors.MappingKeyUnknown;
        }

        var number = LedgerAccount.ValidateNumber(accountNumber);
        if (number.IsFailure || !await _db.Set<LedgerAccount>().AnyAsync(a => a.Number == number.Value && a.IsActive, cancellationToken))
        {
            return AccountingErrors.MappingAccountInvalid;
        }

        var actor = _user.ToAuditActor();
        var now = _clock.GetUtcNow();
        var mapping = await _db.Set<AccountMapping>().FirstOrDefaultAsync(m => m.Key == key, cancellationToken);
        if (mapping is null)
        {
            var created = AccountMapping.Create(key, number.Value, actor, now);
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

    /// <summary>The account of every operation type for the generation of the journals (mapped and active accounts only).</summary>
    internal static async Task<AccountResolver> ResolverAsync(IAppDbContext db, CancellationToken cancellationToken)
    {
        var active = (await db.Set<LedgerAccount>().AsNoTracking().Where(a => a.IsActive).Select(a => a.Number).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var mapped = await db.Set<AccountMapping>().AsNoTracking().ToDictionaryAsync(m => m.Key, m => m.AccountNumber, StringComparer.Ordinal, cancellationToken);
        return new AccountResolver(mapped.Where(m => active.Contains(m.Value)).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal));
    }

    private async Task<bool> IsMappedAsync(string number, CancellationToken cancellationToken) =>
        await _db.Set<AccountMapping>().AnyAsync(m => m.AccountNumber == number, cancellationToken);

    private async Task<Result> ChangeAccountAsync(Guid accountId, Func<LedgerAccount, AuditActor, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        if (!CanManage)
        {
            return AccountingErrors.Forbidden;
        }

        var account = await _db.Set<LedgerAccount>().FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
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
            // A concurrent creation won the unique index (account number, mapping key).
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
/// Seeds the default chart of accounts and the mapping of every operation type in every environment (reference data):
/// idempotent, it only adds the accounts and the keys that are missing (a key added by a later version of the catalogue
/// gets its default account), never changes what the accountant changed.
/// </summary>
internal sealed class AccountingChartSeeder(IAppDbContext db, TimeProvider clock) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Reference;

    public int Order => 800;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var actor = AuditActor.System("system:seed");
        var now = clock.GetUtcNow();
        var numbers = (await db.Set<LedgerAccount>().Select(a => a.Number).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var account in AccountingMappingCatalog.DefaultChart.Where(a => !numbers.Contains(a.Number)))
        {
            db.Set<LedgerAccount>().Add(LedgerAccount.Create(account.Number, account.Label, isDefault: true, actor, now).Value);
        }

        var keys = (await db.Set<AccountMapping>().Select(m => m.Key).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var definition in AccountingMappingCatalog.All.Where(d => !keys.Contains(d.Key)))
        {
            db.Set<AccountMapping>().Add(AccountMapping.Create(definition.Key, definition.DefaultAccount, actor, now).Value);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
