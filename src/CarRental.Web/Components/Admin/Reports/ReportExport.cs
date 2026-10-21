using System.Globalization;
using System.Text;

namespace CarRental.Web.Components.Admin.Reports;

/// <summary>A table of the reports screen that has its Excel export, or <see cref="All"/> of them in one workbook.</summary>
public enum ReportTable
{
    All = 0,
    Daily = 1,
    Vehicles = 2,
    Channels = 3,
    Pace = 4,
    Funnel = 5,
    Requests = 6,
    Cancellations = 7,
    Cash = 8,
    Unpaid = 9,
}

/// <summary>
/// Addresses and file names of the Excel exports of the reports (ADR-0046): one workbook per table, or every table at
/// once, built on the server from the same indicators as the screen and downloaded from <see cref="RoutePrefix"/>.
/// </summary>
public static class ReportExport
{
    public const string RoutePrefix = "/admin/reports/export";

    public const string Route = RoutePrefix + "/{table}";

    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly Dictionary<string, ReportTable> BySegment =
        Enum.GetValues<ReportTable>().ToDictionary(Segment, t => t, StringComparer.Ordinal);

    /// <summary>The path segment of a table (<c>vehicles</c>, <c>all</c>…).</summary>
    public static string Segment(ReportTable table) => table.ToString().ToLowerInvariant();

    public static bool TryParse(string? segment, out ReportTable table) =>
        BySegment.TryGetValue(segment ?? string.Empty, out table);

    /// <summary>The download address of <paramref name="table"/> for an agency and a period (Tunis days, both included).</summary>
    public static string Url(ReportTable table, Guid agencyId, DateOnly from, DateOnly to) =>
        string.Create(CultureInfo.InvariantCulture, $"{RoutePrefix}/{Segment(table)}?agency={agencyId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    /// <summary>A day of an export address (<c>yyyy-MM-dd</c>).</summary>
    public static bool TryParseDay(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>
    /// The file name: <paramref name="prefix"/> and <paramref name="tableName"/> (localized), the agency and the period, in
    /// lower-case ASCII (e.g. <c>rapport-vehicules-agence-demo-tunis-2026-10-01-2026-10-31.xlsx</c>).
    /// </summary>
    public static string FileName(string prefix, string tableName, string agencyName, DateOnly from, DateOnly to) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Slug(prefix)}-{Slug(tableName)}-{Slug(agencyName)}-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx");

    /// <summary>Lower-case ASCII letters and digits separated by single hyphens; accents are dropped (é → e).</summary>
    public static string Slug(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        var hyphen = false;
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                if (hyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(c));
                hyphen = false;
            }
            else
            {
                hyphen = true;
            }

            if (builder.Length >= 40)
            {
                break;
            }
        }

        return builder.Length == 0 ? "export" : builder.ToString();
    }
}
