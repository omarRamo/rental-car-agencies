using CarRental.Domain.Common;

namespace CarRental.Domain.Bookings.Handover;

/// <summary>
/// The rental contract of a file (SPEC §6 "contrat PDF signé à l'écran", ADR-0041): its number is the number of the file,
/// its version counts the contracts of the file (1 at the departure), the signed PDF lives in the private container
/// <c>contracts</c>, with the quote version and the departure inspection it was made from, when it was generated and
/// when it was queued for e-mail to the main driver.
/// </summary>
public sealed class RentalContract : AggregateRoot
{
    public const int NumberMaxLength = 24;
    public const int EmailMaxLength = 254;

    private RentalContract()
    {
    }

    private RentalContract(
        Guid agencyId,
        Guid bookingId,
        Guid inspectionId,
        string number,
        int version,
        Guid quoteId,
        int quoteVersion,
        ContractLanguage language,
        HandoverFile document,
        BookingActor actor,
        DateTimeOffset at)
        : base(NewId())
    {
        AgencyId = agencyId;
        BookingId = bookingId;
        InspectionId = inspectionId;
        Number = number;
        Version = version;
        QuoteId = quoteId;
        QuoteVersion = quoteVersion;
        Language = language;
        StoragePath = document.Path;
        ContentHash = document.ContentHash;
        Length = document.Length;
        GeneratedByUserId = actor.UserId;
        GeneratedBy = actor.Name;
        GeneratedAtUtc = at.ToUniversalTime();
    }

    public Guid AgencyId { get; private set; }

    public Guid BookingId { get; private set; }

    /// <summary>The departure inspection the contract describes.</summary>
    public Guid InspectionId { get; private set; }

    /// <summary>The number of the rental file (<c>LOC-YYYY-NNNNNN</c>).</summary>
    public string Number { get; private set; } = null!;

    /// <summary>Version of the contract within the file, from 1.</summary>
    public int Version { get; private set; }

    /// <summary>The frozen quote the prices of the contract come from.</summary>
    public Guid QuoteId { get; private set; }

    public int QuoteVersion { get; private set; }

    public ContractLanguage Language { get; private set; }

    /// <summary>Path of the PDF in the private container <c>contracts</c>.</summary>
    public string StoragePath { get; private set; } = null!;

    public string ContentHash { get; private set; } = null!;

    public long Length { get; private set; }

    public Guid? GeneratedByUserId { get; private set; }

    public string GeneratedBy { get; private set; } = null!;

    public DateTimeOffset GeneratedAtUtc { get; private set; }

    /// <summary>The e-mail address the contract was sent to (queued in the outbox), if any.</summary>
    public string? SentTo { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    /// <summary>The file name of the PDF, e.g. <c>LOC-2027-000123-v1.pdf</c>.</summary>
    public string FileName => $"{Number}-v{Version}.pdf";

    /// <summary>A contract of the file <paramref name="bookingId"/> stored at <paramref name="document"/> (a PDF).</summary>
    public static Result<RentalContract> Create(
        Guid agencyId,
        Guid bookingId,
        Guid inspectionId,
        string number,
        int version,
        Guid quoteId,
        int quoteVersion,
        ContractLanguage language,
        HandoverFile document,
        BookingActor actor,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(actor);
        if (agencyId == Guid.Empty || bookingId == Guid.Empty || inspectionId == Guid.Empty || quoteId == Guid.Empty)
        {
            throw new ArgumentException("A contract belongs to an agency, a rental file, its departure inspection and a quote.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(quoteVersion, 1);
        if (string.IsNullOrWhiteSpace(number) || number.Length > NumberMaxLength)
        {
            throw new ArgumentException($"A contract number has 1 to {NumberMaxLength} characters.", nameof(number));
        }

        if (!Enum.IsDefined(language))
        {
            throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown contract language.");
        }

        if (document.ContentType != HandoverFile.Pdf)
        {
            throw new ArgumentException("A rental contract is a PDF document.", nameof(document));
        }

        var contract = new RentalContract(agencyId, bookingId, inspectionId, number.Trim(), version, quoteId, quoteVersion, language, document, actor, at);
        contract.Raise(new RentalContractGenerated(contract.Id, bookingId, contract.Number, version));
        return contract;
    }

    /// <summary>The contract was queued for e-mail to <paramref name="email"/> (through the outbox, in the same transaction).</summary>
    public void MarkSent(string email, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var address = email.Trim();
        SentTo = address.Length > EmailMaxLength ? address[..EmailMaxLength] : address;
        SentAtUtc = at.ToUniversalTime();
        Raise(new RentalContractSent(Id, BookingId));
    }
}
