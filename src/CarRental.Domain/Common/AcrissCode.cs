namespace CarRental.Domain.Common;

/// <summary>
/// A four-letter ACRISS (SIPP) vehicle code, e.g. <c>ECMR</c>: Economy, 2/4 doors, Manual, petrol unspecified with air.
/// Each position is validated against the ACRISS expanded matrix.
/// </summary>
/// <remarks>
/// Positions: 1 category, 2 type, 3 transmission and drive, 4 fuel/power and air conditioning.
/// Source: ACRISS car classification codes (expanded matrix, including plug-in hybrid "I" and electric plus "C" with air).
/// </remarks>
public sealed class AcrissCode : IEquatable<AcrissCode>
{
    /// <summary>M Mini, N Mini Elite, E Economy, H Economy Elite, C Compact, D Compact Elite, I Intermediate, J Intermediate Elite,
    /// S Standard, R Standard Elite, F Fullsize, G Fullsize Elite, P Premium, U Premium Elite, L Luxury, W Luxury Elite, O Oversize, X Special.</summary>
    private const string Categories = "MNEHCDIJSRFGPULWOX";

    /// <summary>B 2-3 doors, C 2/4 doors, D 4-5 doors, W wagon, V passenger van, L limousine, S sport, T convertible, F SUV,
    /// J open air all terrain, X special, P pick-up 2 doors, Q pick-up 4 doors, Z special offer, E coupe, M monospace,
    /// R recreational, H motor home, Y two wheels, N roadster, G crossover, K commercial van/truck.</summary>
    private const string Types = "BCDWVLSTFJXPQZEMRHYNGK";

    /// <summary>M manual, N manual 4WD, C manual AWD, A automatic, B automatic 4WD, D automatic AWD.</summary>
    private const string Transmissions = "MNCABD";

    private const string AutomaticTransmissions = "ABD";

    /// <summary>R/N unspecified, D/Q diesel, H hybrid, I plug-in hybrid, E electric, C electric plus, L/S LPG,
    /// A/B hydrogen, M/F multi-fuel, V/Z petrol, U/X ethanol (first letter of each pair: with air conditioning).</summary>
    private const string FuelAndAirConditioning = "RNDQHIECLSABMFVZUX";

    private const string WithAirConditioning = "RDHIECLAMVU";

    private AcrissCode(string value) => Value = value;

    /// <summary>The normalized (upper-case) four-letter code.</summary>
    public string Value { get; }

    public char Category => Value[0];

    public char Type => Value[1];

    public char TransmissionAndDrive => Value[2];

    public char FuelAndAir => Value[3];

    public bool IsAutomatic => AutomaticTransmissions.Contains(Value[2], StringComparison.Ordinal);

    public bool HasAirConditioning => WithAirConditioning.Contains(Value[3], StringComparison.Ordinal);

    public static bool operator ==(AcrissCode? left, AcrissCode? right) => Equals(left, right);

    public static bool operator !=(AcrissCode? left, AcrissCode? right) => !Equals(left, right);

    /// <summary>Validates and normalizes a code (surrounding spaces and lower case accepted).</summary>
    public static Result<AcrissCode> Create(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return AcrissCodeErrors.Required;
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 4)
        {
            return AcrissCodeErrors.InvalidLength;
        }

        if (!Categories.Contains(normalized[0], StringComparison.Ordinal))
        {
            return AcrissCodeErrors.InvalidCategory;
        }

        if (!Types.Contains(normalized[1], StringComparison.Ordinal))
        {
            return AcrissCodeErrors.InvalidType;
        }

        if (!Transmissions.Contains(normalized[2], StringComparison.Ordinal))
        {
            return AcrissCodeErrors.InvalidTransmission;
        }

        if (!FuelAndAirConditioning.Contains(normalized[3], StringComparison.Ordinal))
        {
            return AcrissCodeErrors.InvalidFuelAndAir;
        }

        return new AcrissCode(normalized);
    }

    public bool Equals(AcrissCode? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is AcrissCode other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
