namespace CarRental.Domain.Common;

public static class DocumentNumberErrors
{
    public static readonly Error UnknownType = Error.Validation("document_number.unknown_type", "Unknown document type.");

    public static readonly Error InvalidYear = Error.Validation("document_number.invalid_year", "The fiscal year must have four digits.");

    public static readonly Error InvalidSequence = Error.Validation("document_number.invalid_sequence", "The sequence starts at 1.");

    public static readonly Error InvalidFormat = Error.Validation(
        "document_number.invalid_format",
        "Expected a document number such as FAC-2027-000045.");
}
