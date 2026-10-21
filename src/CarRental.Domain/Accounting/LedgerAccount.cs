using System.Text.RegularExpressions;
using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

/// <summary>
/// An account of the chart of accounts (plan de comptes, SPEC §8 "plan de comptes paramétrable, système comptable
/// tunisien"): a number of digits, its label, active or not. The chart is seeded with a default inspired by the Tunisian
/// accounting system whose accounts stay marked "to be validated by the chartered accountant" until someone confirms them
/// (TODO(spec): the accountant validates or replaces them). A number never changes: it is referenced by the mapping and by
/// the frozen journals; another account is created instead.
/// </summary>
public sealed partial class LedgerAccount : AggregateRoot
{
    public const int NumberMinLength = 2;
    public const int NumberMaxLength = 10;
    public const int LabelMaxLength = 120;

    private LedgerAccount()
    {
    }

    private LedgerAccount(Guid id, string number, string label, bool isDefault, DateTimeOffset at, string? by)
        : base(id)
    {
        Number = number;
        Label = label;
        IsActive = true;
        IsDefault = isDefault;
        NeedsValidation = isDefault;
        CreatedAtUtc = at;
        ChangedAtUtc = at;
        ChangedBy = by;
    }

    public string Number { get; private set; } = null!;

    public string Label { get; private set; } = null!;

    public bool IsActive { get; private set; }

    /// <summary>Part of the default chart seeded by the platform.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>A default account not confirmed yet: shown "à valider par l'expert-comptable".</summary>
    public bool NeedsValidation { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset ChangedAtUtc { get; private set; }

    public string? ChangedBy { get; private set; }

    /// <summary>The class of the account: its first digit (1 equity, 4 third parties, 5 treasury, 6 expenses, 7 revenue…).</summary>
    public int Class => Number[0] - '0';

    /// <summary>Checks an account number: digits only, <see cref="NumberMinLength"/> to <see cref="NumberMaxLength"/> of them, not starting with 0.</summary>
    public static Result<string> ValidateNumber(string? number)
    {
        var cleaned = number?.Trim();
        return cleaned is not null && NumberPattern().IsMatch(cleaned) ? cleaned : AccountingErrors.AccountNumberInvalid;
    }

    /// <summary>An account created by the accountant (or by the default chart when <paramref name="isDefault"/>).</summary>
    public static Result<LedgerAccount> Create(string? number, string? label, bool isDefault, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        var validNumber = ValidateNumber(number);
        if (validNumber.IsFailure)
        {
            return validNumber.Error;
        }

        var validLabel = CleanLabel(label);
        if (validLabel.IsFailure)
        {
            return validLabel.Error;
        }

        var account = new LedgerAccount(NewId(), validNumber.Value, validLabel.Value, isDefault, at, by.UserName);
        account.Raise(new LedgerAccountChanged(account.Id, account.Number));
        return account;
    }

    /// <summary>Renames the account; renaming confirms a default account (the accountant has looked at it).</summary>
    public Result Rename(string? label, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        var validLabel = CleanLabel(label);
        if (validLabel.IsFailure)
        {
            return validLabel.Error;
        }

        Label = validLabel.Value;
        NeedsValidation = false;
        Touch(by, at);
        return Result.Success();
    }

    /// <summary>Confirms a default account as validated by the chartered accountant.</summary>
    public Result Confirm(AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        NeedsValidation = false;
        Touch(by, at);
        return Result.Success();
    }

    /// <summary>Activates or deactivates the account (the caller checks that no operation type is mapped to it first).</summary>
    public Result SetActive(bool active, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        IsActive = active;
        Touch(by, at);
        return Result.Success();
    }

    private static Result<string> CleanLabel(string? label)
    {
        var cleaned = label?.Trim();
        return string.IsNullOrEmpty(cleaned) || cleaned.Length > LabelMaxLength ? AccountingErrors.AccountLabelInvalid : cleaned;
    }

    private void Touch(AuditActor by, DateTimeOffset at)
    {
        ChangedAtUtc = at;
        ChangedBy = by.UserName;
        Raise(new LedgerAccountChanged(Id, Number));
    }

    [GeneratedRegex("^[1-9][0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
