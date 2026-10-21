using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Contracts;

/// <summary>
/// What the departure and the return leave for the other modules (Billing, phase 3; Reporting): the return charges of a
/// file, its rental contracts and its balance (ADR-0041, ADR-0042).
/// </summary>
/// <remarks>No permission check here (like <see cref="IBookingsQueries"/>): the caller enforces its own.</remarks>
public interface IHandoverQueries
{
    /// <summary>
    /// The charges of the return of a file, in their order, with their totals; no line before the return. They are not
    /// a quote version: they add to the balance and become lines of the final invoice (the franchise cap included).
    /// </summary>
    Task<ReturnChargesInfo> GetReturnChargesAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>The rental contracts of a file, the first version first.</summary>
    Task<IReadOnlyList<RentalContractInfo>> GetContractsAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The balance of a file: total of the current quote + return charges − net paid − deposits retained (a retention
    /// settles charges; it is never revenue by itself, invariant 8), and the deposits still held; <see langword="null"/>
    /// for an unknown file.
    /// </summary>
    Task<BookingBalanceInfo?> GetBalanceAsync(Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>Kind of a return charge (same values as the domain enum).</summary>
public enum ReturnChargeKindInfo
{
    ExtraMileage = 1,
    Fuel = 2,
    LateReturn = 3,
    LateContribution = 4,
    Cleaning = 5,
    Damage = 6,
    FranchiseCap = 7,
    Other = 99,
}

/// <summary>Language of a rental contract (same values as the domain enum).</summary>
public enum ContractLanguageInfo
{
    French = 1,
    English = 2,
}

/// <summary>
/// A return charge. Its label is the resource <see cref="LabelKey"/> (<c>ReturnCharge.Label.{Kind}</c>) formatted with
/// <see cref="LabelArguments"/> (invariant strings), followed by the agent's <see cref="Description"/>; the unit price is
/// VAT included; <see cref="ProposedInclVat"/> is what the platform proposed (<see langword="null"/> for an agent's line).
/// </summary>
public sealed record ReturnChargeInfo(
    int Position,
    ReturnChargeKindInfo Kind,
    string LabelKey,
    IReadOnlyList<string> LabelArguments,
    string? Description,
    string? Reason,
    int Quantity,
    Money UnitPrice,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal VatRate,
    Guid? DamageMarkId,
    Money? ProposedInclVat,
    DateTimeOffset RecordedAtUtc,
    string RecordedBy)
{
    /// <summary>The resource key of the label of a kind of charge.</summary>
    public static string LabelKeyOf(ReturnChargeKindInfo kind) => $"ReturnCharge.Label.{kind}";
}

/// <summary>The return charges of a file and their totals; <see cref="Franchise"/> is the cap of the damage charges.</summary>
public sealed record ReturnChargesInfo(
    Guid BookingId,
    IReadOnlyList<ReturnChargeInfo> Lines,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money? Franchise,
    bool FranchiseReduced);

/// <summary>A rental contract (PDF in the private container <c>contracts</c>).</summary>
public sealed record RentalContractInfo(
    Guid Id,
    Guid BookingId,
    string Number,
    int Version,
    ContractLanguageInfo Language,
    string FileName,
    DateTimeOffset GeneratedAtUtc,
    string GeneratedBy,
    string? SentTo,
    DateTimeOffset? SentAtUtc);

/// <summary>
/// The money situation of a file: <see cref="Balance"/> = <see cref="QuoteTotalInclVat"/> + <see cref="ChargesInclVat"/>
/// − <see cref="NetPaid"/> − <see cref="DepositsRetained"/> (positive: due by the customer; negative: to pay back).
/// </summary>
public sealed record BookingBalanceInfo(
    Guid BookingId,
    Money QuoteTotalInclVat,
    Money ChargesInclVat,
    Money NetPaid,
    Money Pending,
    Money DepositsRetained,
    Money Balance,
    Money DepositRequired,
    Money DepositsHeld,
    int DepositsHeldCount);
