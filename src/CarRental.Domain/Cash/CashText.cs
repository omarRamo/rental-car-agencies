using CarRental.Domain.Common;

namespace CarRental.Domain.Cash;

/// <summary>Trimming and validation of the free texts and amounts of the Cash module.</summary>
internal static class CashText
{
    /// <summary>Trimmed text, or <see langword="null"/> for a blank value.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A name cut to the stored length of an actor name.</summary>
    public static string? Name(string? value)
    {
        var name = Clean(value);
        return name is { Length: > AuditActor.UserNameMaxLength } ? name[..AuditActor.UserNameMaxLength] : name;
    }

    public static string? ActorName(AuditActor actor) => Name(actor.UserName);

    /// <summary>A positive amount in TND up to <see cref="CashSession.MaxAmount"/> (the struct is already rounded to the millime).</summary>
    public static bool IsPositiveAmount(Money amount) =>
        amount.Currency == Currency.Tnd && amount.IsPositive && amount.Amount <= CashSession.MaxAmount;

    /// <summary>A zero or positive amount in TND up to <see cref="CashSession.MaxAmount"/>.</summary>
    public static bool IsCountableAmount(Money amount) =>
        amount.Currency == Currency.Tnd && !amount.IsNegative && amount.Amount <= CashSession.MaxAmount;

    /// <summary>A reason of <see cref="CashMovement.ReasonMinLength"/> to <see cref="CashMovement.ReasonMaxLength"/> characters.</summary>
    public static Result<string> Reason(string? reason)
    {
        var cleaned = Clean(reason);
        if (cleaned is null || cleaned.Length < CashMovement.ReasonMinLength)
        {
            return CashErrors.ReasonRequired;
        }

        return cleaned.Length > CashMovement.ReasonMaxLength ? CashErrors.ReasonTooLong : cleaned;
    }
}
