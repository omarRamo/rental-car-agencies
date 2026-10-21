using CarRental.Application.Cash.Expenses;
using CarRental.Domain.Cash.Expenses;
using CarRental.Domain.Common;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Components.Forms;

namespace CarRental.Web.Components.Admin.Expenses;

/// <summary>What the expense dialog returns: the expense saved and, when its receipt could not be stored, why.</summary>
public sealed record ExpenseSaved(Guid ExpenseId, string? ReceiptError);

/// <summary>The upload of the receipt of an expense from the browser (the server checks its real type and size again).</summary>
internal static class ExpenseReceipts
{
    /// <summary>The address of the receipt of an expense.</summary>
    public static string Url(Guid expenseId) => $"/admin/expenses/{expenseId}/receipt";

    public static async Task<Result> UploadAsync(IOperationRunner runner, Guid expenseId, IBrowserFile file)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(file);
        if (file.Size > ExpenseService.MaxReceiptBytes)
        {
            return ExpenseErrors.ReceiptInvalid;
        }

        var name = file.Name;
        await using var content = file.OpenReadStream(ExpenseService.MaxReceiptBytes);
        return await runner.RunAsync<ExpenseService, Result>((service, ct) => service.UploadReceiptAsync(expenseId, name, content, ct));
    }
}
