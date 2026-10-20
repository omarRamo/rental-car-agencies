using CarRental.Domain.Common;

namespace CarRental.Domain.Payments;

/// <summary>
/// A security deposit (caution) held for a booking (aggregate): a card imprint, cash or a cheque. It is a liability to
/// the customer, never revenue (invariant 8): while <see cref="DepositStatus.Held"/>, the whole amount is owed back.
/// At the return it is settled exactly once: released in full, or retained in part or in full with a mandatory
/// justification (and an optional reference to a supporting document); a partial retention releases the rest.
/// </summary>
/// <remarks>
/// A retained amount stops being owed to the customer; it is not a payment of the rental and does not count in the paid
/// amounts. Phase 3 bills the damages and applies the retained amount to that invoice.
/// </remarks>
public sealed class DepositHold : AggregateRoot
{
    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const int JustificationMinLength = 3;
    public const int JustificationMaxLength = 1000;
    public const int AttachmentReferenceMaxLength = 500;

    /// <summary>Upper bound of one deposit, in TND (guard against typing errors).</summary>
    public const decimal MaxAmount = Payment.MaxAmount;

    private DepositHold()
    {
    }

    private DepositHold(Guid id, DepositDetails details, string? reference, string? notes, AuditActor by, DateTimeOffset at)
        : base(id)
    {
        AgencyId = details.AgencyId;
        BookingId = details.BookingId;
        Mode = details.Mode;
        Amount = details.Amount;
        Status = DepositStatus.Held;
        ExternalReference = reference;
        Notes = notes;
        TakenInCashSessionId = details.CashSessionId;
        TakenAtUtc = at;
        TakenByUserId = by.UserId;
        TakenByName = PaymentText.ActorName(by);
    }

    public Guid AgencyId { get; private set; }

    public Guid BookingId { get; private set; }

    public DepositMode Mode { get; private set; }

    public DepositStatus Status { get; private set; }

    public Money Amount { get; private set; }

    /// <summary>Part kept by the agency (zero unless retained).</summary>
    public Money RetainedAmount { get; private set; }

    /// <summary>Authorization number of the card imprint or cheque number.</summary>
    public string? ExternalReference { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Why the agency kept money (damages, fuel, late return…): mandatory as soon as something is retained.</summary>
    public string? RetentionJustification { get; private set; }

    /// <summary>Optional reference to a supporting document (photo, repair quote, invoice number).</summary>
    public string? AttachmentReference { get; private set; }

    /// <summary>Cash deposit: the session in which the cash was received.</summary>
    public Guid? TakenInCashSessionId { get; private set; }

    /// <summary>Cash deposit: the session in which it was returned or retained.</summary>
    public Guid? SettledInCashSessionId { get; private set; }

    public DateTimeOffset TakenAtUtc { get; private set; }

    public Guid? TakenByUserId { get; private set; }

    public string? TakenByName { get; private set; }

    public DateTimeOffset? SettledAtUtc { get; private set; }

    public Guid? SettledByUserId { get; private set; }

    public string? SettledByName { get; private set; }

    public bool IsHeld => Status == DepositStatus.Held;

    /// <summary>What is still owed back to the customer: the whole amount while held, nothing once settled.</summary>
    public Money OpenLiability => IsHeld ? Amount : Money.Zero;

    /// <summary>What was given back to the customer once settled.</summary>
    public Money ReturnedAmount => IsHeld ? Money.Zero : Amount - RetainedAmount;

    /// <summary>
    /// Takes a deposit for a booking. A card imprint needs its authorization number and a cheque its number; cash needs
    /// the open cash session of the agent (the cash is kept apart from the revenue cash).
    /// </summary>
    public static Result<DepositHold> Take(DepositDetails details, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(by);
        if (details.AgencyId == Guid.Empty)
        {
            return PaymentsErrors.AgencyRequired;
        }

        if (details.BookingId == Guid.Empty)
        {
            return PaymentsErrors.BookingRequired;
        }

        if (!Enum.IsDefined(details.Mode))
        {
            return PaymentsErrors.DepositModeInvalid;
        }

        if (!PaymentText.IsValidAmount(details.Amount, MaxAmount))
        {
            return PaymentsErrors.AmountInvalid;
        }

        var reference = PaymentText.Clean(details.ExternalReference);
        if (reference is { Length: > ReferenceMaxLength })
        {
            return PaymentsErrors.ReferenceTooLong;
        }

        if (reference is null && details.Mode is DepositMode.CardImprint or DepositMode.Cheque)
        {
            return PaymentsErrors.DepositReferenceRequired;
        }

        var notes = PaymentText.Clean(details.Notes);
        if (notes is { Length: > NotesMaxLength })
        {
            return PaymentsErrors.NotesTooLong;
        }

        var session = ValidateCashSession(details.Mode, details.CashSessionId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var deposit = new DepositHold(NewId(), details, reference, notes, by, at);
        deposit.Raise(new DepositTaken(deposit.Id, deposit.BookingId, deposit.AgencyId, deposit.Mode));
        return deposit;
    }

    /// <summary>Returns the whole deposit to the customer (cash: from the open cash session of the agent).</summary>
    public Result Release(Guid? cashSessionId, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (!IsHeld)
        {
            return PaymentsErrors.DepositAlreadySettled;
        }

        var session = ValidateCashSession(Mode, cashSessionId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        Settle(DepositStatus.Released, Money.Zero, justification: null, attachment: null, cashSessionId, by, at);
        Raise(new DepositReleased(Id, BookingId));
        return Result.Success();
    }

    /// <summary>
    /// Keeps <paramref name="amount"/> (at most the deposit) with a justification; the rest is returned to the customer.
    /// The whole amount retained gives <see cref="DepositStatus.Retained"/>, less gives <see cref="DepositStatus.PartiallyRetained"/>.
    /// </summary>
    public Result Retain(Money amount, string? justification, string? attachmentReference, Guid? cashSessionId, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (!IsHeld)
        {
            return PaymentsErrors.DepositAlreadySettled;
        }

        if (amount.Currency != Currency.Tnd || !amount.IsPositive || amount > Amount)
        {
            return PaymentsErrors.DepositRetainedAmountInvalid;
        }

        var text = PaymentText.Clean(justification);
        if (text is null || text.Length < JustificationMinLength)
        {
            return PaymentsErrors.DepositJustificationRequired;
        }

        if (text.Length > JustificationMaxLength)
        {
            return PaymentsErrors.DepositJustificationTooLong;
        }

        var attachment = PaymentText.Clean(attachmentReference);
        if (attachment is { Length: > AttachmentReferenceMaxLength })
        {
            return PaymentsErrors.DepositAttachmentTooLong;
        }

        var session = ValidateCashSession(Mode, cashSessionId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var status = amount == Amount ? DepositStatus.Retained : DepositStatus.PartiallyRetained;
        Settle(status, amount, text, attachment, cashSessionId, by, at);
        Raise(new DepositRetained(Id, BookingId, status));
        return Result.Success();
    }

    /// <summary>A cash deposit always goes through a cash session; a card imprint or a cheque never does (passing one is a bug).</summary>
    private static Result ValidateCashSession(DepositMode mode, Guid? cashSessionId)
    {
        if (mode == DepositMode.Cash)
        {
            return cashSessionId is { } id && id != Guid.Empty ? Result.Success() : PaymentsErrors.CashSessionRequired;
        }

        return cashSessionId is null
            ? Result.Success()
            : throw new ArgumentException("Only a cash deposit is recorded in a cash session.", nameof(cashSessionId));
    }

    private void Settle(DepositStatus status, Money retained, string? justification, string? attachment, Guid? cashSessionId, AuditActor by, DateTimeOffset at)
    {
        Status = status;
        RetainedAmount = retained;
        RetentionJustification = justification;
        AttachmentReference = attachment;
        SettledInCashSessionId = cashSessionId;
        SettledAtUtc = at;
        SettledByUserId = by.UserId;
        SettledByName = PaymentText.ActorName(by);
    }
}

/// <summary>
/// What an agent enters to take a deposit. <c>ExternalReference</c> is the authorization number of the card imprint or
/// the cheque number (mandatory for both); <c>CashSessionId</c> is the open cash session of the agent, required for a
/// cash deposit and absent otherwise.
/// </summary>
public sealed record DepositDetails(
    Guid AgencyId,
    Guid BookingId,
    DepositMode Mode,
    Money Amount,
    string? ExternalReference = null,
    string? Notes = null,
    Guid? CashSessionId = null);
