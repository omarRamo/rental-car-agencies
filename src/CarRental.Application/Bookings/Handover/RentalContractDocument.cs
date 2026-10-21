using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Email;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// Everything a rental contract shows (ADR-0041): the agency, the rental (planned and actual pick-up, places, days), the
/// drivers and their documents (numbers masked), the vehicle and its readings, the damages of the schematic, the frozen
/// quote lines and totals, the money situation, the deposit and franchise, the accessories, the terms reference and the
/// signatures. Rendered as a PDF by the web host (<see cref="IRentalContractRenderer"/>) and previewed on the screen.
/// </summary>
public sealed record RentalContractDocument(
    string Number,
    int Version,
    ContractLanguage Language,
    bool IsPreview,
    ContractAgency Agency,
    ContractRental Rental,
    IReadOnlyList<ContractDriver> Drivers,
    ContractVehicle Vehicle,
    ContractInspection Inspection,
    ContractPrice Price,
    ContractDeposit Deposit,
    ContractSignatures Signatures,
    string TermsReference,
    DateTimeOffset GeneratedAtUtc,
    string GeneratedBy);

public sealed record ContractAgency(string Name, string City, string? Address, string? Phone, string? Email);

public sealed record ContractRental(
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    DateTimeOffset? ActualPickupAt,
    string PickupLocation,
    string ReturnLocation,
    int Days,
    SalesChannel Channel,
    string? ChannelReference);

/// <summary>An identity document as printed on the contract: its number masked except the last characters.</summary>
public sealed record ContractDocument(IdentityDocumentTypeInfo Type, string MaskedNumber, string IssuingCountry, DateOnly? ExpiresOn);

public sealed record ContractDriver(
    BookingPartyRoleInfo Role,
    string FullName,
    DateOnly? BirthDate,
    string? Phone,
    string? Email,
    IReadOnlyList<ContractDocument> Documents);

public sealed record ContractVehicle(string PlateNumber, string Make, string Model, int Year, string CategoryCode, string CategoryName, FuelTypeInfo Fuel);

/// <summary>A damage of the schematic, numbered as on the drawing.</summary>
public sealed record ContractDamage(int Number, DamageView View, decimal X, decimal Y, DamageType Type, DamageSeverity Severity, string? Note);

public sealed record ContractInspection(int Mileage, int FuelEighths, AccessoryChecklist Accessories, IReadOnlyList<ContractDamage> Damages, string? Notes);

/// <summary>The frozen quote (lines and totals) and the money situation at the departure.</summary>
public sealed record ContractPrice(
    IReadOnlyList<QuoteLineView> Lines,
    Money TotalExclVat,
    Money TotalVat,
    Money TotalInclVat,
    Money NetPaid,
    Money BalanceDue,
    bool BalanceDeferred);

/// <summary>The deposit asked and held (and how), the franchise it caps the damages at, and whether a manager waived it.</summary>
public sealed record ContractDeposit(
    Money Required,
    Money Held,
    IReadOnlyList<DepositModeInfo> Modes,
    Money Franchise,
    bool FranchiseWaiverBought,
    bool Waived);

/// <summary>A drawn signature (PNG).</summary>
public sealed record ContractSignature(string SignerName, DateTimeOffset SignedAtUtc, ReadOnlyMemory<byte> Png);

/// <summary>The signatures of the customer and the agent, or the reason of the signature on paper.</summary>
public sealed record ContractSignatures(ContractSignature? Customer, ContractSignature? Agent, string? PaperReason);

/// <summary>Renders a rental contract as a PDF (implemented by the web host with its texts, QuestPDF).</summary>
public interface IRentalContractRenderer
{
    byte[] Render(RentalContractDocument document);
}

/// <summary>The e-mail carrying a contract to the main driver (the PDF is attached by reference by the caller).</summary>
public sealed record RentalContractEmail(
    string To,
    string? ToName,
    string Number,
    string AgencyName,
    DateTimeOffset PickupAt,
    DateTimeOffset ReturnAt,
    ContractLanguage Language);

/// <summary>Writes the localized contract e-mail (implemented by the web host with its resources).</summary>
public interface IRentalContractEmailFactory
{
    EmailMessage Create(RentalContractEmail email);
}

/// <summary>
/// The reference of the terms printed on the contract. TODO(spec): the final terms (franchise, deposit, fuel, late
/// return, leaving the territory, fines) are to be written with the lawyer of the agency (points-ouverts.md).
/// </summary>
public static class RentalContractTerms
{
    public const string Reference = "CG-LOC-2026-1";
}
