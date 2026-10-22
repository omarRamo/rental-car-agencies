using CarRental.Application.Accounting.Journals;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Accounting;
using CarRental.Domain.Common;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace CarRental.Web.Components.Admin.Accounting;

/// <summary>
/// Web parts of the expenses and the pre-accounting (ADR-0063 to ADR-0065): the labels of the journal entries and the Excel
/// exporter, the export of the journals of the agency of the session (<c>GET /admin/accounting/export?from=yyyy-MM&amp;to=yyyy-MM&amp;journal=&amp;format=csv|xlsx</c>,
/// <c>accounting.manage</c> and the accounting module, never cached) and the receipts of the expenses
/// (<c>GET /admin/expenses/{id}/receipt</c>, <c>expenses.manage</c> and the cash module), always with an MFA session.
/// </summary>
internal static class AccountingWeb
{
    public const string ExportRoute = "/admin/accounting/export";

    public static IServiceCollection AddAccountingWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IJournalTexts, JournalTexts>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJournalExporter, JournalWorkbook>());
        return services;
    }

    public static IEndpointRouteBuilder MapAccountingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(ExportRoute, ExportAsync)
            .RequireAuthorization(PermissionPolicy.For(Permissions.AccountingManage), ModulePolicy.For(AgencyModules.Accounting))
            .ExcludeFromDescription();
        endpoints.MapGet("/admin/expenses/{expenseId:guid}/receipt", GetReceiptAsync)
            .RequireAuthorization(PermissionPolicy.For(Permissions.ExpensesManage), ModulePolicy.For(AgencyModules.Cash))
            .ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>The address of an export of the journals of the agency of the session.</summary>
    public static string ExportUrl(AccountingMonth from, AccountingMonth to, Journal? journal, string format) =>
        $"{ExportRoute}?from={from}&to={to}&journal={(journal is { } j ? JournalCatalog.CodeOf(j) : "all")}&format={format}";

    /// <summary>A journal from its code (<c>VT</c>, <c>AC</c>…), none for <c>all</c>; false when unknown.</summary>
    public static bool TryParseJournal(string? code, out Journal? journal)
    {
        journal = null;
        if (string.IsNullOrEmpty(code) || string.Equals(code, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var candidate in JournalCatalog.All)
        {
            if (string.Equals(JournalCatalog.CodeOf(candidate), code, StringComparison.OrdinalIgnoreCase))
            {
                journal = candidate;
                return true;
            }
        }

        return false;
    }

    private static async Task<IResult> ExportAsync(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? journal,
        [FromQuery] string? format,
        [FromServices] JournalService journals,
        [FromServices] IUserContext user,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var start = AccountingMonth.Parse(from);
        var end = AccountingMonth.Parse(to ?? from);
        if (start.IsFailure || end.IsFailure || !TryParseJournal(journal, out var wanted))
        {
            return TypedResults.Problem(detail: "accounting.export.query_invalid", statusCode: StatusCodes.Status400BadRequest);
        }

        // The journals of the agency of the session only (ADR-0067): the module policy already requires one.
        if (user.AgencyId is not { } agencyId)
        {
            return TypedResults.Problem(detail: AccountingErrors.AgencyNotFound.Code, statusCode: StatusCodes.Status404NotFound);
        }

        var file = await journals.ExportAsync(agencyId, start.Value, end.Value, wanted, format ?? GenericCsvJournalExporter.FormatName, cancellationToken);
        if (file.IsFailure)
        {
            return TypedResults.Problem(
                detail: file.Error.Code,
                statusCode: file.Error.Kind switch
                {
                    ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                    ErrorKind.NotFound => StatusCodes.Status404NotFound,
                    ErrorKind.Conflict => StatusCodes.Status409Conflict,
                    ErrorKind.Validation => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError,
                });
        }

        context.Response.Headers.CacheControl = "no-store, private";
        return TypedResults.File(file.Value.Content, file.Value.ContentType, file.Value.FileName);
    }

    private static async Task<IResult> GetReceiptAsync(Guid expenseId, HttpContext context, ExpenseService expenses, CancellationToken cancellationToken)
    {
        var found = await expenses.FindReceiptAsync(expenseId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error.Kind == ErrorKind.Forbidden ? Results.Forbid() : Results.NotFound();
        }

        var file = found.Value;
        var entityTag = new EntityTagHeaderValue($"\"{file.ETag}\"");
        context.Response.Headers.CacheControl = "private, no-cache";
        context.Response.Headers.ETag = entityTag.ToString();
        if (context.Request.GetTypedHeaders().IfNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(entityTag, useStrongComparison: false)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var download = await expenses.OpenReceiptAsync(file, cancellationToken);
        if (download is null)
        {
            return Results.NotFound();
        }

        context.Response.RegisterForDisposeAsync(download);
        context.Response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("inline") { FileName = file.FileName }.ToString();
        return Results.Stream(download.Content, file.ContentType);
    }
}
