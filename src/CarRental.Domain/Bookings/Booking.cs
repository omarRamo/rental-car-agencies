using System.Globalization;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings;

/// <summary>
/// The rental file (dossier de location), aggregate of the Bookings module: born in one of the five channels, it follows
/// the status machine of docs/conception/phase-1-exploitation.md §2 (<see cref="BookingStateMachine"/>). Every command
/// checks its invariants, appends a <see cref="BookingEvent"/> to the timeline and raises a domain event (outbox).
/// </summary>
/// <remarks>
/// <para>The aggregate never checks availability itself: the application runs the availability guard under the lock of
/// the category before every command that consumes capacity, then calls the aggregate in the same transaction.</para>
/// <para>Every command increments <see cref="LastEventSequence"/>, so the row (and its <c>rowversion</c>) changes with
/// each timeline row: two concurrent commands on the same file cannot both succeed.</para>
/// <para>The price is a frozen <c>Quote</c> (its own aggregate); the file references its current version and keeps its
/// totals for lists and payments. Instants are UTC; calendar rules use Africa/Tunis.</para>
/// </remarks>
public sealed class Booking : AggregateRoot
{
    public const int ReasonMaxLength = 500;
    public const int ChannelReferenceMaxLength = 64;
    public const int PromoCodeMaxLength = 32;
    public const int NotesMaxLength = 1000;
    public const int ExtrasMaxLength = 400;

    /// <summary>A pick-up may be recorded a little in the past (counter: the customer is at the desk).</summary>
    public static readonly TimeSpan PickupPastTolerance = TimeSpan.FromMinutes(30);

    private readonly List<BookingEvent> _events = [];

    private Booking()
    {
    }

    private Booking(Guid id, BookingDetails details, string number, BookingStatus status, BookingQuoteReference quote, DateTimeOffset at)
        : base(id)
    {
        AgencyId = details.AgencyId;
        Number = number;
        Channel = details.Channel;
        ChannelReference = Clean(details.ChannelReference);
        Attribution = details.Attribution ?? BookingAttribution.None;
        Status = status;
        CategoryId = details.CategoryId;
        GuaranteedVehicleId = details.GuaranteedVehicleId;
        AssignedVehicleId = details.GuaranteedVehicleId;
        PickupLocationId = details.PickupLocationId;
        ReturnLocationId = details.ReturnLocationId;
        PickupAt = details.PickupAt.ToUniversalTime();
        ReturnAt = details.ReturnAt.ToUniversalTime();
        ApplyParties(details.Parties);
        ExtraSelection = ExtraChoice.Format(ExtraChoice.Normalize(details.Extras).Value);
        PromoCode = NormalizePromoCode(details.PromoCode);
        Notes = Clean(details.Notes);
        AttachQuote(quote);
        CreatedAtUtc = at.ToUniversalTime();
        StatusChangedAtUtc = CreatedAtUtc;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Readable number <c>LOC-YYYY-NNNNNN</c>, gapless per Tunis year (ADR-0016).</summary>
    public string Number { get; private set; } = null!;

    public SalesChannel Channel { get; private set; }

    /// <summary>Reference of the booking in its channel (e.g. Booking.com), unique per channel.</summary>
    public string? ChannelReference { get; private set; }

    public BookingAttribution Attribution { get; private set; } = BookingAttribution.None;

    public BookingStatus Status { get; private set; }

    /// <summary>Reason of the last refusal, cancellation or no-show.</summary>
    public string? StatusReason { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>The exact vehicle booked with the "guaranteed model" option; it is also the assigned vehicle.</summary>
    public Guid? GuaranteedVehicleId { get; private set; }

    public Guid? AssignedVehicleId { get; private set; }

    /// <summary>Manager decision letting a non-compliant vehicle carry this file; applies to its vehicle only.</summary>
    public ComplianceWaiver? ComplianceWaiver { get; private set; }

    public Guid PickupLocationId { get; private set; }

    public Guid ReturnLocationId { get; private set; }

    /// <summary>Planned pick-up (UTC).</summary>
    public DateTimeOffset PickupAt { get; private set; }

    /// <summary>Planned return (UTC).</summary>
    public DateTimeOffset ReturnAt { get; private set; }

    public DateTimeOffset? ActualPickupAt { get; private set; }

    public DateTimeOffset? ActualReturnAt { get; private set; }

    /// <summary>End of the hold of a web request (<c>PendingValidation</c>, <c>InformationRequested</c>, or the one that expired).</summary>
    public DateTimeOffset? HoldExpiresAt { get; private set; }

    /// <summary>Expiry of a phone option (<c>OptionHeld</c>, or the option that expired).</summary>
    public DateTimeOffset? OptionExpiresAt { get; private set; }

    /// <summary>Payment deadline of a validated request in the fallback payment mode (<c>AwaitingPayment</c>).</summary>
    public DateTimeOffset? PaymentDueAt { get; private set; }

    public Guid? MainDriverId { get; private set; }

    public Guid? SecondaryDriverId { get; private set; }

    public Guid? PayerCustomerId { get; private set; }

    /// <summary>Selectable extras in their stored form (<see cref="ExtraChoice.Format"/>).</summary>
    public string ExtraSelection { get; private set; } = string.Empty;

    public string? PromoCode { get; private set; }

    /// <summary>Internal notes of the agents (flight number, delivery details…).</summary>
    public string? Notes { get; private set; }

    public Guid CurrentQuoteId { get; private set; }

    public int CurrentQuoteVersion { get; private set; }

    /// <summary>Total TTC of the current quote version.</summary>
    public Money TotalInclVat { get; private set; }

    /// <summary>Deposit of the current quote version (outside the total).</summary>
    public Money Deposit { get; private set; }

    /// <summary>Fee of the cancellation (policy of the agency; zero when free or waived).</summary>
    public Money? CancellationFee { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset StatusChangedAtUtc { get; private set; }

    /// <summary>When the request was first submitted (web, WhatsApp).</summary>
    public DateTimeOffset? SubmittedAtUtc { get; private set; }

    /// <summary>When the request last entered <c>PendingValidation</c>: start of the escalation delay.</summary>
    public DateTimeOffset? PendingSinceUtc { get; private set; }

    /// <summary>When the managers were told that the request was still pending (once per pending period).</summary>
    public DateTimeOffset? EscalatedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>Number of the last timeline row (see remarks of the class).</summary>
    public int LastEventSequence { get; private set; }

    /// <summary>Timeline rows added by this instance (the stored timeline is read by queries, not loaded with the file).</summary>
    public IReadOnlyList<BookingEvent> Events => _events;

    /// <summary>The planned rental <c>[PickupAt, ReturnAt)</c>.</summary>
    public Period Rental => Period.Create(PickupAt, ReturnAt).Value;

    public BookingParties Parties => new(MainDriverId, SecondaryDriverId, PayerCustomerId);

    public IReadOnlyList<ExtraChoice> Extras => ExtraChoice.Parse(ExtraSelection);

    /// <summary>The deadline that makes the file expire in its status, or <see langword="null"/>.</summary>
    public DateTimeOffset? CurrentDeadline => Status switch
    {
        BookingStatus.PendingValidation or BookingStatus.InformationRequested => HoldExpiresAt,
        BookingStatus.OptionHeld => OptionExpiresAt,
        BookingStatus.AwaitingPayment => PaymentDueAt,
        _ => null,
    };

    /// <summary>The compliance waiver applies to the assigned vehicle.</summary>
    public bool IsComplianceWaived => ComplianceWaiver is { } waiver && waiver.VehicleId == AssignedVehicleId;

    /// <summary>The amendable fields as they are now.</summary>
    public BookingChanges CurrentChanges => new(
        CategoryId,
        PickupLocationId,
        ReturnLocationId,
        PickupAt,
        ReturnAt,
        Parties,
        Extras,
        PromoCode,
        GuaranteedVehicleId,
        Notes);

    /// <summary>The vehicle is out and its planned return has passed.</summary>
    public bool IsLateAt(DateTimeOffset now) => Status == BookingStatus.InProgress && ReturnAt < now;

    /// <summary>Whether <paramref name="changes"/> change an input of the price (anything but the payer and the notes).</summary>
    public bool ChangesPrice(BookingChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Diff(changes).PriceInputsChanged;
    }

    /// <summary>
    /// Whether <paramref name="changes"/> move the occupancy of the file (dates, category) or its vehicle (released,
    /// another guaranteed vehicle): such an amendment is re-checked by the availability guard.
    /// </summary>
    public bool ChangesOccupancy(BookingChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var diff = Diff(changes);
        return diff.CategoryChanged || diff.PickupChanged || diff.ReturnChanged || diff.VehicleReleased || diff.GuaranteedVehicleChanged;
    }

    /// <summary>The vehicle the file carries after <paramref name="changes"/> (same rule as the amendment).</summary>
    public Guid? VehicleAfter(BookingChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var diff = Diff(changes);
        if (diff.GuaranteedVehicleChanged && changes.GuaranteedVehicleId is { } guaranteed)
        {
            return guaranteed;
        }

        return diff.CategoryChanged || diff.VehicleReleased ? null : AssignedVehicleId;
    }

    /// <summary>How the file occupies its category now (ADR-0025).</summary>
    public BookingOccupancy ToOccupancy() =>
        new(Id, Status.ToOccupancyKind(), Rental, ActualPickupAt, ActualReturnAt, AssignedVehicleId);

    /// <summary>The occupancy of the file after a change of its rental or vehicle (input of the availability guard).</summary>
    public BookingOccupancy OccupancyAfter(Period rental, Guid? vehicleId)
    {
        ArgumentNullException.ThrowIfNull(rental);
        var kind = Status.ToOccupancyKind();
        return new BookingOccupancy(Id, kind == OccupancyKind.None ? OccupancyKind.Planned : kind, rental, ActualPickupAt, ActualReturnAt, vehicleId);
    }

    /// <summary>A web or WhatsApp request started by the public funnel; drivers and payer may be added at submission.</summary>
    public static Result<Booking> CreateDraft(
        Guid id,
        BookingDetails details,
        DocumentNumber number,
        BookingQuoteReference quote,
        BookingActor actor,
        DateTimeOffset at) =>
        Create(id, details, number, quote, BookingStatus.Draft, optionExpiresAt: null, actor, at);

    /// <summary>A dated option taken by phone (<c>CreateOption</c>): capacity held until <paramref name="optionExpiresAt"/>.</summary>
    public static Result<Booking> CreateOption(
        Guid id,
        BookingDetails details,
        DocumentNumber number,
        BookingQuoteReference quote,
        DateTimeOffset optionExpiresAt,
        BookingActor actor,
        DateTimeOffset at) =>
        Create(id, details, number, quote, BookingStatus.OptionHeld, optionExpiresAt, actor, at);

    /// <summary>A booking confirmed at once (<c>CreateConfirmed</c>): counter, phone, or a channel such as Booking.com.</summary>
    public static Result<Booking> CreateConfirmed(
        Guid id,
        BookingDetails details,
        DocumentNumber number,
        BookingQuoteReference quote,
        BookingActor actor,
        DateTimeOffset at) =>
        Create(id, details, number, quote, BookingStatus.Confirmed, optionExpiresAt: null, actor, at);

    /// <summary>
    /// <c>Draft</c> → <c>PendingValidation</c>: the request holds capacity for <paramref name="holdDuration"/>.
    /// <paramref name="parties"/> are the drivers and payer known at submission, or <see langword="null"/> to keep those
    /// of the draft.
    /// </summary>
    public Result Submit(BookingParties? parties, TimeSpan holdDuration, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(holdDuration, TimeSpan.Zero);
        var allowed = Ensure(BookingCommand.Submit);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var newParties = parties ?? Parties;
        var valid = newParties.Validate(complete: true);
        if (valid.IsFailure)
        {
            return valid;
        }

        if (PickupAt < at - PickupPastTolerance)
        {
            return BookingErrors.PickupInPast;
        }

        ApplyParties(newParties);
        var before = ChangeStatus(BookingStatus.PendingValidation, at);
        HoldExpiresAt = at + holdDuration;
        SubmittedAtUtc ??= at;
        PendingSinceUtc = at;
        EscalatedAtUtc = null;
        Record(BookingEventType.Submitted, before, actor, at, facts: Facts(("holdExpiresAt", Instant(HoldExpiresAt.Value))));
        Raise(new BookingSubmitted(Id, AgencyId, CategoryId, Number, HoldExpiresAt.Value));
        return Result.Success();
    }

    /// <summary>
    /// <c>PendingValidation</c> → <c>InformationRequested</c>, with a predefined reason and the message to the customer;
    /// the hold is extended by <paramref name="extension"/>.
    /// </summary>
    public Result RequestInformation(InformationRequestReason reason, string? message, TimeSpan extension, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(extension, TimeSpan.Zero);
        var allowed = Ensure(BookingCommand.RequestInformation);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (!Enum.IsDefined(reason))
        {
            return BookingErrors.InformationReasonInvalid;
        }

        var text = RequireReason(message);
        if (text.IsFailure)
        {
            return text.Error;
        }

        if (HoldExpiresAt is not { } hold || hold <= at)
        {
            return BookingErrors.HoldExpired;
        }

        var before = ChangeStatus(BookingStatus.InformationRequested, at);
        HoldExpiresAt = hold + extension;
        Record(
            BookingEventType.InformationRequested,
            before,
            actor,
            at,
            text.Value,
            Facts(("reason", reason.ToString()), ("holdExpiresAt", Instant(HoldExpiresAt.Value))));
        Raise(new BookingInformationRequested(Id, AgencyId, CategoryId, reason, text.Value, HoldExpiresAt.Value));
        return Result.Success();
    }

    /// <summary><c>InformationRequested</c> → <c>PendingValidation</c> (the customer or an agent provided it); the hold is kept.</summary>
    public Result ProvideInformation(BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.ProvideInformation);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (HoldExpiresAt is not { } hold || hold <= at)
        {
            return BookingErrors.HoldExpired;
        }

        var before = ChangeStatus(BookingStatus.PendingValidation, at);
        PendingSinceUtc = at;
        EscalatedAtUtc = null;
        Record(BookingEventType.InformationProvided, before, actor, at);
        Raise(new BookingInformationProvided(Id, AgencyId, CategoryId));
        return Result.Success();
    }

    /// <summary>
    /// <c>PendingValidation</c> → <c>Confirmed</c>, or → <c>AwaitingPayment</c> until <paramref name="paymentDueAt"/> in the
    /// fallback payment mode (payment link after validation).
    /// </summary>
    public Result Validate(DateTimeOffset? paymentDueAt, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.Validate);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (HoldExpiresAt is not { } hold || hold <= at)
        {
            return BookingErrors.HoldExpired;
        }

        if (paymentDueAt is { } due && due <= at)
        {
            return BookingErrors.PaymentDueInvalid;
        }

        var before = ChangeStatus(paymentDueAt is null ? BookingStatus.Confirmed : BookingStatus.AwaitingPayment, at);
        HoldExpiresAt = null;
        PaymentDueAt = paymentDueAt?.ToUniversalTime();
        Record(
            BookingEventType.Validated,
            before,
            actor,
            at,
            facts: PaymentDueAt is { } dueAt ? Facts(("paymentDueAt", Instant(dueAt))) : null);
        Raise(new BookingValidated(Id, AgencyId, CategoryId, Status, PaymentDueAt));
        return Result.Success();
    }

    /// <summary><c>PendingValidation</c> or <c>InformationRequested</c> → <c>Refused</c>, with a reason.</summary>
    public Result Refuse(string? reason, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.Refuse);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var text = RequireReason(reason);
        if (text.IsFailure)
        {
            return text.Error;
        }

        var before = ChangeStatus(BookingStatus.Refused, at);
        StatusReason = text.Value;
        Record(BookingEventType.Refused, before, actor, at, text.Value);
        Raise(new BookingRefused(Id, AgencyId, CategoryId, before, text.Value));
        return Result.Success();
    }

    /// <summary>
    /// → <c>Expired</c> once the deadline of the status has passed: hold of a request, option, or payment deadline. The
    /// capacity is released. Run by the platform (expiry job).
    /// </summary>
    public Result Expire(BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.Expire);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (CurrentDeadline is not { } deadline || deadline > at)
        {
            return BookingErrors.DeadlineNotReached;
        }

        var before = ChangeStatus(BookingStatus.Expired, at);
        Record(BookingEventType.Expired, before, actor, at, facts: Facts(("deadline", Instant(deadline))));
        Raise(new BookingExpired(Id, AgencyId, CategoryId, before));
        return Result.Success();
    }

    /// <summary><c>OptionHeld</c> → <c>Confirmed</c>, before the option expires.</summary>
    public Result ConfirmOption(BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.ConfirmOption);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (OptionExpiresAt is not { } expiry || expiry <= at)
        {
            return BookingErrors.OptionExpired;
        }

        var before = ChangeStatus(BookingStatus.Confirmed, at);
        OptionExpiresAt = null;
        Record(BookingEventType.OptionConfirmed, before, actor, at);
        Raise(new BookingOptionConfirmed(Id, AgencyId, CategoryId));
        return Result.Success();
    }

    /// <summary><c>AwaitingPayment</c> → <c>Confirmed</c>: the payment was received (payment confirmation or agent).</summary>
    public Result MarkPaid(string? paymentReference, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.MarkPaid);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var reference = Clean(paymentReference);
        if (reference is { Length: > ReasonMaxLength })
        {
            return BookingErrors.ReasonTooLong;
        }

        var before = ChangeStatus(BookingStatus.Confirmed, at);
        PaymentDueAt = null;
        Record(BookingEventType.Paid, before, actor, at, facts: reference is null ? null : Facts(("paymentReference", reference)));
        Raise(new BookingPaid(Id, AgencyId, CategoryId, reference));
        return Result.Success();
    }

    /// <summary>
    /// → <c>Cancelled</c> with a reason; the fee computed by the cancellation policy is recorded (zero when
    /// <paramref name="feeWaived"/>).
    /// </summary>
    public Result Cancel(string? reason, Money computedFee, bool feeWaived, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.Cancel);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var text = RequireReason(reason);
        if (text.IsFailure)
        {
            return text.Error;
        }

        if (computedFee.IsNegative || computedFee.Currency != Currency.Tnd)
        {
            return BookingErrors.CancellationFeeInvalid;
        }

        var fee = feeWaived ? Money.Zero : computedFee;
        var before = ChangeStatus(BookingStatus.Cancelled, at);
        StatusReason = text.Value;
        CancellationFee = fee;
        Record(
            BookingEventType.Cancelled,
            before,
            actor,
            at,
            text.Value,
            Facts(("fee", Amount(fee)), ("computedFee", Amount(computedFee)), ("feeWaived", feeWaived ? "true" : "false")));
        Raise(new BookingCancelled(Id, AgencyId, CategoryId, before, text.Value, fee, feeWaived));
        return Result.Success();
    }

    /// <summary><c>Confirmed</c> → <c>NoShow</c>, with a reason, once <paramref name="noShowDelay"/> after the planned pick-up has passed.</summary>
    public Result MarkNoShow(string? reason, TimeSpan noShowDelay, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.MarkNoShow);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var text = RequireReason(reason);
        if (text.IsFailure)
        {
            return text.Error;
        }

        if (at < PickupAt + noShowDelay)
        {
            return BookingErrors.NoShowTooEarly;
        }

        var before = ChangeStatus(BookingStatus.NoShow, at);
        StatusReason = text.Value;
        Record(BookingEventType.NoShow, before, actor, at, text.Value);
        Raise(new BookingNoShowRecorded(Id, AgencyId, CategoryId, text.Value));
        return Result.Success();
    }

    /// <summary>Departure: <c>Confirmed</c> → <c>InProgress</c>; a vehicle must be assigned.</summary>
    public Result StartRental(DateTimeOffset actualPickupAt, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.StartRental);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (AssignedVehicleId is not { } vehicleId)
        {
            return BookingErrors.VehicleRequired;
        }

        if (actualPickupAt > at)
        {
            return BookingErrors.ActualPickupInvalid;
        }

        var before = ChangeStatus(BookingStatus.InProgress, at);
        ActualPickupAt = actualPickupAt.ToUniversalTime();
        Record(
            BookingEventType.RentalStarted,
            before,
            actor,
            at,
            facts: Facts(("vehicle", vehicleId.ToString()), ("actualPickupAt", Instant(ActualPickupAt.Value))));
        Raise(new BookingRentalStarted(Id, AgencyId, CategoryId, vehicleId, ActualPickupAt.Value));
        return Result.Success();
    }

    /// <summary>Return: <c>InProgress</c> → <c>Returned</c>; the vehicle keeps its preparation buffer after <paramref name="actualReturnAt"/>.</summary>
    public Result CompleteReturn(DateTimeOffset actualReturnAt, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.CompleteReturn);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (actualReturnAt > at || (ActualPickupAt is { } pickedUp && actualReturnAt < pickedUp))
        {
            return BookingErrors.ActualReturnInvalid;
        }

        var before = ChangeStatus(BookingStatus.Returned, at);
        ActualReturnAt = actualReturnAt.ToUniversalTime();
        Record(BookingEventType.Returned, before, actor, at, facts: Facts(("actualReturnAt", Instant(ActualReturnAt.Value))));
        Raise(new BookingReturned(Id, AgencyId, CategoryId, AssignedVehicleId, ActualReturnAt.Value));
        return Result.Success();
    }

    /// <summary><c>Returned</c> → <c>Closed</c> (balance settled, deposit returned or kept: checked by the caller).</summary>
    public Result Close(BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.Close);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var before = ChangeStatus(BookingStatus.Closed, at);
        ClosedAtUtc = at.ToUniversalTime();
        Record(BookingEventType.Closed, before, actor, at);
        Raise(new BookingClosed(Id, AgencyId, CategoryId, ClosedAtUtc.Value));
        return Result.Success();
    }

    /// <summary>
    /// Amends the file (dates, locations, category, extras, promotion code, guaranteed vehicle, drivers, payer, notes) —
    /// the status is unchanged. A file in progress can only be extended (a later return). <paramref name="quote"/> is the
    /// new quote version, required when a price input changes; <paramref name="manualDiscount"/> makes the reason mandatory.
    /// </summary>
    public Result Amend(
        BookingChanges changes,
        BookingQuoteReference? quote,
        string? reason,
        bool manualDiscount,
        BookingActor actor,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.Amend);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var text = OptionalReason(reason);
        if (text.IsFailure)
        {
            return text.Error;
        }

        if (manualDiscount && text.Value is null)
        {
            return BookingErrors.ReasonRequired;
        }

        var diff = Diff(changes);
        var valid = Status == BookingStatus.InProgress ? ValidateExtension(changes, diff, quote, at) : ValidateAmendment(changes, diff, at);
        if (valid.IsFailure)
        {
            return valid;
        }

        if (diff.IsEmpty && quote is null)
        {
            return BookingErrors.AmendmentNoChange;
        }

        if (diff.PriceInputsChanged && quote is null)
        {
            throw new ArgumentException("A change of a price input needs a new quote version.", nameof(quote));
        }

        var previousCategory = CategoryId;
        var facts = diff.Facts(this, changes);
        Apply(changes, diff);
        if (quote is not null)
        {
            AttachQuote(quote);
            facts["quoteVersion"] = quote.Version.ToString(CultureInfo.InvariantCulture);
            facts["total"] = Amount(quote.TotalInclVat);
        }

        var isExtension = Status == BookingStatus.InProgress;
        Record(isExtension ? BookingEventType.Extended : BookingEventType.Amended, Status, actor, at, text.Value, facts);
        Raise(new BookingAmended(Id, AgencyId, CategoryId, previousCategory == CategoryId ? null : previousCategory, CurrentQuoteVersion, isExtension));
        return Result.Success();
    }

    /// <summary>
    /// Assigns <paramref name="vehicleId"/> (the availability guard checked it under the lock). During the rental only a
    /// swap to another vehicle is allowed, with <paramref name="swapReason"/>. <paramref name="waiverReason"/> records the
    /// manager's compliance waiver for this vehicle (permission checked by the caller).
    /// </summary>
    public Result AssignVehicle(Guid vehicleId, string? waiverReason, string? swapReason, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (vehicleId == Guid.Empty)
        {
            throw new ArgumentException("A vehicle id is required.", nameof(vehicleId));
        }

        var allowed = Ensure(BookingCommand.AssignVehicle);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        var swap = Status == BookingStatus.InProgress;
        Result<string?> reason;
        if (swap)
        {
            if (vehicleId == AssignedVehicleId)
            {
                return BookingErrors.VehicleAlreadyAssigned;
            }

            var required = RequireReason(swapReason);
            reason = required.IsSuccess ? required.Value : Result.Failure<string?>(required.Error);
        }
        else
        {
            if (GuaranteedVehicleId is { } guaranteed && guaranteed != vehicleId)
            {
                return BookingErrors.VehicleGuaranteedModel;
            }

            if (vehicleId == AssignedVehicleId && waiverReason is null)
            {
                return BookingErrors.VehicleAlreadyAssigned;
            }

            reason = OptionalReason(swapReason);
        }

        if (reason.IsFailure)
        {
            return reason.Error;
        }

        string? waiver = null;
        if (waiverReason is not null)
        {
            var requiredWaiver = RequireReason(waiverReason);
            if (requiredWaiver.IsFailure)
            {
                return requiredWaiver.Error;
            }

            waiver = requiredWaiver.Value;
        }

        var previous = AssignedVehicleId;
        AssignedVehicleId = vehicleId;
        if (waiver is not null)
        {
            ComplianceWaiver = new ComplianceWaiver(vehicleId, actor.UserId, actor.Name, at, waiver);
        }
        else if (previous != vehicleId)
        {
            ComplianceWaiver = null;
        }

        var facts = Facts(("vehicle", vehicleId.ToString()), ("waived", IsComplianceWaived ? "true" : "false"));
        if (previous is { } previousVehicle)
        {
            facts["previousVehicle"] = previousVehicle.ToString();
        }

        if (waiver is not null)
        {
            facts["waiverReason"] = waiver;
        }

        Record(swap ? BookingEventType.VehicleSwapped : BookingEventType.VehicleAssigned, Status, actor, at, reason.Value, facts);
        Raise(new BookingVehicleAssigned(Id, AgencyId, CategoryId, vehicleId, previous, IsComplianceWaived));
        return Result.Success();
    }

    /// <summary>Unassigns the vehicle before the departure (not the vehicle of a guaranteed model).</summary>
    public Result UnassignVehicle(BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var allowed = Ensure(BookingCommand.UnassignVehicle);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (GuaranteedVehicleId is not null)
        {
            return BookingErrors.VehicleGuaranteedModel;
        }

        if (AssignedVehicleId is not { } previous)
        {
            return BookingErrors.VehicleNotAssigned;
        }

        AssignedVehicleId = null;
        ComplianceWaiver = null;
        Record(BookingEventType.VehicleUnassigned, Status, actor, at, facts: Facts(("previousVehicle", previous.ToString())));
        Raise(new BookingVehicleUnassigned(Id, AgencyId, CategoryId, previous));
        return Result.Success();
    }

    /// <summary>
    /// Records that the managers were told about a request still pending after the escalation delay. Once per pending
    /// period: a second call does nothing and returns <see langword="false"/>.
    /// </summary>
    public Result<bool> MarkEscalated(BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Status != BookingStatus.PendingValidation)
        {
            return BookingErrors.InvalidTransition;
        }

        if (EscalatedAtUtc is not null)
        {
            return false;
        }

        EscalatedAtUtc = at.ToUniversalTime();
        Record(BookingEventType.Escalated, Status, actor, at, facts: PendingSinceUtc is { } since ? Facts(("pendingSince", Instant(since))) : null);
        Raise(new BookingEscalated(Id, AgencyId, CategoryId, Number));
        return true;
    }

    private static Result<Booking> Create(
        Guid id,
        BookingDetails details,
        DocumentNumber number,
        BookingQuoteReference quote,
        BookingStatus status,
        DateTimeOffset? optionExpiresAt,
        BookingActor actor,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(actor);
        if (number.Type != DocumentType.Booking)
        {
            throw new ArgumentException("A rental file carries a LOC number.", nameof(number));
        }

        var valid = ValidateDetails(details, complete: status != BookingStatus.Draft, at);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        if (optionExpiresAt is { } expiry && expiry <= at)
        {
            return BookingErrors.OptionExpiryInvalid;
        }

        var booking = new Booking(id, details, number.Value, status, quote, at)
        {
            OptionExpiresAt = optionExpiresAt?.ToUniversalTime(),
        };
        var facts = Facts(
            ("number", booking.Number),
            ("channel", booking.Channel.ToString()),
            ("quoteVersion", quote.Version.ToString(CultureInfo.InvariantCulture)),
            ("total", Amount(quote.TotalInclVat)));
        if (booking.OptionExpiresAt is { } optionExpiry)
        {
            facts["optionExpiresAt"] = Instant(optionExpiry);
        }

        booking.Record(BookingEventType.Created, null, actor, at, facts: facts);
        booking.Raise(new BookingCreated(
            booking.Id,
            booking.AgencyId,
            booking.Number,
            booking.Channel,
            booking.Status,
            booking.CategoryId,
            booking.PickupAt,
            booking.ReturnAt,
            booking.TotalInclVat));
        return booking;
    }

    private static Result ValidateDetails(BookingDetails details, bool complete, DateTimeOffset at)
    {
        if (details.AgencyId == Guid.Empty)
        {
            return BookingErrors.AgencyRequired;
        }

        if (!Enum.IsDefined(details.Channel))
        {
            return BookingErrors.ChannelInvalid;
        }

        if (details.CategoryId == Guid.Empty)
        {
            return BookingErrors.CategoryRequired;
        }

        if (details.PickupLocationId == Guid.Empty || details.ReturnLocationId == Guid.Empty)
        {
            return BookingErrors.LocationRequired;
        }

        if (details.ReturnAt <= details.PickupAt)
        {
            return BookingErrors.PeriodInvalid;
        }

        if (details.PickupAt < at - PickupPastTolerance)
        {
            return BookingErrors.PickupInPast;
        }

        if (details.GuaranteedVehicleId == Guid.Empty)
        {
            throw new ArgumentException("A guaranteed vehicle id cannot be empty.", nameof(details));
        }

        if (Clean(details.ChannelReference) is { Length: > ChannelReferenceMaxLength })
        {
            return BookingErrors.ChannelReferenceInvalid;
        }

        return Result.Combine(
            (details.Parties ?? throw new ArgumentException("The parties are required.", nameof(details))).Validate(complete),
            ExtraChoice.Normalize(details.Extras),
            ValidatePromoCode(details.PromoCode),
            ValidateNotes(details.Notes));
    }

    private static Result ValidatePromoCode(string? promoCode) =>
        NormalizePromoCode(promoCode) is { Length: > PromoCodeMaxLength } ? BookingErrors.PromoCodeInvalid : Result.Success();

    private static Result ValidateNotes(string? notes) =>
        Clean(notes) is { Length: > NotesMaxLength } ? BookingErrors.NotesTooLong : Result.Success();

    private static string? NormalizePromoCode(string? promoCode) =>
        string.IsNullOrWhiteSpace(promoCode) ? null : promoCode.Trim().ToUpperInvariant();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Result<string> RequireReason(string? reason)
    {
        var text = Clean(reason);
        if (text is null)
        {
            return BookingErrors.ReasonRequired;
        }

        return text.Length > ReasonMaxLength ? BookingErrors.ReasonTooLong : text;
    }

    private static Result<string?> OptionalReason(string? reason)
    {
        var text = Clean(reason);
        return text is { Length: > ReasonMaxLength } ? BookingErrors.ReasonTooLong : text;
    }

    private static Dictionary<string, string> Facts(params (string Key, string Value)[] facts) =>
        facts.ToDictionary(fact => fact.Key, fact => fact.Value, StringComparer.Ordinal);

    private static string Instant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Amount(Money amount) => amount.Amount.ToString("F3", CultureInfo.InvariantCulture);

    private Result Ensure(BookingCommand command) =>
        BookingStateMachine.CanApply(command, Status) ? Result.Success() : BookingErrors.InvalidTransition;

    private BookingStatus ChangeStatus(BookingStatus status, DateTimeOffset at)
    {
        var before = Status;
        Status = status;
        StatusChangedAtUtc = at.ToUniversalTime();
        return before;
    }

    private void Record(
        BookingEventType type,
        BookingStatus? before,
        BookingActor actor,
        DateTimeOffset at,
        string? reason = null,
        IReadOnlyDictionary<string, string>? facts = null)
    {
        LastEventSequence++;
        _events.Add(BookingEvent.Create(Id, LastEventSequence, at, type, before, Status, actor, reason, facts));
    }

    private void ApplyParties(BookingParties parties)
    {
        MainDriverId = parties.MainDriverId;
        SecondaryDriverId = parties.SecondaryDriverId;
        PayerCustomerId = parties.PayerCustomerId;
    }

    private void AttachQuote(BookingQuoteReference quote)
    {
        if (quote.QuoteId == Guid.Empty || quote.Version < 1 || quote.TotalInclVat.IsNegative || quote.Deposit.IsNegative)
        {
            throw new ArgumentException("A quote reference has an id, a version from 1 and non-negative totals.", nameof(quote));
        }

        if (quote.Version <= CurrentQuoteVersion)
        {
            throw new ArgumentException("A new quote version must follow the current one.", nameof(quote));
        }

        CurrentQuoteId = quote.QuoteId;
        CurrentQuoteVersion = quote.Version;
        TotalInclVat = quote.TotalInclVat;
        Deposit = quote.Deposit;
    }

    private AmendmentDiff Diff(BookingChanges changes) => new(
        CategoryChanged: changes.CategoryId != CategoryId,
        PickupLocationChanged: changes.PickupLocationId != PickupLocationId,
        ReturnLocationChanged: changes.ReturnLocationId != ReturnLocationId,
        PickupChanged: changes.PickupAt.ToUniversalTime() != PickupAt,
        ReturnChanged: changes.ReturnAt.ToUniversalTime() != ReturnAt,
        PartiesChanged: changes.Parties != Parties,
        DriversChanged: changes.Parties.MainDriverId != MainDriverId || changes.Parties.SecondaryDriverId != SecondaryDriverId,
        ExtrasChanged: ExtraChoice.Normalize(changes.Extras) is not { IsSuccess: true } extras
                       || ExtraChoice.Format(extras.Value) != ExtraSelection,
        PromoCodeChanged: NormalizePromoCode(changes.PromoCode) != PromoCode,
        GuaranteedVehicleChanged: changes.GuaranteedVehicleId != GuaranteedVehicleId,
        NotesChanged: Clean(changes.Notes) != Notes,
        VehicleReleased: changes.ReleaseVehicle && AssignedVehicleId is not null);

    private static Result ValidateAmendment(BookingChanges changes, AmendmentDiff diff, DateTimeOffset at)
    {
        if (changes.CategoryId == Guid.Empty)
        {
            return BookingErrors.CategoryRequired;
        }

        if (changes.PickupLocationId == Guid.Empty || changes.ReturnLocationId == Guid.Empty)
        {
            return BookingErrors.LocationRequired;
        }

        if (changes.ReturnAt <= changes.PickupAt)
        {
            return BookingErrors.PeriodInvalid;
        }

        if (diff.PickupChanged && changes.PickupAt < at - PickupPastTolerance)
        {
            return BookingErrors.PickupInPast;
        }

        if (changes.GuaranteedVehicleId == Guid.Empty)
        {
            throw new ArgumentException("A guaranteed vehicle id cannot be empty.", nameof(changes));
        }

        if (changes.ReleaseVehicle && changes.GuaranteedVehicleId is not null)
        {
            return BookingErrors.VehicleGuaranteedModel;
        }

        return Result.Combine(
            (changes.Parties ?? throw new ArgumentException("The parties are required.", nameof(changes))).Validate(complete: true),
            ExtraChoice.Normalize(changes.Extras),
            ValidatePromoCode(changes.PromoCode),
            ValidateNotes(changes.Notes));
    }

    private Result ValidateExtension(BookingChanges changes, AmendmentDiff diff, BookingQuoteReference? quote, DateTimeOffset at)
    {
        if (diff.CategoryChanged || diff.PickupLocationChanged || diff.ReturnLocationChanged || diff.PickupChanged || diff.PartiesChanged
            || diff.ExtrasChanged || diff.PromoCodeChanged || diff.GuaranteedVehicleChanged || diff.VehicleReleased)
        {
            return BookingErrors.AmendmentExtensionOnly;
        }

        if (!diff.ReturnChanged)
        {
            return diff.NotesChanged && quote is null ? ValidateNotes(changes.Notes) : BookingErrors.AmendmentExtensionOnly;
        }

        if (changes.ReturnAt <= ReturnAt || changes.ReturnAt <= at)
        {
            return BookingErrors.AmendmentExtensionOnly;
        }

        return ValidateNotes(changes.Notes);
    }

    private void Apply(BookingChanges changes, AmendmentDiff diff)
    {
        CategoryId = changes.CategoryId;
        PickupLocationId = changes.PickupLocationId;
        ReturnLocationId = changes.ReturnLocationId;
        PickupAt = changes.PickupAt.ToUniversalTime();
        ReturnAt = changes.ReturnAt.ToUniversalTime();
        ApplyParties(changes.Parties);
        ExtraSelection = ExtraChoice.Format(ExtraChoice.Normalize(changes.Extras).Value);
        PromoCode = NormalizePromoCode(changes.PromoCode);
        Notes = Clean(changes.Notes);

        // A new guaranteed vehicle is the assigned one; removing the guarantee keeps the vehicle unless it is released or
        // the category changes (the vehicle belongs to the former category).
        if (diff.GuaranteedVehicleChanged && changes.GuaranteedVehicleId is { } guaranteed)
        {
            AssignedVehicleId = guaranteed;
        }
        else if (diff.CategoryChanged || diff.VehicleReleased)
        {
            AssignedVehicleId = null;
        }

        GuaranteedVehicleId = changes.GuaranteedVehicleId;
        if (!IsComplianceWaived)
        {
            ComplianceWaiver = null;
        }
    }

    /// <summary>What an amendment changes, and the facts it writes to the timeline.</summary>
    private sealed record AmendmentDiff(
        bool CategoryChanged,
        bool PickupLocationChanged,
        bool ReturnLocationChanged,
        bool PickupChanged,
        bool ReturnChanged,
        bool PartiesChanged,
        bool DriversChanged,
        bool ExtrasChanged,
        bool PromoCodeChanged,
        bool GuaranteedVehicleChanged,
        bool NotesChanged,
        bool VehicleReleased)
    {
        public bool IsEmpty => !(CategoryChanged || PickupLocationChanged || ReturnLocationChanged || PickupChanged || ReturnChanged
                                 || PartiesChanged || ExtrasChanged || PromoCodeChanged || GuaranteedVehicleChanged || NotesChanged
                                 || VehicleReleased);

        /// <summary>A change that the quote depends on (the payer and the notes do not change the price).</summary>
        public bool PriceInputsChanged => CategoryChanged || PickupLocationChanged || ReturnLocationChanged || PickupChanged || ReturnChanged
                                          || DriversChanged || ExtrasChanged || PromoCodeChanged || GuaranteedVehicleChanged;

        public Dictionary<string, string> Facts(Booking booking, BookingChanges changes)
        {
            var facts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (CategoryChanged)
            {
                facts["category"] = $"{booking.CategoryId} → {changes.CategoryId}";
            }

            if (PickupChanged)
            {
                facts["pickupAt"] = $"{Instant(booking.PickupAt)} → {Instant(changes.PickupAt)}";
            }

            if (ReturnChanged)
            {
                facts["returnAt"] = $"{Instant(booking.ReturnAt)} → {Instant(changes.ReturnAt)}";
            }

            if (PickupLocationChanged)
            {
                facts["pickupLocation"] = $"{booking.PickupLocationId} → {changes.PickupLocationId}";
            }

            if (ReturnLocationChanged)
            {
                facts["returnLocation"] = $"{booking.ReturnLocationId} → {changes.ReturnLocationId}";
            }

            if (DriversChanged)
            {
                facts["drivers"] = "changed";
            }

            if (PartiesChanged && changes.Parties.PayerCustomerId != booking.PayerCustomerId)
            {
                facts["payer"] = "changed";
            }

            if (ExtrasChanged)
            {
                facts["extras"] = ExtraChoice.Format(ExtraChoice.Normalize(changes.Extras).Value);
            }

            if (PromoCodeChanged)
            {
                facts["promoCode"] = NormalizePromoCode(changes.PromoCode) ?? string.Empty;
            }

            if (GuaranteedVehicleChanged)
            {
                facts["guaranteedVehicle"] = changes.GuaranteedVehicleId?.ToString() ?? string.Empty;
            }

            if (VehicleReleased)
            {
                facts["vehicleReleased"] = booking.AssignedVehicleId?.ToString() ?? string.Empty;
            }

            if (NotesChanged)
            {
                facts["notes"] = "changed";
            }

            return facts;
        }
    }
}
