namespace CarRental.Domain.Common;

public static class AcrissCodeErrors
{
    public static readonly Error Required = Error.Validation("acriss.required", "An ACRISS code is required.");

    public static readonly Error InvalidLength = Error.Validation("acriss.invalid_length", "An ACRISS code has exactly four letters.");

    public static readonly Error InvalidCategory = Error.Validation("acriss.invalid_category", "The first letter is not an ACRISS category.");

    public static readonly Error InvalidType = Error.Validation("acriss.invalid_type", "The second letter is not an ACRISS vehicle type.");

    public static readonly Error InvalidTransmission = Error.Validation(
        "acriss.invalid_transmission",
        "The third letter is not an ACRISS transmission/drive code.");

    public static readonly Error InvalidFuelAndAir = Error.Validation(
        "acriss.invalid_fuel_air",
        "The fourth letter is not an ACRISS fuel/air-conditioning code.");
}
