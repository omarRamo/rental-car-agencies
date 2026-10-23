using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>The details of a claim an agent enters or corrects.</summary>
public sealed record ClaimDetails(
    DateTimeOffset AccidentAt,
    string? Place,
    string? Description,
    string? Insurer,
    string? InsurerClaimNumber,
    decimal Franchise);

/// <summary>A cost of a claim (repair, towing, expertise…) or a reimbursement (insurer, third party): date, label, amount, reference.</summary>
public sealed record ClaimEntryInput(DateOnly Date, string? Label, decimal Amount, string? Reference = null);

/// <summary>A file stored for a claim (accident report, photo, other): kind, original name, path, type, size and hash.</summary>
public sealed record ClaimFileInput(ClaimDocumentKind Kind, string FileName, string StoragePath, string ContentType, long Length, string ContentHash);

/// <summary>
/// A claim (sinistre), aggregate of the Billing module: the accident of a vehicle of the agency, attached to the rental
/// file when there is one — circumstances, accident report and photos, insurer and its claim number, the franchise of the
/// insurance policy, the costs and the reimbursements. Its net cost (costs − reimbursements) enters the profitability of
/// the vehicle (Reporting source <c>claims</c>). Closed, it does not change any more.
/// </summary>
/// <remarks>
/// The customer's share of the damages is not a reimbursement here: it is invoiced with the return charges (damage lines,
/// capped at the franchise of the rental) and counted as revenue. A repair recorded on the claim is not recorded again as
/// a cost of the vehicle in the Fleet module (it would count twice in the margin).
/// </remarks>
public sealed class Claim : AggregateRoot, IAgencyOwned
{
    public const int PlaceMaxLength = 200;
    public const int DescriptionMinLength = 3;
    public const int DescriptionMaxLength = 2000;
    public const int InsurerMaxLength = 120;
    public const int ClaimNumberMaxLength = 60;
    public const int BookingNumberMaxLength = 32;
    public const int LabelMaxLength = 200;
    public const int ReferenceMaxLength = 100;
    public const int FileNameMaxLength = 200;
    public const int PathMaxLength = 300;
    public const int ContentTypeMaxLength = 64;
    public const int HashLength = 64;
    public const int MaxEntries = 50;
    public const int MaxFiles = 40;
    public const decimal MaxAmount = 1_000_000m;

    private readonly List<ClaimCost> _costs = [];
    private readonly List<ClaimReimbursement> _reimbursements = [];
    private readonly List<ClaimDocument> _documents = [];

    private Claim()
    {
    }

    private Claim(Guid id, Guid agencyId, Guid vehicleId, Guid? bookingId, string? bookingNumber, BillingActor actor, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        VehicleId = vehicleId;
        BookingId = bookingId;
        BookingNumber = bookingNumber;
        Status = ClaimStatus.Open;
        OpenedAtUtc = now.ToUniversalTime();
        OpenedByUserId = actor.UserId;
        OpenedBy = actor.Name;
        LastChangedAtUtc = OpenedAtUtc;
        LastChangedBy = actor.Name;
    }

    public Guid AgencyId { get; private set; }

    public Guid VehicleId { get; private set; }

    /// <summary>The rental file during which the accident happened, when any.</summary>
    public Guid? BookingId { get; private set; }

    public string? BookingNumber { get; private set; }

    public DateTimeOffset AccidentAtUtc { get; private set; }

    public string? Place { get; private set; }

    public string Description { get; private set; } = null!;

    public string? Insurer { get; private set; }

    /// <summary>The number of the claim at the insurer.</summary>
    public string? InsurerClaimNumber { get; private set; }

    /// <summary>The franchise of the insurance policy (what the insurer does not pay).</summary>
    public Money Franchise { get; private set; }

    public ClaimStatus Status { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private set; }

    public Guid? OpenedByUserId { get; private set; }

    public string OpenedBy { get; private set; } = null!;

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    public string LastChangedBy { get; private set; } = null!;

    /// <summary>Technical counter bumped by every change, so that the row version guards the children too.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<ClaimCost> Costs => [.. _costs.OrderBy(c => c.Date).ThenBy(c => c.RecordedAtUtc)];

    public IReadOnlyList<ClaimReimbursement> Reimbursements => [.. _reimbursements.OrderBy(r => r.Date).ThenBy(r => r.RecordedAtUtc)];

    public IReadOnlyList<ClaimDocument> Documents => [.. _documents.OrderBy(d => d.UploadedAtUtc)];

    public Money TotalCosts => _costs.Select(c => c.Amount).Sum();

    public Money TotalReimbursements => _reimbursements.Select(r => r.Amount).Sum();

    /// <summary>What the claim costs the agency: costs − reimbursements (negative when reimbursed beyond the costs).</summary>
    public Money NetCost => TotalCosts - TotalReimbursements;

    /// <summary>Opens a claim for a vehicle (and the file during which the accident happened, when any).</summary>
    public static Result<Claim> Open(
        Guid agencyId,
        Guid vehicleId,
        Guid? bookingId,
        string? bookingNumber,
        ClaimDetails details,
        BillingActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(actor);
        if (agencyId == Guid.Empty || vehicleId == Guid.Empty)
        {
            return BillingErrors.ClaimVehicleRequired;
        }

        if (bookingNumber is { Length: > BookingNumberMaxLength })
        {
            return BillingErrors.ClaimFieldTooLong;
        }

        var claim = new Claim(Entity.NewId(), agencyId, vehicleId, bookingId, bookingId is null ? null : bookingNumber, actor, now);
        var applied = claim.Apply(details, now);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        claim.Raise(new ClaimOpened(claim.Id, agencyId, vehicleId, bookingId));
        return claim;
    }

    /// <summary>Corrects the details (circumstances, insurer, number, franchise).</summary>
    public Result UpdateDetails(ClaimDetails details, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(actor);
        if (Status == ClaimStatus.Closed)
        {
            return BillingErrors.ClaimClosed;
        }

        var applied = Apply(details, now);
        return applied.IsFailure ? applied : Touch(actor, now);
    }

    public Result<Guid> AddCost(ClaimEntryInput input, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actor);
        var checkedEntry = CheckEntry(input, _costs.Count);
        if (checkedEntry.IsFailure)
        {
            return checkedEntry.Error;
        }

        var (label, amount, reference) = checkedEntry.Value;
        var cost = new ClaimCost(Id, AgencyId, input.Date, label, amount, reference, actor, now);
        _costs.Add(cost);
        var touched = Touch(actor, now);
        return touched.IsFailure ? touched.Error : cost.Id;
    }

    public Result RemoveCost(Guid costId, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Status == ClaimStatus.Closed)
        {
            return BillingErrors.ClaimClosed;
        }

        var cost = _costs.FirstOrDefault(c => c.Id == costId);
        if (cost is null)
        {
            return BillingErrors.ClaimEntryNotFound;
        }

        _costs.Remove(cost);
        return Touch(actor, now);
    }

    public Result<Guid> AddReimbursement(ClaimEntryInput input, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actor);
        var checkedEntry = CheckEntry(input, _reimbursements.Count);
        if (checkedEntry.IsFailure)
        {
            return checkedEntry.Error;
        }

        var (label, amount, reference) = checkedEntry.Value;
        var reimbursement = new ClaimReimbursement(Id, AgencyId, input.Date, label, amount, reference, actor, now);
        _reimbursements.Add(reimbursement);
        var touched = Touch(actor, now);
        return touched.IsFailure ? touched.Error : reimbursement.Id;
    }

    public Result RemoveReimbursement(Guid reimbursementId, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Status == ClaimStatus.Closed)
        {
            return BillingErrors.ClaimClosed;
        }

        var reimbursement = _reimbursements.FirstOrDefault(r => r.Id == reimbursementId);
        if (reimbursement is null)
        {
            return BillingErrors.ClaimEntryNotFound;
        }

        _reimbursements.Remove(reimbursement);
        return Touch(actor, now);
    }

    /// <summary>Adds a stored file (the blob is written before the transaction and deleted if it fails).</summary>
    public Result<Guid> AddDocument(ClaimFileInput file, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(actor);
        if (Status == ClaimStatus.Closed)
        {
            return BillingErrors.ClaimClosed;
        }

        if (_documents.Count >= MaxFiles)
        {
            return BillingErrors.ClaimTooManyEntries;
        }

        var name = string.IsNullOrWhiteSpace(file.FileName) ? null : file.FileName.Trim();
        if (!Enum.IsDefined(file.Kind)
            || name is null
            || string.IsNullOrWhiteSpace(file.StoragePath) || file.StoragePath.Length > PathMaxLength
            || string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType.Length > ContentTypeMaxLength
            || file.ContentHash is not { Length: HashLength }
            || file.Length <= 0)
        {
            return BillingErrors.ClaimFileInvalid;
        }

        var document = new ClaimDocument(Id, AgencyId, file.Kind, name.Length > FileNameMaxLength ? name[..FileNameMaxLength] : name, file.StoragePath, file.ContentType, file.Length, file.ContentHash, actor, now);
        _documents.Add(document);
        var touched = Touch(actor, now);
        return touched.IsFailure ? touched.Error : document.Id;
    }

    /// <summary>Removes a file; its blob is deleted after the commit (<see cref="ClaimFileDiscarded"/>).</summary>
    public Result RemoveDocument(Guid documentId, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Status == ClaimStatus.Closed)
        {
            return BillingErrors.ClaimClosed;
        }

        var document = _documents.FirstOrDefault(d => d.Id == documentId);
        if (document is null)
        {
            return BillingErrors.ClaimEntryNotFound;
        }

        _documents.Remove(document);
        Raise(new ClaimFileDiscarded(Id, document.StoragePath));
        return Touch(actor, now);
    }

    /// <summary>Declared to the insurer: waiting for its decision or its payment.</summary>
    public Result SendToInsurer(BillingActor actor, DateTimeOffset now) =>
        Status == ClaimStatus.Open ? ChangeStatus(ClaimStatus.AtInsurer, actor, now) : BillingErrors.ClaimInvalidTransition;

    /// <summary>Back to the agency (the insurer asks for more, or refuses).</summary>
    public Result Reopen(BillingActor actor, DateTimeOffset now) =>
        Status == ClaimStatus.AtInsurer ? ChangeStatus(ClaimStatus.Open, actor, now) : BillingErrors.ClaimInvalidTransition;

    /// <summary>Closed: nothing changes any more.</summary>
    public Result Close(BillingActor actor, DateTimeOffset now)
    {
        if (Status == ClaimStatus.Closed)
        {
            return BillingErrors.ClaimInvalidTransition;
        }

        ClosedAtUtc = now.ToUniversalTime();
        return ChangeStatus(ClaimStatus.Closed, actor, now);
    }

    private Result Apply(ClaimDetails details, DateTimeOffset now)
    {
        var description = Clean(details.Description);
        if (description is not { Length: >= DescriptionMinLength and <= DescriptionMaxLength })
        {
            return BillingErrors.ClaimDescriptionRequired;
        }

        if (details.AccidentAt > now)
        {
            return BillingErrors.ClaimAccidentInFuture;
        }

        var place = Clean(details.Place);
        var insurer = Clean(details.Insurer);
        var number = Clean(details.InsurerClaimNumber);
        if (place is { Length: > PlaceMaxLength } || insurer is { Length: > InsurerMaxLength } || number is { Length: > ClaimNumberMaxLength })
        {
            return BillingErrors.ClaimFieldTooLong;
        }

        if (details.Franchise is < 0m or > MaxAmount || Money.FromExact(details.Franchise, Currency.Tnd) is not { IsSuccess: true } franchise)
        {
            return BillingErrors.ClaimAmountInvalid;
        }

        AccidentAtUtc = details.AccidentAt.ToUniversalTime();
        Place = place;
        Description = description;
        Insurer = insurer;
        InsurerClaimNumber = number;
        Franchise = franchise.Value;
        return Result.Success();
    }

    private Result<(string Label, Money Amount, string? Reference)> CheckEntry(ClaimEntryInput input, int count)
    {
        if (Status == ClaimStatus.Closed)
        {
            return BillingErrors.ClaimClosed;
        }

        if (count >= MaxEntries)
        {
            return BillingErrors.ClaimTooManyEntries;
        }

        var label = Clean(input.Label);
        if (label is not { Length: <= LabelMaxLength })
        {
            return BillingErrors.ClaimLabelRequired;
        }

        var reference = Clean(input.Reference);
        if (reference is { Length: > ReferenceMaxLength })
        {
            return BillingErrors.ClaimFieldTooLong;
        }

        if (input.Amount is <= 0m or > MaxAmount || Money.FromExact(input.Amount, Currency.Tnd) is not { IsSuccess: true } amount)
        {
            return BillingErrors.ClaimAmountInvalid;
        }

        return (label, amount.Value, reference);
    }

    private Result ChangeStatus(ClaimStatus status, BillingActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var previous = Status;
        Status = status;
        Revision++;
        LastChangedAtUtc = now.ToUniversalTime();
        LastChangedBy = actor.Name;
        Raise(new ClaimStatusChanged(Id, AgencyId, previous, status));
        return Result.Success();
    }

    private Result Touch(BillingActor actor, DateTimeOffset now)
    {
        Revision++;
        LastChangedAtUtc = now.ToUniversalTime();
        LastChangedBy = actor.Name;
        Raise(new ClaimUpdated(Id, AgencyId, VehicleId));
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A cost of a claim: date, label, amount (as paid), reference (invoice of the garage…).</summary>
public sealed class ClaimCost : Entity, IAgencyOwned
{
    private ClaimCost()
    {
    }

    internal ClaimCost(Guid claimId, Guid agencyId, DateOnly date, string label, Money amount, string? reference, BillingActor actor, DateTimeOffset now)
        : base(NewId())
    {
        ClaimId = claimId;
        AgencyId = agencyId;
        Date = date;
        Label = label;
        Amount = amount;
        Reference = reference;
        RecordedAtUtc = now.ToUniversalTime();
        RecordedBy = actor.Name;
    }

    public Guid ClaimId { get; private set; }

    /// <summary>The agency of the claim.</summary>
    public Guid AgencyId { get; private set; }

    public DateOnly Date { get; private set; }

    public string Label { get; private set; } = null!;

    public Money Amount { get; private set; }

    public string? Reference { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public string RecordedBy { get; private set; } = null!;
}

/// <summary>A reimbursement of a claim (insurer, third party): date, label, amount, reference.</summary>
public sealed class ClaimReimbursement : Entity, IAgencyOwned
{
    private ClaimReimbursement()
    {
    }

    internal ClaimReimbursement(Guid claimId, Guid agencyId, DateOnly date, string label, Money amount, string? reference, BillingActor actor, DateTimeOffset now)
        : base(NewId())
    {
        ClaimId = claimId;
        AgencyId = agencyId;
        Date = date;
        Label = label;
        Amount = amount;
        Reference = reference;
        RecordedAtUtc = now.ToUniversalTime();
        RecordedBy = actor.Name;
    }

    public Guid ClaimId { get; private set; }

    /// <summary>The agency of the claim.</summary>
    public Guid AgencyId { get; private set; }

    public DateOnly Date { get; private set; }

    public string Label { get; private set; } = null!;

    public Money Amount { get; private set; }

    public string? Reference { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public string RecordedBy { get; private set; } = null!;
}

/// <summary>A file of a claim in the private storage (accident report, photo, other document).</summary>
public sealed class ClaimDocument : Entity, IAgencyOwned
{
    private ClaimDocument()
    {
    }

    internal ClaimDocument(
        Guid claimId,
        Guid agencyId,
        ClaimDocumentKind kind,
        string fileName,
        string storagePath,
        string contentType,
        long length,
        string contentHash,
        BillingActor actor,
        DateTimeOffset now)
        : base(NewId())
    {
        ClaimId = claimId;
        AgencyId = agencyId;
        Kind = kind;
        FileName = fileName;
        StoragePath = storagePath;
        ContentType = contentType;
        Length = length;
        ContentHash = contentHash;
        UploadedAtUtc = now.ToUniversalTime();
        UploadedBy = actor.Name;
    }

    public Guid ClaimId { get; private set; }

    /// <summary>The agency of the claim.</summary>
    public Guid AgencyId { get; private set; }

    public ClaimDocumentKind Kind { get; private set; }

    /// <summary>The name given by the agent's device (shown, never used as a path).</summary>
    public string FileName { get; private set; } = null!;

    public string StoragePath { get; private set; } = null!;

    public string ContentType { get; private set; } = null!;

    public long Length { get; private set; }

    /// <summary>SHA-256 of the content, hexadecimal (also the ETag of the stream).</summary>
    public string ContentHash { get; private set; } = null!;

    public DateTimeOffset UploadedAtUtc { get; private set; }

    public string UploadedBy { get; private set; } = null!;
}
