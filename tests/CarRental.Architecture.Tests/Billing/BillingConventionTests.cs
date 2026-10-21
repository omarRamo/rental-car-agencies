using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Architecture.Tests.Billing;

/// <summary>
/// Conventions of the Billing module (ADR-0011, ADR-0057): every error and every label built from a value exists in French
/// and English — the screens, the PDF, the e-mails and the texts frozen on the invoices — and the contract enums mirror the
/// domain ones (names and values).
/// </summary>
public sealed class BillingConventionTests
{
    private static readonly CultureInfo[] Cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en")];

    private static readonly ResourceManager Shared = new("CarRental.Web.Resources.SharedResources", Assemblies.Web);

    private static readonly ResourceManager Screens = new("CarRental.Web.Resources.Admin.Billing.BillingResources", Assemblies.Web);

    private static List<Error> Errors() =>
        [.. typeof(BillingErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];

    /// <summary>The label keys of the invoice lines and mentions written by the composer (<c>Billing.*</c> constants).</summary>
    private static IEnumerable<string> ComposerKeys()
    {
        var composer = Assemblies.Application.GetType("CarRental.Application.Billing.Invoicing.InvoiceComposer", throwOnError: true)!;
        return composer.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(k => k.StartsWith("Billing.", StringComparison.Ordinal) && !k.EndsWith('.'));
    }

    public static TheoryData<string> LabelKeys() =>
    [
        .. Enum.GetNames<InvoiceKind>().Select(k => $"Kind.{k}"),
        .. Enum.GetNames<CreditNoteScope>().Select(s => $"Scope.{s}"),
        .. Enum.GetNames<EInvoiceStatus>().Select(s => $"EInvoiceStatus.{s}"),
        .. Enum.GetNames<EInvoiceMode>().Select(m => $"EInvoiceMode.{m}"),
        .. Enum.GetNames<FineStatus>().Select(s => $"FineStatus.{s}"),
        .. Enum.GetNames<FineMatch>().SelectMany(m => new[] { $"FineMatch.{m}", $"FineMatch.{m}.Help" }),
        .. Enum.GetNames<ClaimStatus>().Select(s => $"ClaimStatus.{s}"),
        .. Enum.GetNames<ClaimDocumentKind>().Select(k => $"ClaimDocumentKind.{k}"),
        .. Enum.GetNames<IdentityDocumentTypeInfo>().Select(t => $"Designation.Document.{t}"),
        .. new[] { "Invoice", "CreditNote" }.SelectMany(t => new[] { $"Email.{t}.Subject", $"Email.{t}.Body" }),
        .. Enum.GetNames<SalesChannel>().Select(c => $"Billing.Channel.{c}"),
        .. ComposerKeys(),
    ];

    [Fact]
    public void Every_error_code_of_the_billing_module_has_a_user_facing_text()
    {
        var errors = Errors();

        errors.Count.ShouldBeGreaterThan(40);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("billing.");
            foreach (var culture in Cultures)
            {
                Shared.GetString($"Error.{error.Code}", culture).ShouldNotBeNullOrWhiteSpace($"Error.{error.Code} ({culture.Name})");
            }
        }
    }

    [Fact]
    public void Error_codes_of_the_billing_module_are_unique()
    {
        var codes = Errors().Select(e => e.Code).ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }

    [Theory]
    [MemberData(nameof(LabelKeys))]
    public void Every_value_shown_by_the_billing_screens_and_documents_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Screens.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }

    [Fact]
    public void The_composer_keys_are_found()
    {
        ComposerKeys().ShouldContain("Billing.Line.Fine");
        ComposerKeys().ShouldContain("Billing.Mention.ChannelBooking");
    }

    [Theory]
    [InlineData(typeof(InvoiceKindInfo), typeof(InvoiceKind))]
    [InlineData(typeof(InvoiceLineNatureInfo), typeof(InvoiceLineNature))]
    [InlineData(typeof(BuyerKindInfo), typeof(BuyerKind))]
    [InlineData(typeof(EInvoiceModeInfo), typeof(EInvoiceMode))]
    [InlineData(typeof(EInvoiceStatusInfo), typeof(EInvoiceStatus))]
    [InlineData(typeof(SalesDocumentTypeInfo), typeof(BillingDocumentType))]
    public void Contract_enums_mirror_the_domain_ones(Type contract, Type domain)
    {
        static IEnumerable<string> Members(Type type) =>
            Enum.GetValues(type).Cast<object>().Select(v => string.Create(CultureInfo.InvariantCulture, $"{Enum.GetName(type, v)}={Convert.ToInt32(v, CultureInfo.InvariantCulture)}"));

        Members(contract).ShouldBe(Members(domain), ignoreOrder: true, $"{contract.Name} must keep the names and values of {domain.Name}");
    }
}
