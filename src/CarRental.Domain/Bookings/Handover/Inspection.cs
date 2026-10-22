using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Handover;

/// <summary>
/// The inspection (état des lieux) of a rental file at its departure or its return (SPEC §6, ADR-0041), aggregate of the
/// Bookings module: the vehicle and its readings (mileage, fuel in eighths, accessories), the originals seen at the
/// counter, the guided photos, the damages marked on the schematic (pre-existing or new), the signatures and, for a
/// return, the charges (ADR-0042). A draft is filled step by step and survives a reload; it is completed with the
/// transition of the rental file, in the same transaction.
/// </summary>
/// <remarks>
/// <para>Every change bumps <see cref="Revision"/>, so that the <c>rowversion</c> of the root guards its children too:
/// two tablets cannot both change the same inspection.</para>
/// <para>A signature attests the inspection as it was signed: changing the readings, the photos, the damages or the
/// notes of a draft voids the signatures (they are taken again).</para>
/// <para>Files that are no longer referenced (replaced photos, voided signatures) are listed in
/// <see cref="ReleasedFiles"/> for the application to delete once the change is committed.</para>
/// </remarks>
public sealed class Inspection : AggregateRoot, IAgencyOwned
{
    public const int MaxMileage = 2_000_000;
    public const int FullTank = 8;
    public const int MaxPhotos = 40;
    public const int MaxDamages = 60;
    public const int NotesMaxLength = 1000;
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 500;
    public const int OriginalsMaxLength = 200;

    private readonly List<InspectionPhoto> _photos = [];
    private readonly List<DamageMark> _damages = [];
    private readonly List<InspectionSignature> _signatures = [];
    private readonly List<ReturnCharge> _charges = [];
    private readonly List<string> _releasedFiles = [];

    private Inspection()
    {
    }

    private Inspection(Guid id, Guid agencyId, Guid bookingId, InspectionKind kind, BookingActor actor, DateTimeOffset at)
        : base(id)
    {
        AgencyId = agencyId;
        BookingId = bookingId;
        Kind = kind;
        Status = InspectionStatus.Draft;
        StartedByUserId = actor.UserId;
        StartedBy = actor.Name;
        StartedAtUtc = at.ToUniversalTime();
    }

    public Guid AgencyId { get; private set; }

    public Guid BookingId { get; private set; }

    public InspectionKind Kind { get; private set; }

    public InspectionStatus Status { get; private set; }

    /// <summary>The vehicle inspected (confirmed at the vehicle step of a departure; the vehicle out at a return).</summary>
    public Guid? VehicleId { get; private set; }

    /// <summary>The lowest mileage accepted: the last known mileage of the vehicle (departure), the departure mileage (return).</summary>
    public int? ReferenceMileage { get; private set; }

    public int? Mileage { get; private set; }

    /// <summary>Fuel level in eighths, from 0 (empty) to <see cref="FullTank"/>.</summary>
    public int? FuelEighths { get; private set; }

    public AccessoryChecklist? Accessories { get; private set; }

    /// <summary>The originals seen, in their stored form (<see cref="DriverOriginals.Format"/>).</summary>
    public string OriginalsSelection { get; private set; } = string.Empty;

    /// <summary>The vehicle came back dirty: a cleaning fee is proposed (return).</summary>
    public bool NeedsCleaning { get; private set; }

    public string? Notes { get; private set; }

    public Guid? StartedByUserId { get; private set; }

    public string StartedBy { get; private set; } = null!;

    public DateTimeOffset StartedAtUtc { get; private set; }

    public Guid? CompletedByUserId { get; private set; }

    public string? CompletedBy { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>The actual pick-up (departure) or return of the vehicle.</summary>
    public DateTimeOffset? HandedOverAtUtc { get; private set; }

    public PaperSignature? PaperSignature { get; private set; }

    /// <summary>The manager's deferral of the balance or waiver of the deposit (departure).</summary>
    public DepartureDeferral? Deferral { get; private set; }

    /// <summary>The cap of the damage charges, frozen with the return: the deposit of the quote (reduced with the waiver).</summary>
    public Money? Franchise { get; private set; }

    /// <summary>The franchise is the reduced deposit (the customer bought the franchise waiver).</summary>
    public bool FranchiseReduced { get; private set; }

    /// <summary>Technical counter bumped by every change (see remarks of the class).</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<InspectionPhoto> Photos => _photos;

    public IReadOnlyList<DamageMark> Damages => _damages;

    public IReadOnlyList<InspectionSignature> Signatures => _signatures;

    /// <summary>Charges of a return, in their order.</summary>
    public IReadOnlyList<ReturnCharge> Charges => [.. _charges.OrderBy(c => c.Position)];

    public IReadOnlyList<DriverOriginals> Originals => DriverOriginals.Parse(OriginalsSelection);

    /// <summary>Storage paths no longer referenced by this instance (see remarks of the class); not persisted.</summary>
    public IReadOnlyList<string> ReleasedFiles => _releasedFiles;

    public bool IsDraft => Status == InspectionStatus.Draft;

    /// <summary>The guided views without a photo.</summary>
    public IReadOnlyList<PhotoSlot> MissingGuidedPhotos => [.. PhotoSlots.Guided.Where(slot => _photos.All(p => p.Slot != slot))];

    public bool IsSigned => PaperSignature is not null
                            || (_signatures.Any(s => s.Role == SignatureRole.Customer) && _signatures.Any(s => s.Role == SignatureRole.Agent));

    /// <summary>Total of the charges, VAT included.</summary>
    public Money ChargesTotal => _charges.Select(c => c.InclVat).Sum();

    /// <summary>A departure inspection, as a draft without vehicle until the vehicle step confirms it.</summary>
    public static Inspection StartDeparture(Guid agencyId, Guid bookingId, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireIds(agencyId, bookingId);
        var inspection = new Inspection(NewId(), agencyId, bookingId, InspectionKind.Departure, actor, at);
        inspection.Raise(new InspectionStarted(inspection.Id, bookingId, InspectionKind.Departure, null));
        return inspection;
    }

    /// <summary>
    /// A return inspection of <paramref name="vehicleId"/>: the mileage cannot go below <paramref name="referenceMileage"/>
    /// (the departure mileage); the damages known at the departure are copied as pre-existing.
    /// </summary>
    public static Result<Inspection> StartReturn(
        Guid agencyId,
        Guid bookingId,
        Guid vehicleId,
        int referenceMileage,
        IEnumerable<DamageMark> knownDamages,
        BookingActor actor,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(knownDamages);
        ArgumentNullException.ThrowIfNull(actor);
        RequireIds(agencyId, bookingId);
        if (vehicleId == Guid.Empty)
        {
            return HandoverErrors.VehicleRequired;
        }

        if (referenceMileage is < 0 or > MaxMileage)
        {
            return HandoverErrors.MileageInvalid;
        }

        var inspection = new Inspection(NewId(), agencyId, bookingId, InspectionKind.Return, actor, at)
        {
            VehicleId = vehicleId,
            ReferenceMileage = referenceMileage,
        };
        inspection.CopyDamages(knownDamages, actor, at);
        inspection.Raise(new InspectionStarted(inspection.Id, bookingId, InspectionKind.Return, vehicleId));
        return inspection;
    }

    /// <summary>
    /// Confirms the vehicle of a departure: the mileage cannot go below <paramref name="lastKnownMileage"/> and the damages
    /// of its last inspection are copied as pre-existing. Another vehicle than the confirmed one starts the vehicle part
    /// again (readings, photos, damages and signatures are about the vehicle).
    /// </summary>
    public Result ConfirmVehicle(Guid vehicleId, int lastKnownMileage, IEnumerable<DamageMark> knownDamages, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(knownDamages);
        ArgumentNullException.ThrowIfNull(actor);
        var editable = EnsureDraft(InspectionKind.Departure);
        if (editable.IsFailure)
        {
            return editable;
        }

        if (vehicleId == Guid.Empty)
        {
            return HandoverErrors.VehicleRequired;
        }

        if (lastKnownMileage is < 0 or > MaxMileage)
        {
            return HandoverErrors.MileageInvalid;
        }

        if (VehicleId == vehicleId)
        {
            ReferenceMileage = Math.Max(ReferenceMileage ?? 0, lastKnownMileage);
            Revision++;
            return Result.Success();
        }

        foreach (var photo in _photos.ToList())
        {
            Release(photo);
        }

        _photos.Clear();
        _damages.Clear();
        VoidSignatures();
        Mileage = null;
        FuelEighths = null;
        Accessories = null;
        VehicleId = vehicleId;
        ReferenceMileage = lastKnownMileage;
        CopyDamages(knownDamages, actor, at);
        Revision++;
        Raise(new InspectionVehicleConfirmed(Id, BookingId, vehicleId, _damages.Count));
        return Result.Success();
    }

    /// <summary>The readings: mileage (at least the reference), fuel in eighths, accessories.</summary>
    public Result RecordReadings(int mileage, int fuelEighths, AccessoryChecklist accessories)
    {
        ArgumentNullException.ThrowIfNull(accessories);
        var editable = EnsureDraftWithVehicle();
        if (editable.IsFailure)
        {
            return editable;
        }

        if (mileage is < 0 or > MaxMileage)
        {
            return HandoverErrors.MileageInvalid;
        }

        if (mileage < ReferenceMileage)
        {
            return HandoverErrors.MileageBelowReference;
        }

        if (fuelEighths is < 0 or > FullTank)
        {
            return HandoverErrors.FuelInvalid;
        }

        if (Mileage == mileage && FuelEighths == fuelEighths && Accessories == accessories)
        {
            return Result.Success();
        }

        Mileage = mileage;
        FuelEighths = fuelEighths;
        Accessories = accessories;
        ContentChanged();
        return Result.Success();
    }

    /// <summary>The originals seen at the counter (departure); each entry is a driver of the file.</summary>
    public Result RecordOriginals(IReadOnlyList<DriverOriginals> originals, IReadOnlyCollection<Guid> drivers)
    {
        ArgumentNullException.ThrowIfNull(originals);
        ArgumentNullException.ThrowIfNull(drivers);
        var editable = EnsureDraft(InspectionKind.Departure);
        if (editable.IsFailure)
        {
            return editable;
        }

        if (originals.Any(o => !drivers.Contains(o.PersonId)) || originals.Select(o => o.PersonId).Distinct().Count() != originals.Count)
        {
            return HandoverErrors.OriginalsUnknownDriver;
        }

        var stored = DriverOriginals.Format(originals);
        if (stored.Length > OriginalsMaxLength)
        {
            return HandoverErrors.OriginalsUnknownDriver;
        }

        OriginalsSelection = stored;
        Revision++;
        return Result.Success();
    }

    /// <summary>Whether the vehicle came back dirty (a cleaning fee is proposed).</summary>
    public Result SetCleaningNeeded(bool needsCleaning)
    {
        var editable = EnsureDraft(InspectionKind.Return);
        if (editable.IsFailure)
        {
            return editable;
        }

        if (NeedsCleaning != needsCleaning)
        {
            NeedsCleaning = needsCleaning;
            Revision++;
        }

        return Result.Success();
    }

    public Result SetNotes(string? notes)
    {
        var editable = EnsureDraft(null);
        if (editable.IsFailure)
        {
            return editable;
        }

        var text = Clean(notes);
        if (text is { Length: > NotesMaxLength })
        {
            return HandoverErrors.NotesTooLong;
        }

        if (text != Notes)
        {
            Notes = text;
            ContentChanged();
        }

        return Result.Success();
    }

    /// <summary>
    /// Adds a photo stored at <paramref name="image"/> (with its <paramref name="thumbnail"/>). A guided view holds one
    /// photo: a new one replaces it (retake). A damage photo may refer to a damage of this inspection.
    /// </summary>
    public Result<InspectionPhoto> AddPhoto(
        Guid photoId,
        PhotoSlot slot,
        HandoverFile image,
        HandoverFile? thumbnail,
        Guid? damageMarkId,
        BookingActor actor,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(actor);
        if (photoId == Guid.Empty)
        {
            throw new ArgumentException("A photo id is required.", nameof(photoId));
        }

        var editable = EnsureDraftWithVehicle();
        if (editable.IsFailure)
        {
            return editable.Error;
        }

        if (!Enum.IsDefined(slot))
        {
            return HandoverErrors.PhotoSlotInvalid;
        }

        if (!image.IsImage || thumbnail is { IsImage: false })
        {
            return HandoverErrors.ImageUnsupported;
        }

        if (damageMarkId is { } markId && _damages.All(d => d.Id != markId))
        {
            return HandoverErrors.DamageNotFound;
        }

        var replaced = slot.IsGuided() ? _photos.FirstOrDefault(p => p.Slot == slot) : null;
        if (replaced is null && _photos.Count >= MaxPhotos)
        {
            return HandoverErrors.TooManyPhotos;
        }

        if (replaced is not null)
        {
            Release(replaced);
            _photos.Remove(replaced);
        }

        var photo = new InspectionPhoto(photoId, Id, AgencyId, slot, slot == PhotoSlot.Damage ? damageMarkId : null, image, thumbnail, actor, at);
        _photos.Add(photo);
        ContentChanged();
        return photo;
    }

    public Result RemovePhoto(Guid photoId)
    {
        var editable = EnsureDraft(null);
        if (editable.IsFailure)
        {
            return editable;
        }

        var photo = _photos.FirstOrDefault(p => p.Id == photoId);
        if (photo is null)
        {
            return HandoverErrors.PhotoNotFound;
        }

        Release(photo);
        _photos.Remove(photo);
        ContentChanged();
        return Result.Success();
    }

    /// <summary>Marks a damage. At a departure every damage is pre-existing; at a return a damage is new unless said otherwise.</summary>
    public Result<DamageMark> AddDamage(DamageInput input, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actor);
        var editable = EnsureDraftWithVehicle();
        if (editable.IsFailure)
        {
            return editable.Error;
        }

        if (_damages.Count >= MaxDamages)
        {
            return HandoverErrors.TooManyDamages;
        }

        var valid = DamageMark.Validate(input);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var mark = new DamageMark(Entity.NewId(), Id, AgencyId, input with { Note = Clean(input.Note), IsPreExisting = Kind == InspectionKind.Departure || input.IsPreExisting }, null, actor, at);
        _damages.Add(mark);
        ContentChanged();
        return mark;
    }

    public Result UpdateDamage(Guid damageId, DamageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var editable = EnsureDraft(null);
        if (editable.IsFailure)
        {
            return editable;
        }

        var mark = _damages.FirstOrDefault(d => d.Id == damageId);
        if (mark is null)
        {
            return HandoverErrors.DamageNotFound;
        }

        var valid = DamageMark.Validate(input);
        if (valid.IsFailure)
        {
            return valid;
        }

        mark.Update(input with { Note = Clean(input.Note), IsPreExisting = Kind == InspectionKind.Departure || input.IsPreExisting });
        ContentChanged();
        return Result.Success();
    }

    /// <summary>Removes a damage (e.g. a pre-existing damage repaired since) and its photos.</summary>
    public Result RemoveDamage(Guid damageId)
    {
        var editable = EnsureDraft(null);
        if (editable.IsFailure)
        {
            return editable;
        }

        var mark = _damages.FirstOrDefault(d => d.Id == damageId);
        if (mark is null)
        {
            return HandoverErrors.DamageNotFound;
        }

        foreach (var photo in _photos.Where(p => p.DamageMarkId == damageId).ToList())
        {
            Release(photo);
            _photos.Remove(photo);
        }

        _damages.Remove(mark);
        ContentChanged();
        return Result.Success();
    }

    /// <summary>Records the drawn signature of <paramref name="role"/> (a PNG image), replacing a previous one; ends a signature on paper.</summary>
    public Result<InspectionSignature> Sign(
        Guid signatureId,
        SignatureRole role,
        string? signerName,
        HandoverFile image,
        BookingActor actor,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(actor);
        if (signatureId == Guid.Empty)
        {
            throw new ArgumentException("A signature id is required.", nameof(signatureId));
        }

        var editable = EnsureDraftWithVehicle();
        if (editable.IsFailure)
        {
            return editable.Error;
        }

        if (!Enum.IsDefined(role))
        {
            return HandoverErrors.SignaturesMissing;
        }

        if (image.ContentType != HandoverFile.Png)
        {
            return HandoverErrors.ImageUnsupported;
        }

        var name = Clean(signerName);
        if (name is null || name.Length > InspectionSignature.SignerNameMaxLength)
        {
            return HandoverErrors.SignerNameRequired;
        }

        if (_signatures.FirstOrDefault(s => s.Role == role) is { } previous)
        {
            _releasedFiles.Add(previous.StoragePath);
            _signatures.Remove(previous);
        }

        PaperSignature = null;
        var signature = new InspectionSignature(signatureId, Id, AgencyId, role, name, image, actor, at);
        _signatures.Add(signature);
        Revision++;
        return signature;
    }

    /// <summary>The accessible fallback: the contract is signed on paper, with the reason (the drawn signatures are dropped).</summary>
    public Result SignOnPaper(string? reason, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var editable = EnsureDraftWithVehicle();
        if (editable.IsFailure)
        {
            return editable;
        }

        var text = Clean(reason);
        if (text is not { Length: >= ReasonMinLength and <= ReasonMaxLength })
        {
            return HandoverErrors.PaperReasonRequired;
        }

        foreach (var signature in _signatures)
        {
            _releasedFiles.Add(signature.StoragePath);
        }

        _signatures.Clear();
        PaperSignature = new PaperSignature(text, actor.UserId, actor.Name, at);
        Revision++;
        return Result.Success();
    }

    /// <summary>Back to the signature on the tablet.</summary>
    public Result CancelPaperSignature()
    {
        var editable = EnsureDraft(null);
        if (editable.IsFailure)
        {
            return editable;
        }

        if (PaperSignature is not null)
        {
            PaperSignature = null;
            Revision++;
        }

        return Result.Success();
    }

    /// <summary>
    /// A manager lets the vehicle leave without the balance <paramref name="balanceDue"/> paid (deferred) and/or without
    /// a deposit (waived), with a reason (permission checked by the caller).
    /// </summary>
    public Result DeferPayment(Money balanceDue, bool deferBalance, bool waiveDeposit, string? reason, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var editable = EnsureDraft(InspectionKind.Departure);
        if (editable.IsFailure)
        {
            return editable;
        }

        if ((!deferBalance || !balanceDue.IsPositive) && !waiveDeposit)
        {
            return HandoverErrors.NothingToDefer;
        }

        var text = Clean(reason);
        if (text is not { Length: >= ReasonMinLength and <= ReasonMaxLength })
        {
            return HandoverErrors.ReasonRequired;
        }

        var deferred = deferBalance && balanceDue.IsPositive ? balanceDue : Money.Zero;
        Deferral = new DepartureDeferral(deferred, waiveDeposit, text, actor.UserId, actor.Name, at);
        Revision++;
        Raise(new DeparturePaymentDeferred(Id, BookingId, deferred, waiveDeposit));
        return Result.Success();
    }

    public Result CancelDeferral()
    {
        var editable = EnsureDraft(InspectionKind.Departure);
        if (editable.IsFailure)
        {
            return editable;
        }

        if (Deferral is not null)
        {
            Deferral = null;
            Revision++;
        }

        return Result.Success();
    }

    /// <summary>What still prevents the departure inspection from being completed (empty when it can be).</summary>
    public IReadOnlyList<Error> DepartureIssues(IReadOnlyCollection<Guid> drivers)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        var issues = new List<Error>();
        if (Kind != InspectionKind.Departure)
        {
            issues.Add(HandoverErrors.InspectionKindMismatch);
            return issues;
        }

        if (VehicleId is null)
        {
            issues.Add(HandoverErrors.VehicleRequired);
        }

        var originals = Originals;
        if (drivers.Count == 0 || drivers.Any(driver => originals.FirstOrDefault(o => o.PersonId == driver) is not { IsComplete: true }))
        {
            issues.Add(HandoverErrors.OriginalsIncomplete);
        }

        if (Mileage is null || FuelEighths is null || Accessories is null)
        {
            issues.Add(HandoverErrors.ReadingsMissing);
        }

        if (MissingGuidedPhotos.Count > 0)
        {
            issues.Add(HandoverErrors.PhotosMissing);
        }

        if (!IsSigned)
        {
            issues.Add(HandoverErrors.SignaturesMissing);
        }

        return issues;
    }

    /// <summary>
    /// Completes the departure inspection of <paramref name="vehicleId"/> (the vehicle of the file now) at
    /// <paramref name="handedOverAt"/>: originals of every driver seen, readings, the eight guided photos, both signatures
    /// or the signature on paper.
    /// </summary>
    public Result CompleteDeparture(Guid vehicleId, IReadOnlyCollection<Guid> drivers, DateTimeOffset handedOverAt, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(actor);
        var editable = EnsureDraft(InspectionKind.Departure);
        if (editable.IsFailure)
        {
            return editable;
        }

        if (VehicleId is { } confirmed && confirmed != vehicleId)
        {
            return HandoverErrors.VehicleChanged;
        }

        var issues = DepartureIssues(drivers);
        if (issues.Count > 0)
        {
            return issues[0];
        }

        Complete(handedOverAt, actor, at);
        return Result.Success();
    }

    /// <summary>
    /// Completes the return inspection at <paramref name="handedOverAt"/> with its final <paramref name="charges"/>
    /// (<see cref="ReturnCharges.Finalize"/>) and the <paramref name="franchise"/> they were capped at.
    /// </summary>
    public Result CompleteReturn(
        IReadOnlyList<ReturnChargeLine> charges,
        Money franchise,
        bool franchiseReduced,
        DateTimeOffset handedOverAt,
        BookingActor actor,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(charges);
        ArgumentNullException.ThrowIfNull(actor);
        var editable = EnsureDraft(InspectionKind.Return);
        if (editable.IsFailure)
        {
            return editable;
        }

        if (VehicleId is null)
        {
            return HandoverErrors.VehicleRequired;
        }

        if (Mileage is null || FuelEighths is null || Accessories is null)
        {
            return HandoverErrors.ReadingsMissing;
        }

        if (charges.Count > ReturnCharges.MaxLines || franchise.IsNegative)
        {
            return HandoverErrors.TooManyCharges;
        }

        Franchise = franchise;
        FranchiseReduced = franchiseReduced;
        ReplaceCharges(charges, actor, at);
        Complete(handedOverAt, actor, at);
        Raise(new ReturnChargesRecorded(Id, BookingId, ChargesTotal, _charges.Count, IsRevision: false));
        return Result.Success();
    }

    /// <summary>Replaces the charges of a completed return (before the closing of the file: checked by the caller).</summary>
    public Result ReviseCharges(IReadOnlyList<ReturnChargeLine> charges, BookingActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(charges);
        ArgumentNullException.ThrowIfNull(actor);
        if (Kind != InspectionKind.Return)
        {
            return HandoverErrors.InspectionKindMismatch;
        }

        if (Status != InspectionStatus.Completed)
        {
            return HandoverErrors.ChargesNotRevisable;
        }

        if (charges.Count > ReturnCharges.MaxLines)
        {
            return HandoverErrors.TooManyCharges;
        }

        ReplaceCharges(charges, actor, at);
        Revision++;
        Raise(new ReturnChargesRecorded(Id, BookingId, ChargesTotal, _charges.Count, IsRevision: true));
        return Result.Success();
    }

    /// <summary>The ids of the damages marked new at this return (the ones a repair charge can refer to).</summary>
    public IReadOnlyList<Guid> NewDamageIds => [.. _damages.Where(d => !d.IsPreExisting).Select(d => d.Id)];

    /// <summary>Forgets the released files once the application deleted them (or gave up).</summary>
    public IReadOnlyList<string> DequeueReleasedFiles()
    {
        var files = _releasedFiles.ToArray();
        _releasedFiles.Clear();
        return files;
    }

    private static void RequireIds(Guid agencyId, Guid bookingId)
    {
        if (agencyId == Guid.Empty || bookingId == Guid.Empty)
        {
            throw new ArgumentException("An inspection belongs to an agency and a rental file.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Result EnsureDraft(InspectionKind? kind)
    {
        if (kind is { } expected && Kind != expected)
        {
            return HandoverErrors.InspectionKindMismatch;
        }

        return Status == InspectionStatus.Draft ? Result.Success() : HandoverErrors.InspectionCompleted;
    }

    private Result EnsureDraftWithVehicle()
    {
        var editable = EnsureDraft(null);
        if (editable.IsFailure)
        {
            return editable;
        }

        return VehicleId is null ? HandoverErrors.VehicleRequired : Result.Success();
    }

    private void CopyDamages(IEnumerable<DamageMark> knownDamages, BookingActor actor, DateTimeOffset at)
    {
        foreach (var known in knownDamages.Take(MaxDamages))
        {
            var input = new DamageInput(known.View, known.X, known.Y, known.Type, known.Severity, known.Note, IsPreExisting: true);
            _damages.Add(new DamageMark(Entity.NewId(), Id, AgencyId, input, known.Id, actor, at));
        }
    }

    /// <summary>The content the signatures attest changed: they are voided (taken again).</summary>
    private void ContentChanged()
    {
        VoidSignatures();
        Revision++;
    }

    private void VoidSignatures()
    {
        foreach (var signature in _signatures)
        {
            _releasedFiles.Add(signature.StoragePath);
        }

        _signatures.Clear();
        PaperSignature = null;
    }

    private void Release(InspectionPhoto photo)
    {
        _releasedFiles.Add(photo.StoragePath);
        if (photo.ThumbnailPath is { } thumbnail)
        {
            _releasedFiles.Add(thumbnail);
        }
    }

    private void ReplaceCharges(IReadOnlyList<ReturnChargeLine> charges, BookingActor actor, DateTimeOffset at)
    {
        _charges.Clear();
        for (var position = 0; position < charges.Count; position++)
        {
            _charges.Add(new ReturnCharge(Id, AgencyId, position + 1, charges[position], actor, at));
        }
    }

    private void Complete(DateTimeOffset handedOverAt, BookingActor actor, DateTimeOffset at)
    {
        Status = InspectionStatus.Completed;
        HandedOverAtUtc = handedOverAt.ToUniversalTime();
        CompletedAtUtc = at.ToUniversalTime();
        CompletedByUserId = actor.UserId;
        CompletedBy = actor.Name;
        Revision++;
        Raise(new InspectionCompleted(
            Id,
            BookingId,
            Kind,
            VehicleId!.Value,
            Mileage!.Value,
            FuelEighths!.Value,
            _damages.Count(d => !d.IsPreExisting),
            HandedOverAtUtc.Value));
    }
}

/// <summary>A photo of an inspection, kept in the private container <c>inspections</c> with its thumbnail.</summary>
public sealed class InspectionPhoto : Entity, IAgencyOwned
{
    private InspectionPhoto()
    {
    }

    internal InspectionPhoto(
        Guid id,
        Guid inspectionId,
        Guid agencyId,
        PhotoSlot slot,
        Guid? damageMarkId,
        HandoverFile image,
        HandoverFile? thumbnail,
        BookingActor actor,
        DateTimeOffset at)
        : base(id)
    {
        InspectionId = inspectionId;
        AgencyId = agencyId;
        Slot = slot;
        DamageMarkId = damageMarkId;
        StoragePath = image.Path;
        ContentType = image.ContentType;
        ContentHash = image.ContentHash;
        Length = image.Length;
        ThumbnailPath = thumbnail?.Path;
        ThumbnailContentType = thumbnail?.ContentType;
        TakenByUserId = actor.UserId;
        TakenBy = actor.Name;
        TakenAtUtc = at.ToUniversalTime();
    }

    public Guid InspectionId { get; private set; }

    /// <summary>The agency of the inspection (given by the inspection).</summary>
    public Guid AgencyId { get; private set; }

    public PhotoSlot Slot { get; private set; }

    /// <summary>The damage a close-up shows.</summary>
    public Guid? DamageMarkId { get; private set; }

    public string StoragePath { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;

    /// <summary>Lower-case hexadecimal SHA-256 (entity tag of the stream).</summary>
    public string ContentHash { get; private set; } = null!;

    public long Length { get; private set; }

    public string? ThumbnailPath { get; private set; }

    public string? ThumbnailContentType { get; private set; }

    public Guid? TakenByUserId { get; private set; }

    public string TakenBy { get; private set; } = null!;

    public DateTimeOffset TakenAtUtc { get; private set; }
}

/// <summary>
/// A damage marked on the schematic of the vehicle: view, position in percent of the view (from its top-left corner),
/// type, severity and note; pre-existing (known before this rental) or new (found at its return).
/// </summary>
public sealed class DamageMark : Entity, IAgencyOwned
{
    public const int NoteMaxLength = 500;

    private DamageMark()
    {
    }

    internal DamageMark(Guid id, Guid inspectionId, Guid agencyId, DamageInput input, Guid? sourceMarkId, BookingActor actor, DateTimeOffset at)
        : base(id)
    {
        InspectionId = inspectionId;
        AgencyId = agencyId;
        SourceMarkId = sourceMarkId;
        RecordedByUserId = actor.UserId;
        RecordedBy = actor.Name;
        RecordedAtUtc = at.ToUniversalTime();
        Update(input);
    }

    public Guid InspectionId { get; private set; }

    /// <summary>The agency of the inspection (given by the inspection).</summary>
    public Guid AgencyId { get; private set; }

    public DamageView View { get; private set; }

    /// <summary>Horizontal position on the view, in percent (0 = left edge).</summary>
    public decimal X { get; private set; }

    /// <summary>Vertical position on the view, in percent (0 = top edge).</summary>
    public decimal Y { get; private set; }

    public DamageType Type { get; private set; }

    public DamageSeverity Severity { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Known before this rental (departure, or copied from the departure at the return): not charged.</summary>
    public bool IsPreExisting { get; private set; }

    /// <summary>The mark of the previous inspection this one was copied from.</summary>
    public Guid? SourceMarkId { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public string RecordedBy { get; private set; } = null!;

    public DateTimeOffset RecordedAtUtc { get; private set; }

    internal static Result Validate(DamageInput input)
    {
        if (!Enum.IsDefined(input.View) || !Enum.IsDefined(input.Type) || !Enum.IsDefined(input.Severity)
            || input.X is < 0m or > 100m || input.Y is < 0m or > 100m)
        {
            return HandoverErrors.DamageInvalid;
        }

        return input.Note is { Length: > 0 } note && note.Trim().Length > NoteMaxLength ? HandoverErrors.DamageNoteTooLong : Result.Success();
    }

    internal void Update(DamageInput input)
    {
        View = input.View;
        X = Math.Round(input.X, 2, MidpointRounding.AwayFromZero);
        Y = Math.Round(input.Y, 2, MidpointRounding.AwayFromZero);
        Type = input.Type;
        Severity = input.Severity;
        Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        IsPreExisting = input.IsPreExisting;
    }
}

/// <summary>A signature drawn on the tablet (PNG in the private container <c>inspections</c>).</summary>
public sealed class InspectionSignature : Entity, IAgencyOwned
{
    public const int SignerNameMaxLength = 120;

    private InspectionSignature()
    {
    }

    internal InspectionSignature(Guid id, Guid inspectionId, Guid agencyId, SignatureRole role, string signerName, HandoverFile image, BookingActor actor, DateTimeOffset at)
        : base(id)
    {
        InspectionId = inspectionId;
        AgencyId = agencyId;
        Role = role;
        SignerName = signerName;
        StoragePath = image.Path;
        ContentHash = image.ContentHash;
        Length = image.Length;
        RecordedByUserId = actor.UserId;
        RecordedBy = actor.Name;
        SignedAtUtc = at.ToUniversalTime();
    }

    public Guid InspectionId { get; private set; }

    /// <summary>The agency of the inspection (given by the inspection).</summary>
    public Guid AgencyId { get; private set; }

    public SignatureRole Role { get; private set; }

    public string SignerName { get; private set; } = null!;

    public string StoragePath { get; private set; } = null!;

    public string ContentHash { get; private set; } = null!;

    public long Length { get; private set; }

    /// <summary>The user of the tablet when the signature was recorded.</summary>
    public Guid? RecordedByUserId { get; private set; }

    public string RecordedBy { get; private set; } = null!;

    public DateTimeOffset SignedAtUtc { get; private set; }
}
