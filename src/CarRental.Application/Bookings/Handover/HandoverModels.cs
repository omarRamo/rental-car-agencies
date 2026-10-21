using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Handover;

/// <summary>The rental file as the hand-over screens show it.</summary>
public sealed record HandoverBookingView(
    Guid Id,
    string Number,
    Guid AgencyId,
    BookingStatus Status,
    SalesChannel Channel,
    string? ChannelReference,
    Guid CategoryId,
    string CategoryCode,
    string CategoryName,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    DateTimeOffset? ActualPickupAt,
    DateTimeOffset? ActualReturnAt,
    string PickupLocation,
    string ReturnLocation,
    Money TotalInclVat,
    Money Deposit,
    bool IncludesFranchiseWaiver,
    int QuoteDays,
    bool IsLate);

/// <summary>An identity document of a driver, its number masked (last characters only).</summary>
public sealed record HandoverDocumentView(
    IdentityDocumentTypeInfo Type,
    string MaskedNumber,
    string IssuingCountry,
    DateOnly? ExpiresOn,
    bool IsVerified,
    string? VerifiedBy,
    bool IsValidUntilReturn);

/// <summary>A driver of the file, with the documents to check at the counter and the eligibility for the rental.</summary>
public sealed record HandoverDriverView(
    Guid PersonId,
    BookingPartyRoleInfo Role,
    string FullName,
    string? Email,
    string? Phone,
    bool IsAnonymized,
    IReadOnlyList<HandoverDocumentView> Documents,
    DriverEligibilityInfo? Eligibility);

/// <summary>The vehicle of the file: identity, last known mileage and whether its documents cover the rental.</summary>
public sealed record HandoverVehicleView(
    Guid Id,
    string PlateNumber,
    string Make,
    string Model,
    int Year,
    int Mileage,
    FuelTypeInfo Fuel,
    DateTimeOffset? CompliantUntil,
    bool IsCompliant,
    bool IsComplianceWaived);

/// <summary>A photo of an inspection (streamed by the authorized endpoint).</summary>
public sealed record InspectionPhotoView(Guid Id, PhotoSlot Slot, Guid? DamageMarkId, DateTimeOffset TakenAtUtc, string TakenBy, bool HasThumbnail);

/// <summary>A damage of an inspection; <see cref="Number"/> is its number on the schematic (1, 2, 3…).</summary>
public sealed record DamageMarkView(
    Guid Id,
    int Number,
    DamageView View,
    decimal X,
    decimal Y,
    DamageType Type,
    DamageSeverity Severity,
    string? Note,
    bool IsPreExisting,
    Guid? SourceMarkId,
    DateTimeOffset RecordedAtUtc,
    string RecordedBy);

/// <summary>A drawn signature.</summary>
public sealed record SignatureView(Guid Id, SignatureRole Role, string SignerName, DateTimeOffset SignedAtUtc);

/// <summary>A return charge as shown and edited by the screens.</summary>
public sealed record ReturnChargeView(
    int Position,
    ReturnChargeKind Kind,
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
    string? RecordedBy,
    DateTimeOffset? RecordedAtUtc)
{
    public bool IsProposed => ProposedInclVat is not null;

    public bool IsWaived => ProposedInclVat is { IsPositive: true } && InclVat.IsZero;
}

/// <summary>An inspection (draft or completed) with its photos, damages, signatures and charges.</summary>
public sealed record InspectionView(
    Guid Id,
    InspectionKind Kind,
    InspectionStatus Status,
    Guid? VehicleId,
    int? ReferenceMileage,
    int? Mileage,
    int? FuelEighths,
    AccessoryChecklist? Accessories,
    IReadOnlyList<DriverOriginals> Originals,
    bool NeedsCleaning,
    string? Notes,
    IReadOnlyList<InspectionPhotoView> Photos,
    IReadOnlyList<DamageMarkView> Damages,
    IReadOnlyList<SignatureView> Signatures,
    PaperSignature? PaperSignature,
    DepartureDeferral? Deferral,
    IReadOnlyList<ReturnChargeView> Charges,
    Money? Franchise,
    bool FranchiseReduced,
    IReadOnlyList<PhotoSlot> MissingGuidedPhotos,
    DateTimeOffset StartedAtUtc,
    string StartedBy,
    DateTimeOffset? CompletedAtUtc,
    string? CompletedBy,
    DateTimeOffset? HandedOverAtUtc)
{
    public bool IsDraft => Status == InspectionStatus.Draft;

    public bool IsSigned => PaperSignature is not null
                            || (Signatures.Any(s => s.Role == SignatureRole.Customer) && Signatures.Any(s => s.Role == SignatureRole.Agent));

    public Money ChargesTotal => Charges.Select(c => c.InclVat).Sum();
}

/// <summary>Where the pre-existing damages of a departure come from: the last inspection of the vehicle.</summary>
public sealed record PreviousInspectionInfo(Guid InspectionId, Guid BookingId, InspectionKind Kind, DateTimeOffset CompletedAtUtc, int DamageCount);

/// <summary>
/// What the current user may do on the hand-over screens (the services check the same permissions).
/// <see cref="Counter"/> (<c>bookings.manage</c>) opens the counter steps — originals, contract and signatures, finish
/// of the departure, completion of the return —; without it (a preparer) only the inspection steps are shown (ADR-0041).
/// </summary>
public sealed record HandoverAbilities(
    bool Counter,
    bool ManagePayments,
    bool DeferPayment,
    bool AssignVehicle,
    bool WaiveCompliance,
    bool EditCharges,
    bool Close);

/// <summary>The departure screen: file, drivers, vehicle, draft inspection, money and what still blocks the departure.</summary>
public sealed record DepartureContext(
    HandoverBookingView Booking,
    IReadOnlyList<HandoverDriverView> Drivers,
    HandoverVehicleView? Vehicle,
    InspectionView? Inspection,
    bool VehicleChanged,
    PreviousInspectionInfo? Previous,
    BookingBalanceInfo Balance,
    IReadOnlyList<Error> Issues,
    HandoverAbilities Can,
    IReadOnlyList<RentalContractInfo> Contracts)
{
    /// <summary>The amount due before the departure (never negative).</summary>
    public Money AmountDue => Balance.Balance.IsPositive ? Balance.Balance : Money.Zero;

    public bool IsDone => Booking.Status is not BookingStatus.Confirmed;
}

/// <summary>The return screen: file, vehicle, departure and return inspections, money, deposits.</summary>
public sealed record ReturnContext(
    HandoverBookingView Booking,
    HandoverVehicleView? Vehicle,
    InspectionView? Departure,
    InspectionView? Inspection,
    BookingBalanceInfo Balance,
    IReadOnlyList<DepositInfo> Deposits,
    IReadOnlyList<AccessoryItem> MissingAccessories,
    HandoverAbilities Can)
{
    public bool IsReturned => Booking.Status is BookingStatus.Returned or BookingStatus.Closed;

    public bool IsClosed => Booking.Status == BookingStatus.Closed;
}

/// <summary>The agency's charge rules applied to a return (all VAT included).</summary>
public sealed record ReturnChargePolicyView(int IncludedKmPerDay, Money ExtraKmPrice, Money RefuelFeePerEighth, Money CleaningFee, decimal VatRate);

/// <summary>
/// The charges proposed for a return at <see cref="ActualReturnAt"/>: late days, charged days, the proposal, the
/// franchise, the rules, and the error of the pricing engine when the late days could not be priced.
/// </summary>
public sealed record ChargesPreview(
    DateTimeOffset ActualReturnAt,
    int LateDays,
    int ChargedDays,
    IReadOnlyList<ReturnChargeView> Proposal,
    Money ProposalTotal,
    Money Franchise,
    bool FranchiseReduced,
    ReturnChargePolicyView Policy,
    Error? PricingError);

/// <summary>The readings of an inspection: mileage, fuel in eighths and the accessories.</summary>
public sealed record ReadingsInput(
    int Mileage,
    int FuelEighths,
    bool SpareWheel,
    bool Jack,
    bool WarningTriangle,
    bool SafetyVest,
    int Keys,
    int ChildSeats,
    bool Gps,
    bool VehiclePapers);

/// <summary>A manager's deferral of the balance and/or waiver of the deposit, with its reason.</summary>
public sealed record DeferPaymentRequest(bool DeferBalance, bool WaiveDeposit, string Reason);

/// <summary>Finishes a departure: the actual pick-up (now by default) and the language of the contract.</summary>
public sealed record FinishDepartureRequest(Guid BookingId, DateTimeOffset? ActualPickupAt = null, ContractLanguage Language = ContractLanguage.French);

/// <summary>A finished departure: the contract and where it was e-mailed (none when the main driver has no address).</summary>
public sealed record DepartureOutcome(Guid ContractId, string Number, int Version, string? EmailedTo);

/// <summary>
/// Completes a return at <see cref="ActualReturnAt"/> with the charges edited by the agent (empty: the proposal as it is);
/// <see cref="ExpectedProposalTotal"/> is the total of the proposal the agent saw (a different proposal is refused).
/// </summary>
public sealed record CompleteReturnRequest(
    Guid BookingId,
    DateTimeOffset ActualReturnAt,
    IReadOnlyList<ReturnChargeInput> Charges,
    Money ExpectedProposalTotal);

/// <summary>A completed return: the total of its charges and the balance of the file now.</summary>
public sealed record ReturnOutcome(Money ChargesTotal, Money Balance);

/// <summary>A departure or a return of the day, with what is ready and what is not.</summary>
public sealed record HandoverDayItem(
    Guid BookingId,
    string Number,
    BookingStatus Status,
    DateTimeOffset At,
    string CategoryCode,
    string? PlateNumber,
    string CustomerName,
    string? Phone,
    bool IsDone,
    bool IsLate,
    HandoverReadiness Readiness,
    InspectionStatus? Inspection);

/// <summary>
/// Readiness of a hand-over: documents of the drivers, vehicle assigned and covered by its documents until the return,
/// amount due paid (or deferred), deposit held (or waived).
/// </summary>
public sealed record HandoverReadiness(
    DocumentsStateInfo Documents,
    bool VehicleAssigned,
    bool VehicleCompliant,
    Money AmountDue,
    bool DepositHeld,
    bool Deferred)
{
    public bool Paid => !AmountDue.IsPositive;

    public bool IsReady => Documents == DocumentsStateInfo.Complete && VehicleAssigned && VehicleCompliant && (Paid || Deferred) && (DepositHeld || Deferred);
}

/// <summary>The hand-overs of an agency on a Tunis day, and the rentals whose return is late.</summary>
public sealed record HandoverDay(
    Guid AgencyId,
    DateOnly Date,
    IReadOnlyList<HandoverDayItem> Departures,
    IReadOnlyList<HandoverDayItem> Returns,
    IReadOnlyList<HandoverDayItem> LateReturns);

/// <summary>The hand-overs of a file, as shown on its page: inspections, contracts, charges and balance.</summary>
public sealed record HandoverSummary(
    Guid BookingId,
    InspectionView? Departure,
    InspectionView? Return,
    IReadOnlyList<RentalContractInfo> Contracts,
    BookingBalanceInfo? Balance);

/// <summary>A file of a hand-over to stream: container, path, content type, entity tag and file name.</summary>
public sealed record HandoverMediaFile(string Container, string Path, string ContentType, string ETag, string FileName);
