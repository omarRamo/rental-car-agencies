using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>What a notice (procès-verbal) says: number, authority, instant and place of the offence, plate, amount, notes.</summary>
public sealed record FineDetails(
    string? TicketNumber,
    string? Authority,
    DateTimeOffset OffenceAt,
    string? Place,
    string? PlateNumber,
    decimal Amount,
    string? Notes = null);

/// <summary>
/// A rental that carried a vehicle from <see cref="CarriedFrom"/> (actual pick-up, or a vehicle swap) to
/// <see cref="CarriedUntil"/> (actual return or swap; <see langword="null"/> while the rental goes on), with its drivers.
/// </summary>
public sealed record RentalCandidate(
    Guid BookingId,
    string Number,
    DateTimeOffset CarriedFrom,
    DateTimeOffset? CarriedUntil,
    Guid? MainDriverId,
    Guid? SecondaryDriverId)
{
    /// <summary>Whether the rental had the vehicle at <paramref name="instant"/>: <c>[from, until)</c>, open while in progress.</summary>
    public bool Covers(DateTimeOffset instant) => CarriedFrom <= instant && (CarriedUntil is null || instant < CarriedUntil);

    public bool HasDriver(Guid personId) => personId != Guid.Empty && (MainDriverId == personId || SecondaryDriverId == personId);
}

/// <summary>The outcome of the search of the rental of a fine: the vehicle found, the rental matched, or the candidates.</summary>
public sealed record FineMatchResult(FineMatch Match, Guid? VehicleId, RentalCandidate? Rental, IReadOnlyList<Guid> Candidates);

/// <summary>
/// Who had the vehicle at the instant of the offence (docs/conception/phase-3-finance.md §2): the rentals in progress,
/// returned or closed whose actual period covers the instant. Exactly one gives the file and its main driver; none or
/// several are listed for an agent, who chooses.
/// </summary>
public static class FineMatching
{
    public static FineMatchResult Match(Guid? vehicleId, DateTimeOffset offenceAt, IReadOnlyList<RentalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (vehicleId is null)
        {
            return new FineMatchResult(FineMatch.UnknownVehicle, null, null, []);
        }

        var covering = candidates.Where(c => c is not null && c.Covers(offenceAt)).DistinctBy(c => c.BookingId).ToList();
        return covering.Count switch
        {
            0 => new FineMatchResult(FineMatch.NoRental, vehicleId, null, []),
            1 => new FineMatchResult(FineMatch.Matched, vehicleId, covering[0], [covering[0].BookingId]),
            _ => new FineMatchResult(FineMatch.Ambiguous, vehicleId, null, [.. covering.Select(c => c.BookingId)]),
        };
    }
}

/// <summary>The comparison key of a plate number: its letters and digits, upper case (<c>123 TU 4567</c> → <c>123TU4567</c>).</summary>
public static class PlateKeys
{
    public static string Of(string? plateNumber)
    {
        var key = new StringBuilder(plateNumber?.Length ?? 0);
        foreach (var character in plateNumber ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character))
            {
                key.Append(char.ToUpperInvariant(character));
            }
        }

        return key.ToString();
    }
}

/// <summary>
/// A traffic fine received by the agency for one of its vehicles (amende), aggregate of the Billing module: the notice,
/// the rental file and the driver who had the vehicle at that instant (found by <see cref="FineMatching"/> or chosen by an
/// agent), the designation of the driver to the authority, the optional re-invoicing to the payer and its payment.
/// </summary>
public sealed class Fine : AggregateRoot
{
    public const int TicketMaxLength = 40;
    public const int AuthorityMaxLength = 120;
    public const int PlaceMaxLength = 200;
    public const int PlateMaxLength = 20;
    public const int NotesMaxLength = 1000;
    public const int CloseNoteMaxLength = 500;
    public const int BookingNumberMaxLength = 32;
    public const decimal MaxAmount = 100_000m;
    public const int MaxCandidates = 10;

    private List<Guid> _candidates = [];

    private Fine()
    {
    }

    private Fine(Guid id, Guid agencyId, string ticket, string authority, DateTimeOffset offenceAt, string place, string plate, Money amount, string? notes, BillingActor actor, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        TicketNumber = ticket;
        Authority = authority;
        OffenceAtUtc = offenceAt.ToUniversalTime();
        Place = place;
        PlateNumber = plate;
        PlateKey = PlateKeys.Of(plate);
        Amount = amount;
        Notes = notes;
        Status = FineStatus.Recorded;
        RecordedAtUtc = now.ToUniversalTime();
        RecordedByUserId = actor.UserId;
        RecordedBy = actor.Name;
        StatusChangedAtUtc = RecordedAtUtc;
        StatusChangedBy = actor.Name;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Number of the notice (procès-verbal).</summary>
    public string TicketNumber { get; private set; } = null!;

    /// <summary>The authority that issued it (police, garde nationale, municipality…).</summary>
    public string Authority { get; private set; } = null!;

    public DateTimeOffset OffenceAtUtc { get; private set; }

    public string Place { get; private set; } = null!;

    public string PlateNumber { get; private set; } = null!;

    /// <summary><see cref="PlateKeys.Of"/> of the plate (search).</summary>
    public string PlateKey { get; private set; } = null!;

    public Money Amount { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>The vehicle of the plate, when it is one of the agency.</summary>
    public Guid? VehicleId { get; private set; }

    public FineMatch Match { get; private set; }

    /// <summary>The rental file that had the vehicle.</summary>
    public Guid? BookingId { get; private set; }

    public string? BookingNumber { get; private set; }

    /// <summary>The driver designated (the main driver of the file by default).</summary>
    public Guid? DriverPersonId { get; private set; }

    /// <summary>The files that covered the instant when several did.</summary>
    public IReadOnlyList<Guid> CandidateBookingIds
    {
        get => _candidates;
        private set => _candidates = [.. value];
    }

    public FineStatus Status { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public string RecordedBy { get; private set; } = null!;

    public DateTimeOffset? DesignatedAtUtc { get; private set; }

    public string? DesignatedBy { get; private set; }

    /// <summary>The invoice that re-invoiced the fine.</summary>
    public Guid? InvoiceId { get; private set; }

    public string? InvoiceNumber { get; private set; }

    public DateTimeOffset? ReinvoicedAtUtc { get; private set; }

    /// <summary>When the fine was paid to the authority (by the agency or by the driver).</summary>
    public DateTimeOffset? PaidAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public string? CloseNote { get; private set; }

    public DateTimeOffset StatusChangedAtUtc { get; private set; }

    public string StatusChangedBy { get; private set; } = null!;

    /// <summary>Whether the fine is attached to a file and a driver (designation and re-invoicing need both).</summary>
    public bool HasRental => BookingId is not null && DriverPersonId is not null;

    /// <summary>Records a notice and the outcome of the search of its rental (<see cref="FineMatching.Match"/>).</summary>
    public static Result<Fine> Record(Guid agencyId, FineDetails details, FineMatchResult match, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(actor);
        var ticket = Clean(details.TicketNumber);
        if (agencyId == Guid.Empty || ticket is not { Length: <= TicketMaxLength })
        {
            return BillingErrors.FineTicketRequired;
        }

        var authority = Clean(details.Authority);
        if (authority is not { Length: <= AuthorityMaxLength })
        {
            return BillingErrors.FineAuthorityRequired;
        }

        var place = Clean(details.Place);
        if (place is not { Length: <= PlaceMaxLength })
        {
            return BillingErrors.FinePlaceRequired;
        }

        var plate = Clean(details.PlateNumber)?.ToUpperInvariant();
        if (plate is not { Length: <= PlateMaxLength } || PlateKeys.Of(plate).Length == 0)
        {
            return BillingErrors.FinePlateRequired;
        }

        if (details.Amount is <= 0m or > MaxAmount || Money.FromExact(details.Amount, Currency.Tnd) is not { IsSuccess: true } amount)
        {
            return BillingErrors.FineAmountInvalid;
        }

        if (details.OffenceAt > now)
        {
            return BillingErrors.FineOffenceInFuture;
        }

        var notes = Clean(details.Notes);
        if (notes is { Length: > NotesMaxLength })
        {
            return BillingErrors.FineNotesTooLong;
        }

        var fine = new Fine(Entity.NewId(), agencyId, ticket, authority, details.OffenceAt, place, plate, amount.Value, notes, actor, now)
        {
            VehicleId = match.VehicleId,
            Match = match.Match,
            _candidates = match.Match == FineMatch.Ambiguous ? [.. match.Candidates.Take(MaxCandidates)] : [],
        };
        if (match is { Match: FineMatch.Matched, Rental: { } rental } && rental.MainDriverId is { } driver)
        {
            fine.BookingId = rental.BookingId;
            fine.BookingNumber = Truncate(rental.Number, BookingNumberMaxLength);
            fine.DriverPersonId = driver;
        }

        fine.Raise(new FineRecorded(fine.Id, agencyId, fine.Match, fine.BookingId));
        return fine;
    }

    /// <summary>
    /// An agent chooses the file and its driver (ambiguous or missing match, or another driver of the file): the file must
    /// have had the vehicle at the instant of the offence. Only before the designation and the re-invoicing.
    /// </summary>
    public Result AssignRental(RentalCandidate rental, Guid driverPersonId, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rental);
        ArgumentNullException.ThrowIfNull(actor);
        if (Status != FineStatus.Recorded)
        {
            return BillingErrors.FineInvalidTransition;
        }

        if (!rental.Covers(OffenceAtUtc))
        {
            return BillingErrors.FineRentalNotCovering;
        }

        if (!rental.HasDriver(driverPersonId))
        {
            return BillingErrors.FineDriverInvalid;
        }

        BookingId = rental.BookingId;
        BookingNumber = Truncate(rental.Number, BookingNumberMaxLength);
        DriverPersonId = driverPersonId;
        Match = FineMatch.Manual;
        _candidates = [];
        StatusChangedAtUtc = now.ToUniversalTime();
        StatusChangedBy = actor.Name;
        Raise(new FineRentalAssigned(Id, AgencyId, rental.BookingId));
        return Result.Success();
    }

    /// <summary>The driver is designated to the authority (designation form produced). Again later: no change.</summary>
    public Result Designate(BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!HasRental)
        {
            return BillingErrors.FineNoRental;
        }

        if (Status == FineStatus.Designated)
        {
            return Result.Success();
        }

        if (Status != FineStatus.Recorded)
        {
            return BillingErrors.FineInvalidTransition;
        }

        DesignatedAtUtc = now.ToUniversalTime();
        DesignatedBy = actor.Name;
        return ChangeStatus(FineStatus.Designated, actor, now);
    }

    /// <summary>The fine was re-invoiced to the payer of the file by <paramref name="invoiceNumber"/> (once).</summary>
    public Result MarkReinvoiced(Guid invoiceId, string invoiceNumber, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (InvoiceId is not null)
        {
            return BillingErrors.FineAlreadyReinvoiced;
        }

        if (!HasRental)
        {
            return BillingErrors.FineNoRental;
        }

        if (Status is not (FineStatus.Recorded or FineStatus.Designated) || invoiceId == Guid.Empty || string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return BillingErrors.FineInvalidTransition;
        }

        InvoiceId = invoiceId;
        InvoiceNumber = invoiceNumber;
        ReinvoicedAtUtc = now.ToUniversalTime();
        return ChangeStatus(FineStatus.Reinvoiced, actor, now);
    }

    /// <summary>The fine was paid to the authority.</summary>
    public Result MarkPaid(BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Status is not (FineStatus.Recorded or FineStatus.Designated or FineStatus.Reinvoiced))
        {
            return BillingErrors.FineInvalidTransition;
        }

        PaidAtUtc = now.ToUniversalTime();
        return ChangeStatus(FineStatus.Paid, actor, now);
    }

    /// <summary>Nothing left to do (paid, contested, cancelled by the authority…), with an optional note.</summary>
    public Result Close(string? note, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Status == FineStatus.Closed)
        {
            return BillingErrors.FineInvalidTransition;
        }

        var clean = Clean(note);
        if (clean is { Length: > CloseNoteMaxLength })
        {
            return BillingErrors.FineNotesTooLong;
        }

        CloseNote = clean;
        ClosedAtUtc = now.ToUniversalTime();
        return ChangeStatus(FineStatus.Closed, actor, now);
    }

    private Result ChangeStatus(FineStatus status, BillingActor actor, DateTimeOffset now)
    {
        var previous = Status;
        Status = status;
        StatusChangedAtUtc = now.ToUniversalTime();
        StatusChangedBy = actor.Name;
        Raise(new FineStatusChanged(Id, AgencyId, previous, status));
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
