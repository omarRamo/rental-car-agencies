namespace CarRental.Application.Common.Files;

/// <summary>Blob containers used by the platform (all private). Names follow Azure rules: lower case letters, digits and dashes.</summary>
public static class FileContainers
{
    /// <summary>Identity documents and driving licences (access logged).</summary>
    public const string Documents = "documents";

    /// <summary>Hand-over and return photos, damage pictures.</summary>
    public const string Inspections = "inspections";

    /// <summary>Signed rental contracts (PDF).</summary>
    public const string Contracts = "contracts";

    /// <summary>Invoices and credit notes (PDF, TEIF XML).</summary>
    public const string Invoices = "invoices";

    /// <summary>Accounting and report exports.</summary>
    public const string Exports = "exports";

    public static IReadOnlyList<string> All { get; } = [Documents, Inspections, Contracts, Invoices, Exports];
}
