using CarRental.Application.Identity.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using CarRental.Web.Resources.Admin.Reports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Reports;

/// <summary>
/// Excel exports of the reports screen (ADR-0046): <c>GET /admin/reports/export/{table}?agency=&amp;from=&amp;to=</c>, with
/// an MFA session and <c>reports.read</c>. The workbook is built on the server from the same indicators as the screen
/// (<see cref="ReportQueries"/>, which checks the permission again), in the user's language, and sent as an attachment
/// that no cache keeps (<c>no-store</c>).
/// </summary>
internal static class ReportExportEndpoints
{
    public static IEndpointRouteBuilder MapReportExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(ReportExport.Route, ExportAsync)
            .RequireAuthorization(PermissionPolicy.For(Permissions.ReportsRead))
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        string table,
        [FromQuery] Guid? agency,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromServices] ReportQueries queries,
        [FromServices] IStringLocalizer<ReportsResources> localizer,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!ReportExport.TryParse(table, out var kind))
        {
            return TypedResults.NotFound();
        }

        if (agency is not { } agencyId || !ReportExport.TryParseDay(from, out var start) || !ReportExport.TryParseDay(to, out var end))
        {
            return TypedResults.Problem(detail: "reporting.export.query_invalid", statusCode: StatusCodes.Status400BadRequest);
        }

        var report = await queries.GetDashboardAsync(new ReportRequest(agencyId, start, end), cancellationToken);
        if (report.IsFailure)
        {
            return Problem(report.Error);
        }

        var content = ReportWorkbook.Build(report.Value, kind, localizer);
        var fileName = ReportExport.FileName(localizer["Export.FilePrefix"], localizer[$"Export.File.{kind}"], report.Value.AgencyName, start, end);
        context.Response.Headers.CacheControl = "no-store, private";
        return TypedResults.File(content, ReportExport.ContentType, fileName);
    }

    /// <summary>A business error as a status code; the detail is the stable error code only.</summary>
    private static ProblemHttpResult Problem(Error error) => TypedResults.Problem(
        detail: error.Code,
        statusCode: error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError,
        });
}
