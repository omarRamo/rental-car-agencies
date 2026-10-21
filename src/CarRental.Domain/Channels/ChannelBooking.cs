using CarRental.Domain.Common;

namespace CarRental.Domain.Channels;

/// <summary>
/// The channel side of a rental file created by a channel (SPEC §10 "Réservations reçues"): its reference in the
/// channel (the idempotency key: unique per channel), where it came from, the payment mode, the gross price, the
/// commission (the rule of the channel, or the amount it reported) and the net, the engine's total for comparison, and
/// the state mirrored from the rental file. No personal data: the drivers live in the Customers module.
/// </summary>
public sealed class ChannelBooking : AggregateRoot
{
    /// <summary>Length of a request fingerprint (SHA-256, hexadecimal).</summary>
    public const int FingerprintLength = 64;

    public const int BookingNumberMaxLength = 24;

    private ChannelBooking()
    {
    }

    private ChannelBooking(ChannelBookingDetails details, ChannelAmounts amounts, DateTimeOffset at)
        : base(NewId())
    {
        ChannelId = details.ChannelId;
        AgencyId = details.AgencyId;
        SalesChannel = details.SalesChannel;
        BookingId = details.BookingId;
        BookingNumber = details.BookingNumber;
        ChannelReference = details.ChannelReference;
        Source = details.Source;
        PaymentMode = details.PaymentMode;
        RequestFingerprint = details.RequestFingerprint;
        State = ChannelBookingState.Active;
        Apply(amounts, details.CategoryId, details.PickupAt, details.ReturnAt, details.EngineTotalInclVat);
        ReceivedAtUtc = at.ToUniversalTime();
        UpdatedAtUtc = ReceivedAtUtc;
    }

    public Guid ChannelId { get; private set; }

    public Guid AgencyId { get; private set; }

    public SalesChannel SalesChannel { get; private set; }

    /// <summary>The rental file (Bookings module), by id.</summary>
    public Guid BookingId { get; private set; }

    /// <summary>The readable number of the rental file (<c>LOC-2027-000123</c>), kept for the lists.</summary>
    public string BookingNumber { get; private set; } = null!;

    public string ChannelReference { get; private set; } = null!;

    public ChannelBookingSource Source { get; private set; }

    public ChannelBookingState State { get; private set; }

    public ChannelPaymentMode PaymentMode { get; private set; }

    /// <summary>What the customer pays for the rental on the channel, VAT included.</summary>
    public Money Gross { get; private set; }

    /// <summary>The commission owed to the channel: none for a cancelled booking unless the channel reported one.</summary>
    public Money Commission { get; private set; }

    /// <summary><see cref="Gross"/> minus <see cref="Commission"/>.</summary>
    public Money Net { get; private set; }

    public ChannelCommissionSource CommissionSource { get; private set; }

    /// <summary>The rate applied when <see cref="CommissionSource"/> is the rule (0 otherwise).</summary>
    public decimal CommissionRate { get; private set; }

    /// <summary>Total of the rental file priced by the engine for the channel (the reconciliation shows the difference).</summary>
    public Money EngineTotalInclVat { get; private set; }

    public Guid CategoryId { get; private set; }

    public DateTimeOffset PickupAt { get; private set; }

    public DateTimeOffset ReturnAt { get; private set; }

    /// <summary>Fingerprint of the creation request (dates, places, category, amounts, extras; no personal data).</summary>
    public string RequestFingerprint { get; private set; } = null!;

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public int ModificationCount { get; private set; }

    public static Result<ChannelBooking> Record(ChannelBookingDetails details, ChannelAmounts amounts, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(amounts);
        if (details.ChannelId == Guid.Empty || details.AgencyId == Guid.Empty || details.BookingId == Guid.Empty || details.CategoryId == Guid.Empty)
        {
            throw new ArgumentException("A channel booking needs its channel, agency, rental file and category.", nameof(details));
        }

        var reference = ChannelCodes.Reference(details.ChannelReference);
        if (reference.IsFailure)
        {
            return reference.Error;
        }

        if (string.IsNullOrWhiteSpace(details.BookingNumber) || details.BookingNumber.Length > BookingNumberMaxLength
            || details.RequestFingerprint.Length != FingerprintLength
            || !Enum.IsDefined(details.Source) || !Enum.IsDefined(details.PaymentMode) || details.ReturnAt <= details.PickupAt)
        {
            throw new ArgumentException("The details of the channel booking are inconsistent.", nameof(details));
        }

        var booking = new ChannelBooking(details with { ChannelReference = reference.Value }, amounts, at);
        booking.Raise(new ChannelBookingRecorded(booking.Id, booking.ChannelId, booking.BookingId, booking.Source));
        return booking;
    }

    /// <summary>The channel modified the booking: new amounts, category, dates and engine total.</summary>
    public Result Amend(ChannelAmounts amounts, Guid categoryId, DateTimeOffset pickupAt, DateTimeOffset returnAt, Money engineTotalInclVat, ChannelPaymentMode paymentMode, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        if (State == ChannelBookingState.Cancelled)
        {
            return ChannelsErrors.BookingCancelled;
        }

        if (categoryId == Guid.Empty || returnAt <= pickupAt || !Enum.IsDefined(paymentMode))
        {
            throw new ArgumentException("The amended channel booking is inconsistent.", nameof(categoryId));
        }

        Apply(amounts, categoryId, pickupAt, returnAt, engineTotalInclVat);
        PaymentMode = paymentMode;
        ModificationCount++;
        UpdatedAtUtc = at.ToUniversalTime();
        Raise(new ChannelBookingAmended(Id, ChannelId, BookingId));
        return Result.Success();
    }

    /// <summary>
    /// The booking was cancelled: no commission is owed, unless the channel reported one (cancellation fee of its
    /// contract: TODO(spec)). Idempotent.
    /// </summary>
    public Result Cancel(Money? reportedCommission, DateTimeOffset at)
    {
        if (reportedCommission is { } reported && (reported.IsNegative || reported.Currency != Currency.Tnd || reported > Gross))
        {
            return ChannelsErrors.AmountInvalid;
        }

        if (State == ChannelBookingState.Cancelled)
        {
            return Result.Success();
        }

        State = ChannelBookingState.Cancelled;
        if (reportedCommission is { } kept)
        {
            Commission = kept;
            CommissionSource = ChannelCommissionSource.Reported;
            CommissionRate = 0m;
        }
        else
        {
            Commission = Money.Zero;
        }

        Net = Gross - Commission;
        CancelledAtUtc = at.ToUniversalTime();
        UpdatedAtUtc = CancelledAtUtc.Value;
        Raise(new ChannelBookingCancelled(Id, ChannelId, BookingId));
        return Result.Success();
    }

    /// <summary>Follows the rental file: a file cancelled elsewhere cancels the channel booking; a no-show is recorded.</summary>
    public void Mirror(ChannelBookingState state, DateTimeOffset at)
    {
        if (state == State || !Enum.IsDefined(state))
        {
            return;
        }

        if (state == ChannelBookingState.Cancelled)
        {
            _ = Cancel(null, at);
            return;
        }

        if (State == ChannelBookingState.Cancelled)
        {
            // A cancelled file never comes back to life.
            return;
        }

        State = state;
        UpdatedAtUtc = at.ToUniversalTime();
    }

    private void Apply(ChannelAmounts amounts, Guid categoryId, DateTimeOffset pickupAt, DateTimeOffset returnAt, Money engineTotalInclVat)
    {
        Gross = amounts.Gross;
        Commission = amounts.Commission;
        Net = amounts.Net;
        CommissionSource = amounts.Source;
        CommissionRate = amounts.Rate;
        CategoryId = categoryId;
        PickupAt = pickupAt.ToUniversalTime();
        ReturnAt = returnAt.ToUniversalTime();
        EngineTotalInclVat = engineTotalInclVat;
    }
}

/// <summary>What a channel booking is recorded with.</summary>
public sealed record ChannelBookingDetails(
    Guid ChannelId,
    Guid AgencyId,
    SalesChannel SalesChannel,
    Guid BookingId,
    string BookingNumber,
    string ChannelReference,
    ChannelBookingSource Source,
    ChannelPaymentMode PaymentMode,
    Guid CategoryId,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    Money EngineTotalInclVat,
    string RequestFingerprint);

/// <summary>Gross, commission and net of a channel booking, and where the commission comes from.</summary>
public sealed record ChannelAmounts(Money Gross, Money Commission, Money Net, ChannelCommissionSource Source, decimal Rate);

/// <summary>
/// The commission rule of a channel (docs/conception/phase-5-canaux.md §1): the amount reported by the channel wins;
/// otherwise the rate of the channel applied to the gross price, rounded to the millime (away from zero). The net is
/// the gross minus the commission; a net reported by the channel must agree with it.
/// </summary>
public static class ChannelCommissionRule
{
    /// <summary>Highest amount of a channel booking (TND).</summary>
    public const decimal MaxAmount = 1_000_000m;

    public static Result<ChannelAmounts> Compute(Money gross, Money? reportedCommission, Money? reportedNet, decimal rate)
    {
        if (!IsTnd(gross) || !gross.IsPositive || gross.Amount > MaxAmount)
        {
            return ChannelsErrors.AmountInvalid;
        }

        if (rate is < 0m or > Channel.MaxCommissionRate)
        {
            return ChannelsErrors.CommissionRateInvalid;
        }

        Money commission;
        ChannelCommissionSource source;
        if (reportedCommission is { } reported)
        {
            if (!IsTnd(reported) || reported.IsNegative || reported > gross)
            {
                return ChannelsErrors.AmountInvalid;
            }

            commission = reported;
            source = ChannelCommissionSource.Reported;
            rate = 0m;
        }
        else
        {
            commission = gross * rate;
            source = ChannelCommissionSource.Rule;
        }

        var net = gross - commission;
        if (reportedNet is { } givenNet && (!IsTnd(givenNet) || givenNet != net))
        {
            return ChannelsErrors.AmountsInconsistent;
        }

        return new ChannelAmounts(gross, commission, net, source, rate);
    }

    private static bool IsTnd(Money amount) => amount.Currency == Currency.Tnd;
}
