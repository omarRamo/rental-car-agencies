using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// A document of a vehicle (insurance policy, vignette, technical inspection, registration card). Recording a new
/// document of a type supersedes the current one, which stays in the renewal history.
/// </summary>
public sealed class VehicleDocument : Entity, IAgencyOwned
{
    public const int NumberMaxLength = 60;
    public const int ProviderMaxLength = 120;
    public const int NotesMaxLength = 500;

    private VehicleDocument()
    {
    }

    private VehicleDocument(Guid id, Vehicle vehicle, VehicleDocumentDetails details, DateTimeOffset recordedAt)
        : base(id)
    {
        VehicleId = vehicle.Id;
        AgencyId = vehicle.AgencyId;
        Type = details.Type;
        Apply(details);
        IsCurrent = true;
        RecordedAtUtc = recordedAt;
    }

    public Guid VehicleId { get; private set; }

    /// <summary>The agency of the vehicle (ADR-0067).</summary>
    public Guid AgencyId { get; private set; }

    public VehicleDocumentType Type { get; private set; }

    public string Number { get; private set; } = null!;

    public DateOnly? IssuedOn { get; private set; }

    /// <summary>Last day of validity (Tunis calendar date), included.</summary>
    public DateOnly? ExpiresOn { get; private set; }

    /// <summary>Insurer, administration or inspection centre.</summary>
    public string? Provider { get; private set; }

    /// <summary>Price paid (premium, vignette, inspection fee), VAT included, for the profitability reports.</summary>
    public Money? Cost { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>The document in force for its type (the latest recorded one).</summary>
    public bool IsCurrent { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public DateTimeOffset? SupersededAtUtc { get; private set; }

    /// <summary>End (exclusive, UTC) of the validity: the end of the Tunis day of <see cref="ExpiresOn"/>.</summary>
    public DateTimeOffset? ValidUntil => ExpiresOn is { } expiresOn ? VehicleCompliance.EndOfTunisDay(expiresOn) : null;

    internal static Result<VehicleDocumentDetails> Validate(VehicleDocumentDetails details)
    {
        if (!Enum.IsDefined(details.Type))
        {
            return FleetErrors.DocumentTypeInvalid;
        }

        var normalized = details with
        {
            Number = details.Number?.Trim() ?? string.Empty,
            Provider = string.IsNullOrWhiteSpace(details.Provider) ? null : details.Provider.Trim(),
            Notes = string.IsNullOrWhiteSpace(details.Notes) ? null : details.Notes.Trim(),
        };

        if (normalized.Number.Length is 0 or > NumberMaxLength)
        {
            return FleetErrors.DocumentNumberInvalid;
        }

        if (VehicleCompliance.IsMandatory(normalized.Type) && normalized.ExpiresOn is null)
        {
            return FleetErrors.DocumentExpiryRequired;
        }

        if (normalized.IssuedOn is { } issued && normalized.ExpiresOn is { } expires && expires < issued)
        {
            return FleetErrors.DocumentDatesInvalid;
        }

        if (normalized.Provider?.Length > ProviderMaxLength || normalized.Notes?.Length > NotesMaxLength)
        {
            return FleetErrors.DocumentTextTooLong;
        }

        if (!FleetAmounts.IsValid(normalized.Cost))
        {
            return FleetErrors.AmountInvalid;
        }

        return normalized;
    }

    internal static VehicleDocument Create(Vehicle vehicle, VehicleDocumentDetails validated, DateTimeOffset recordedAt) =>
        new(NewId(), vehicle, validated, recordedAt);

    internal void Correct(VehicleDocumentDetails validated) => Apply(validated);

    internal void Supersede(DateTimeOffset at)
    {
        IsCurrent = false;
        SupersededAtUtc = at;
    }

    private void Apply(VehicleDocumentDetails details)
    {
        Number = details.Number;
        IssuedOn = details.IssuedOn;
        ExpiresOn = details.ExpiresOn;
        Provider = details.Provider;
        Cost = details.Cost;
        Notes = details.Notes;
    }
}

/// <summary>Data of a document. <paramref name="ExpiresOn"/> is required for the mandatory types.</summary>
public sealed record VehicleDocumentDetails(
    VehicleDocumentType Type,
    string Number,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    string? Provider = null,
    Money? Cost = null,
    string? Notes = null);
