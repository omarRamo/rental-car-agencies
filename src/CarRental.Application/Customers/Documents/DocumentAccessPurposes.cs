namespace CarRental.Application.Customers.Documents;

/// <summary>
/// Why an identity document image is viewed: written as the reason of each row of the document access log
/// (who, when, which document, purpose). Stable codes; the back office shows a localized label.
/// </summary>
public static class DocumentAccessPurposes
{
    /// <summary>Default: consultation of the customer file.</summary>
    public const string CustomerFile = "customer_file";

    /// <summary>Checking the document against the original before marking it verified.</summary>
    public const string Verification = "verification";

    /// <summary>Validation of a booking request or a departure (checklist).</summary>
    public const string BookingCheck = "booking_check";

    /// <summary>Dispute, damage claim or traffic fine.</summary>
    public const string Dispute = "dispute";

    /// <summary>Request of the person (access right).</summary>
    public const string DataSubjectRequest = "data_subject_request";

    public static IReadOnlyList<string> All { get; } = [CustomerFile, Verification, BookingCheck, Dispute, DataSubjectRequest];

    /// <summary>The purpose code (the default for a blank value), or <see langword="null"/> when unknown.</summary>
    public static string? Normalize(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return CustomerFile;
        }

        var code = purpose.Trim();
        return All.Contains(code, StringComparer.Ordinal) ? code : null;
    }
}
