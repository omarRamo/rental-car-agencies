using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Reporting.Indicators;
using CarRental.Domain.Common;
using CarRental.Web.Resources.Admin.Reports;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Reports;

/// <summary>
/// Excel exports of the reports screen (ADR-0046): <c>GET /admin/reports/export/{table}?from=&amp;to=</c>, with an MFA
/// session, <c>reports.read</c> and the reports module on for the agency of the session, whose report it is (ADR-0067).
/// The workbook is built on the server from the same indicators as the screen (<see cref="ReportQueries"/>, which checks
/// the permission again), in the user's language, and sent as an attachment that no cache keeps (<c>no-store</c>).
/// </summary>
internal static class ReportExportEndpoints
{
    public static IEndpointRouteBuilder MapReportExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(ReportExport.Route, ExportAsync)
            .RequireAuthorization(PermissionPolicy.For(Permissions.ReportsRead), ModulePolicy.For(AgencyModules.Reports))
            .ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ExportAsync(
        string table,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromServices] ReportQueries queries,
        [FromServices] IUserContext user,
        [FromServices] IStringLocalizer<ReportsResources> localizer,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!ReportExport.TryParse(table, out var kind))
        {
            return TypedResults.NotFound();
        }

        if (!ReportExport.TryParseDay(from, out var start) || !ReportExport.TryParseDay(to, out var end))
        {
            return TypedResults.Problem(detail: "reporting.export.query_invalid", statusCode: StatusCodes.Status400BadRequest);
        }

        // The module policy guarantees an agency; the report is the one of the agency the session works for.
        if (user.AgencyId is not { } agencyId)
        {
            return TypedResults.NotFound();
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
