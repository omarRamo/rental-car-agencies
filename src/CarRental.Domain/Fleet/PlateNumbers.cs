using System.Text;
using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// Registration plate numbers, e.g. <c>215 TU 4512</c> ("TU" standing for تونس). Normalized form: trimmed, upper case,
/// single spaces; letters (Latin or Arabic), digits, spaces and dashes only.
/// </summary>
public static class PlateNumbers
{
    public static Result<string> Normalize(string? plateNumber)
    {
        if (string.IsNullOrWhiteSpace(plateNumber))
        {
            return FleetErrors.PlateNumberInvalid;
        }

        var builder = new StringBuilder(plateNumber.Length);
        var pendingSpace = false;
        foreach (var character in plateNumber.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (!char.IsLetterOrDigit(character) && character != '-')
            {
                return FleetErrors.PlateNumberInvalid;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        var normalized = builder.ToString();
        return normalized.Length is < Vehicle.PlateNumberMinLength or > Vehicle.PlateNumberMaxLength
            ? FleetErrors.PlateNumberInvalid
            : normalized;
    }
}
