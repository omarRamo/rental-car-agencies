using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Handover;

/// <summary>
/// The charge rules of the agency (settings <c>bookings.*</c>, ADR-0042): mileage included per day (0 = unlimited) and
/// price of an extra kilometre, refuel fee per missing eighth (full-to-full), cleaning fee — all VAT included — and the
/// VAT rate that splits them.
/// </summary>
public sealed record ReturnChargePolicy(
    int IncludedKmPerDay,
    Money ExtraKmPrice,
    Money RefuelFeePerEighth,
    Money CleaningFee,
    decimal VatRate);

/// <summary>
/// The late days of a return and their price at the current plan (delta method, ADR-0042):
/// <c>quote(pick-up → actual return, now) − quote(pick-up → planned return, now)</c>, VAT included; the daily
/// contribution of those days (excluding VAT, per day) is charged apart.
/// </summary>
public sealed record LateReturnPrice(int Days, Money PriceDeltaInclVat, Money ContributionPerDayExclVat);

/// <summary>
/// What the return charges are proposed from: the readings of the departure (unknown without a departure inspection)
/// and of the return, the charged days (quote days plus late days), whether the vehicle needs cleaning, the late return.
/// </summary>
public sealed record ReturnFacts(
    int? DepartureMileage,
    int ReturnMileage,
    int? DepartureFuelEighths,
    int ReturnFuelEighths,
    int ChargedDays,
    bool NeedsCleaning,
    LateReturnPrice? Late);

/// <summary>A charge entered or edited by an agent: kind, quantity, unit price VAT included, description, reason, damage.</summary>
public sealed record ReturnChargeInput(
    ReturnChargeKind Kind,
    int Quantity,
    decimal UnitPrice,
    string? Description = null,
    string? Reason = null,
    Guid? DamageMarkId = null);

/// <summary>
/// A computed or entered return charge, before it belongs to an inspection: quantity, unit price VAT included, the split
/// excluding VAT / VAT at the VAT rate, the arguments of its label (invariant strings), the agent's description and
/// reason, the damage it repairs, and what the platform proposed (<see langword="null"/> for a line added by an agent).
/// </summary>
public sealed record ReturnChargeLine(
    ReturnChargeKind Kind,
    int Quantity,
    Money UnitPrice,
    Money ExclVat,
    Money Vat,
    Money InclVat,
    decimal VatRate,
    IReadOnlyList<string> LabelArguments,
    string? Description,
    string? Reason,
    Guid? DamageMarkId,
    Money? ProposedInclVat)
{
    /// <summary>A line priced VAT included: <c>TTC = quantity × unit price</c>, <c>HT = round(TTC / (1 + rate))</c>, <c>VAT = TTC − HT</c>.</summary>
    public static ReturnChargeLine FromInclVat(
        ReturnChargeKind kind,
        int quantity,
        Money unitPrice,
        decimal vatRate,
        IReadOnlyList<string> labelArguments,
        string? description = null,
        string? reason = null,
        Guid? damageMarkId = null,
        Money? proposedInclVat = null)
    {
        var inclVat = unitPrice * quantity;
        var exclVat = Money.Tnd(inclVat.Amount / (1m + vatRate));
        return new ReturnChargeLine(kind, quantity, unitPrice, exclVat, inclVat - exclVat, inclVat, vatRate, labelArguments, description, reason, damageMarkId, proposedInclVat);
    }

    /// <summary>
    /// The daily contribution of <paramref name="days"/> days, priced excluding VAT like the pricing engine:
    /// <c>HT = days × contribution</c>, <c>VAT = round(HT × rate)</c>, <c>TTC = HT + VAT</c>.
    /// </summary>
    public static ReturnChargeLine Contribution(int days, Money perDayExclVat, decimal vatRate, Money? proposedInclVat = null)
    {
        var exclVat = perDayExclVat * days;
        var vat = exclVat * vatRate;
        var unitPrice = perDayExclVat * (1m + vatRate);
        return new ReturnChargeLine(
            ReturnChargeKind.LateContribution,
            days,
            unitPrice,
            exclVat,
            vat,
            exclVat + vat,
            vatRate,
            [days.ToString(CultureInfo.InvariantCulture), Amount(perDayExclVat)],
            null,
            null,
            null,
            proposedInclVat);
    }

    internal static string Amount(Money amount) => amount.Amount.ToString("F3", CultureInfo.InvariantCulture);
}

/// <summary>
/// The return charges rules (ADR-0042), pure: the proposal from the facts and the policy, then the final set once an
/// agent edited it — changes of a proposed amount need a reason, other charges a description and a reason, damage
/// repairs refer to new damages and are capped together at the franchise by a computed negative line.
/// </summary>
public static class ReturnCharges
{
    public const int MaxLines = 30;

    /// <summary>Upper bound of a unit price (typing errors), like the payments.</summary>
    public const decimal MaxUnitPrice = 1_000_000m;

    /// <summary>Kinds the platform proposes from the facts (an agent changes their amount with a reason).</summary>
    public static IReadOnlyList<ReturnChargeKind> ProposedKinds { get; } =
    [
        ReturnChargeKind.ExtraMileage,
        ReturnChargeKind.Fuel,
        ReturnChargeKind.LateReturn,
        ReturnChargeKind.LateContribution,
        ReturnChargeKind.Cleaning,
    ];

    /// <summary>
    /// The charges proposed for a return: extra kilometres beyond <c>included per day × charged days</c> (when a limit
    /// applies and the departure mileage is known), missing eighths of fuel compared with the departure, late days at the
    /// current plan and their daily contribution, cleaning when needed.
    /// </summary>
    public static IReadOnlyList<ReturnChargeLine> Propose(ReturnFacts facts, ReturnChargePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(policy);
        var lines = new List<ReturnChargeLine>();
        var rate = policy.VatRate;

        if (policy.IncludedKmPerDay > 0 && facts.DepartureMileage is { } departureMileage)
        {
            var driven = Math.Max(0, facts.ReturnMileage - departureMileage);
            var included = policy.IncludedKmPerDay * Math.Max(1, facts.ChargedDays);
            var extra = driven - included;
            if (extra > 0 && policy.ExtraKmPrice.IsPositive)
            {
                lines.Add(Proposed(ReturnChargeLine.FromInclVat(ReturnChargeKind.ExtraMileage, extra, policy.ExtraKmPrice, rate, [Int(driven), Int(included)])));
            }
        }

        if (facts.DepartureFuelEighths is { } departureFuel && facts.ReturnFuelEighths < departureFuel && policy.RefuelFeePerEighth.IsPositive)
        {
            var missing = departureFuel - facts.ReturnFuelEighths;
            lines.Add(Proposed(ReturnChargeLine.FromInclVat(ReturnChargeKind.Fuel, missing, policy.RefuelFeePerEighth, rate, [Int(departureFuel), Int(facts.ReturnFuelEighths)])));
        }

        if (facts.Late is { Days: > 0 } late)
        {
            var contribution = late.ContributionPerDayExclVat.IsPositive ? ReturnChargeLine.Contribution(late.Days, late.ContributionPerDayExclVat, rate) : null;
            var rental = late.PriceDeltaInclVat - (contribution?.InclVat ?? Money.Zero);
            if (rental.IsPositive)
            {
                lines.Add(Proposed(ReturnChargeLine.FromInclVat(ReturnChargeKind.LateReturn, 1, rental, rate, [Int(late.Days)])));
            }

            if (contribution is not null)
            {
                lines.Add(Proposed(contribution));
            }
        }

        if (facts.NeedsCleaning && policy.CleaningFee.IsPositive)
        {
            lines.Add(Proposed(ReturnChargeLine.FromInclVat(ReturnChargeKind.Cleaning, 1, policy.CleaningFee, rate, [])));
        }

        return lines;
    }

    /// <summary>
    /// The final charges of a return from the <paramref name="proposal"/> and what the agent <paramref name="entered"/>.
    /// A proposed charge left out is kept as proposed; its quantity is a fact (only its unit price changes, with a
    /// reason, zero to waive it). Damage repairs refer to one of <paramref name="newDamageIds"/> (or carry a
    /// description) and are capped together at <paramref name="franchise"/> by a <see cref="ReturnChargeKind.FranchiseCap"/>
    /// line; other charges need a description and a reason.
    /// </summary>
    public static Result<IReadOnlyList<ReturnChargeLine>> Finalize(
        IReadOnlyList<ReturnChargeLine> proposal,
        IReadOnlyList<ReturnChargeInput> entered,
        Money franchise,
        decimal vatRate,
        IReadOnlyCollection<Guid> newDamageIds)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(entered);
        ArgumentNullException.ThrowIfNull(newDamageIds);
        if (franchise.IsNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(franchise), "A franchise is never negative.");
        }

        if (entered.Count > MaxLines)
        {
            return HandoverErrors.TooManyCharges;
        }

        var lines = new List<ReturnChargeLine>();
        var used = new HashSet<ReturnChargeKind>();
        foreach (var input in entered)
        {
            ArgumentNullException.ThrowIfNull(input, nameof(entered));
            var line = Line(input, proposal, vatRate, newDamageIds, used);
            if (line.IsFailure)
            {
                return line.Error;
            }

            lines.Add(line.Value);
        }

        // A proposed charge the agent did not send stays as proposed (never dropped without a reason).
        lines.AddRange(proposal.Where(p => !used.Contains(p.Kind)));

        var ordered = lines
            .OrderBy(l => ProposedOrder(l.Kind))
            .ThenBy(l => l.Kind)
            .ToList();
        var damages = ordered.Where(l => l.Kind == ReturnChargeKind.Damage).Select(l => l.InclVat).Sum();
        if (damages > franchise)
        {
            ordered.Add(ReturnChargeLine.FromInclVat(ReturnChargeKind.FranchiseCap, 1, (damages - franchise).Negate(), vatRate, [ReturnChargeLine.Amount(franchise)]));
        }

        return ordered.Count > MaxLines ? HandoverErrors.TooManyCharges : ordered;
    }

    /// <summary>Total VAT included of charges.</summary>
    public static Money TotalOf(IEnumerable<ReturnChargeLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Select(l => l.InclVat).Sum();
    }

    private static Result<ReturnChargeLine> Line(
        ReturnChargeInput input,
        IReadOnlyList<ReturnChargeLine> proposal,
        decimal vatRate,
        IReadOnlyCollection<Guid> newDamageIds,
        HashSet<ReturnChargeKind> used)
    {
        if (!Enum.IsDefined(input.Kind)
            || input.Quantity < 0
            || input.UnitPrice is < 0m or > MaxUnitPrice
            || Money.FromExact(input.UnitPrice, Currency.Tnd).IsFailure)
        {
            return HandoverErrors.ChargeInvalid;
        }

        if (input.Kind == ReturnChargeKind.FranchiseCap)
        {
            return HandoverErrors.ChargeNotEditable;
        }

        var description = Clean(input.Description);
        var reason = Clean(input.Reason);
        if (description is { Length: > ReturnCharge.DescriptionMaxLength })
        {
            return HandoverErrors.ChargeDescriptionRequired;
        }

        if (reason is { Length: > Inspection.ReasonMaxLength })
        {
            return HandoverErrors.ReasonRequired;
        }

        var unitPrice = Money.Tnd(input.UnitPrice);
        var proposed = proposal.FirstOrDefault(p => p.Kind == input.Kind && ProposedKinds.Contains(p.Kind));
        if (proposed is not null)
        {
            if (!used.Add(input.Kind))
            {
                // One line per proposed kind.
                return HandoverErrors.ChargeInvalid;
            }

            // The quantity is a fact of the inspection (kilometres, eighths, days): only the price changes.
            var edited = proposed.Kind == ReturnChargeKind.LateContribution
                ? ScaledContribution(proposed, unitPrice, vatRate)
                : ReturnChargeLine.FromInclVat(proposed.Kind, proposed.Quantity, unitPrice, vatRate, proposed.LabelArguments, description, reason, null, proposed.ProposedInclVat);
            if (edited.InclVat != proposed.InclVat && !IsReason(reason))
            {
                return HandoverErrors.ChargeReasonRequired;
            }

            return edited with { Description = description, Reason = reason };
        }

        if (input.Quantity < 1)
        {
            return HandoverErrors.ChargeInvalid;
        }

        switch (input.Kind)
        {
            case ReturnChargeKind.Damage:
                if (input.DamageMarkId is { } markId)
                {
                    if (!newDamageIds.Contains(markId))
                    {
                        return HandoverErrors.ChargeDamageInvalid;
                    }
                }
                else if (description is null)
                {
                    return HandoverErrors.ChargeDescriptionRequired;
                }

                return ReturnChargeLine.FromInclVat(ReturnChargeKind.Damage, input.Quantity, unitPrice, vatRate, [], description, reason, input.DamageMarkId);

            case ReturnChargeKind.Other:
                if (description is null)
                {
                    return HandoverErrors.ChargeDescriptionRequired;
                }

                return IsReason(reason)
                    ? ReturnChargeLine.FromInclVat(ReturnChargeKind.Other, input.Quantity, unitPrice, vatRate, [], description, reason)
                    : HandoverErrors.ChargeReasonRequired;

            case ReturnChargeKind.LateContribution:
                // The contribution follows the late days, which only the platform measures.
                return HandoverErrors.ChargeNotEditable;

            default:
                // A kind the facts did not propose (e.g. fuel while the gauge read full): an agent's decision, with a reason.
                if (!used.Add(input.Kind))
                {
                    return HandoverErrors.ChargeInvalid;
                }

                return IsReason(reason)
                    ? ReturnChargeLine.FromInclVat(input.Kind, input.Quantity, unitPrice, vatRate, [Int(input.Quantity)], description, reason)
                    : HandoverErrors.ChargeReasonRequired;
        }
    }

    /// <summary>The contribution with another unit price VAT included (zero waives it): the split stays excluding VAT based.</summary>
    private static ReturnChargeLine ScaledContribution(ReturnChargeLine proposed, Money unitPrice, decimal vatRate)
    {
        if (unitPrice == proposed.UnitPrice)
        {
            return proposed;
        }

        var inclVat = unitPrice * proposed.Quantity;
        var exclVat = Money.Tnd(inclVat.Amount / (1m + vatRate));
        return proposed with { UnitPrice = unitPrice, ExclVat = exclVat, Vat = inclVat - exclVat, InclVat = inclVat };
    }

    private static ReturnChargeLine Proposed(ReturnChargeLine line) => line with { ProposedInclVat = line.InclVat };

    private static int ProposedOrder(ReturnChargeKind kind) => kind switch
    {
        ReturnChargeKind.ExtraMileage => 1,
        ReturnChargeKind.Fuel => 2,
        ReturnChargeKind.LateReturn => 3,
        ReturnChargeKind.LateContribution => 4,
        ReturnChargeKind.Cleaning => 5,
        ReturnChargeKind.Damage => 6,
        ReturnChargeKind.Other => 7,
        _ => 8,
    };

    private static bool IsReason(string? reason) => reason is { Length: >= Inspection.ReasonMinLength and <= Inspection.ReasonMaxLength };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A return charge of an inspection (ADR-0042): kept with who recorded it and when.</summary>
public sealed class ReturnCharge : Entity
{
    public const int DescriptionMaxLength = 200;

    private ReturnCharge()
    {
    }

    internal ReturnCharge(Guid inspectionId, int position, ReturnChargeLine line, BookingActor actor, DateTimeOffset at)
        : base(NewId())
    {
        InspectionId = inspectionId;
        Position = position;
        Kind = line.Kind;
        Quantity = line.Quantity;
        UnitPrice = line.UnitPrice;
        ExclVat = line.ExclVat;
        Vat = line.Vat;
        InclVat = line.InclVat;
        VatRate = line.VatRate;
        LabelArguments = [.. line.LabelArguments];
        Description = line.Description;
        Reason = line.Reason;
        DamageMarkId = line.DamageMarkId;
        ProposedInclVat = line.ProposedInclVat;
        RecordedByUserId = actor.UserId;
        RecordedBy = actor.Name;
        RecordedAtUtc = at.ToUniversalTime();
    }

    public Guid InspectionId { get; private set; }

    /// <summary>Order of the line, from 1.</summary>
    public int Position { get; private set; }

    public ReturnChargeKind Kind { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>Unit price, VAT included.</summary>
    public Money UnitPrice { get; private set; }

    public Money ExclVat { get; private set; }

    public Money Vat { get; private set; }

    public Money InclVat { get; private set; }

    public decimal VatRate { get; private set; }

    /// <summary>Invariant arguments of the label of the kind (kilometres, eighths, days, amounts).</summary>
    public IReadOnlyList<string> LabelArguments { get; private set; } = [];

    public string? Description { get; private set; }

    /// <summary>Why the agent changed, waived or added the charge.</summary>
    public string? Reason { get; private set; }

    /// <summary>The new damage a repair charge refers to.</summary>
    public Guid? DamageMarkId { get; private set; }

    /// <summary>What the platform proposed, VAT included; <see langword="null"/> for a line added by an agent.</summary>
    public Money? ProposedInclVat { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public string RecordedBy { get; private set; } = null!;

    public DateTimeOffset RecordedAtUtc { get; private set; }

    /// <summary>A proposed charge set to zero by an agent.</summary>
    public bool IsWaived => ProposedInclVat is { IsPositive: true } && InclVat.IsZero;

    public ReturnChargeLine ToLine() =>
        new(Kind, Quantity, UnitPrice, ExclVat, Vat, InclVat, VatRate, LabelArguments, Description, Reason, DamageMarkId, ProposedInclVat);
}
