using System.Globalization;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Billing;

/// <summary>Display formats of the billing screens and documents (current culture; instants in Tunis time).</summary>
internal static class BillingFormat
{
    public const string PdfContentType = "application/pdf";

    public static DialogOptions Dialog(MaxWidth maxWidth = MaxWidth.Small) =>
        new() { MaxWidth = maxWidth, FullWidth = true, CloseOnEscapeKey = true };

    public static string Amount(Money amount) => amount.ToString("N", CultureInfo.CurrentCulture);

    public static string Amount(Money amount, CultureInfo culture) => amount.ToString("N", culture);

    /// <summary>A VAT rate as a percentage, e.g. <c>19 %</c>.</summary>
    public static string Rate(decimal rate, CultureInfo? culture = null) =>
        string.Create(culture ?? CultureInfo.CurrentCulture, $"{rate * 100m:0.##} %");

    public static string Date(DateOnly date, CultureInfo? culture = null) => date.ToString("d", culture ?? CultureInfo.CurrentCulture);

    public static string Instant(DateTimeOffset instant, CultureInfo? culture = null) =>
        TunisTime.ToTunis(instant).ToString("g", culture ?? CultureInfo.CurrentCulture);

    public static string Instant(DateTimeOffset? instant) => instant is { } value ? Instant(value) : "—";

    /// <summary>The address of the PDF of an invoice (original, or the courtesy translation in <paramref name="language"/>).</summary>
    public static string InvoicePdfUrl(Guid invoiceId, string? language = null) =>
        language is null ? $"/admin/invoices/{invoiceId:D}/pdf" : $"/admin/invoices/{invoiceId:D}/pdf?lang={language}";

    public static string CreditNotePdfUrl(Guid creditNoteId) => $"/admin/credit-notes/{creditNoteId:D}/pdf";

    public static string DesignationUrl(Guid fineId) => $"/admin/fines/{fineId:D}/designation";

    public static string ClaimFileUrl(Guid documentId) => $"/admin/claims/files/{documentId:D}";

    public static string EInvoiceIcon(EInvoiceStatus status) => status switch
    {
        EInvoiceStatus.Transmitted => Icons.Material.Outlined.CloudDone,
        EInvoiceStatus.Pending => Icons.Material.Outlined.CloudUpload,
        EInvoiceStatus.Failed => Icons.Material.Outlined.ErrorOutline,
        _ => Icons.Material.Outlined.Description,
    };

    public static Color EInvoiceColor(EInvoiceStatus status) => status switch
    {
        EInvoiceStatus.Transmitted => Color.Success,
        EInvoiceStatus.Pending => Color.Info,
        EInvoiceStatus.Failed => Color.Error,
        _ => Color.Default,
    };

    public static string FineIcon(FineStatus status) => status switch
    {
        FineStatus.Recorded => Icons.Material.Outlined.Inbox,
        FineStatus.Designated => Icons.Material.Outlined.PersonPin,
        FineStatus.Reinvoiced => Icons.Material.Outlined.ReceiptLong,
        FineStatus.Paid => Icons.Material.Outlined.Paid,
        _ => Icons.Material.Outlined.Lock,
    };

    public static string ClaimIcon(ClaimStatus status) => status switch
    {
        ClaimStatus.Open => Icons.Material.Outlined.FolderOpen,
        ClaimStatus.AtInsurer => Icons.Material.Outlined.HourglassTop,
        _ => Icons.Material.Outlined.Lock,
    };

    /// <summary>The value of a <c>datetime-local</c> input for an instant (Tunis wall clock).</summary>
    public static string ToLocalInput(DateTimeOffset instant) =>
        TunisTime.ToTunis(instant).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The instant of a <c>datetime-local</c> input value (Tunis wall clock), or <see langword="null"/>.</summary>
    public static DateTimeOffset? FromLocalInput(string? value) =>
        DateTime.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? TunisTime.FromLocal(local)
            : null;

    /// <summary>A date of a <c>date</c> input (<c>yyyy-MM-dd</c>), or <see langword="null"/>.</summary>
    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static string ToDateInput(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
