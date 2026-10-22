using CarRental.Domain.Accounting;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Accounting;

/// <summary>The chart of accounts, the catalogue of the operation types and their default accounts.</summary>
public sealed class ChartOfAccountsTests
{
    private static readonly AuditActor Accountant = new(Guid.CreateVersion7(), "comptable@demo.local", null, null);
    private static readonly DateTimeOffset At = new(2027, 4, 2, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("411", true)]
    [InlineData(" 4366 ", true)]
    [InlineData("1234567890", true)]
    [InlineData("4", false)]
    [InlineData("12345678901", false)]
    [InlineData("0411", false)]
    [InlineData("41a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_account_number_has_two_to_ten_digits_not_starting_with_zero(string? number, bool valid)
    {
        LedgerAccount.ValidateNumber(number).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void A_default_account_waits_for_the_chartered_accountant_until_confirmed_or_renamed()
    {
        var account = LedgerAccount.Create("4366", "État, TVA collectée", isDefault: true, Accountant, At).Value;

        account.NeedsValidation.ShouldBeTrue();
        account.IsActive.ShouldBeTrue();
        account.Class.ShouldBe(4);
        account.Confirm(Accountant, At).IsSuccess.ShouldBeTrue();
        account.NeedsValidation.ShouldBeFalse();

        var other = LedgerAccount.Create("7061", "Locations", isDefault: true, Accountant, At).Value;
        other.Rename("  Locations de voitures ", Accountant, At).IsSuccess.ShouldBeTrue();
        other.Label.ShouldBe("Locations de voitures");
        other.NeedsValidation.ShouldBeFalse();
        other.Rename(" ", Accountant, At).Error.ShouldBe(AccountingErrors.AccountLabelInvalid);

        LedgerAccount.Create("6061", "Carburants", isDefault: false, Accountant, At).Value.NeedsValidation.ShouldBeFalse();
        LedgerAccount.Create("6061", new string('x', 121), isDefault: false, Accountant, At).Error.ShouldBe(AccountingErrors.AccountLabelInvalid);
        LedgerAccount.Create("x", "Carburants", isDefault: false, Accountant, At).Error.ShouldBe(AccountingErrors.AccountNumberInvalid);
    }

    [Fact]
    public void Every_operation_type_has_a_unique_key_and_a_default_account_of_the_default_chart()
    {
        var chart = AccountingMappingCatalog.DefaultChart.Select(a => a.Number).ToList();

        chart.Distinct(StringComparer.Ordinal).Count().ShouldBe(chart.Count, "the default chart has unique numbers");
        chart.ShouldAllBe(n => LedgerAccount.ValidateNumber(n).IsSuccess);
        AccountingMappingCatalog.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count().ShouldBe(AccountingMappingCatalog.All.Count);
        AccountingMappingCatalog.All.ShouldAllBe(d => chart.Contains(d.DefaultAccount));
        AccountingMappingCatalog.All.ShouldAllBe(d => d.Key.Length <= AccountMapping.KeyMaxLength);
    }

    [Fact]
    public void Deposits_received_default_to_a_liability_and_never_to_revenue()
    {
        AccountingMappingCatalog.Find(AccountingMappingCatalog.DepositsReceived)!.DefaultAccount.ShouldStartWith("1");
        AccountingMappingCatalog.Find(AccountingMappingCatalog.SalesDailyContribution)!.DefaultAccount.ShouldStartWith("4", Case.Sensitive, "collected for the State");
        AccountingMappingCatalog.Find(AccountingMappingCatalog.SalesFineReinvoicing)!.DefaultAccount.ShouldStartWith("4", Case.Sensitive, "a disbursement");
    }

    [Fact]
    public void The_vat_collected_has_a_key_per_usual_rate_and_a_generic_one()
    {
        AccountingMappingCatalog.VatCollectedKey(0.19m).ShouldBe(AccountingMappingCatalog.VatCollected19);
        AccountingMappingCatalog.VatCollectedKey(0.13m).ShouldBe(AccountingMappingCatalog.VatCollected13);
        AccountingMappingCatalog.VatCollectedKey(0.07m).ShouldBe(AccountingMappingCatalog.VatCollected7);
        AccountingMappingCatalog.VatCollectedKey(0.065m).ShouldBe(AccountingMappingCatalog.VatCollected);
    }

    [Fact]
    public void A_mapping_names_a_known_operation_type_and_a_valid_number()
    {
        var mapping = AccountMapping.Create(AccountingMappingCatalog.Bank, "532", Accountant, At).Value;

        mapping.MapTo("5321", Accountant, At).IsSuccess.ShouldBeTrue();
        mapping.AccountNumber.ShouldBe("5321");
        mapping.MapTo("x", Accountant, At).Error.ShouldBe(AccountingErrors.MappingAccountInvalid);
        AccountMapping.Create("sales.unknown", "532", Accountant, At).Error.ShouldBe(AccountingErrors.MappingKeyUnknown);
    }

    [Fact]
    public void The_journals_have_their_usual_codes_and_the_cash_and_differences_block_the_closing()
    {
        JournalCatalog.All.Select(JournalCatalog.CodeOf).ShouldBe(["VT", "AC", "BQ", "CA", "OD"]);
        JournalCatalog.SeverityOf(ClosingCheckKind.UnlockedCashSession).ShouldBe(ClosingCheckSeverity.Blocking);
        JournalCatalog.SeverityOf(ClosingCheckKind.UnjustifiedDifference).ShouldBe(ClosingCheckSeverity.Blocking);
        JournalCatalog.SeverityOf(ClosingCheckKind.FileToInvoice).ShouldBe(ClosingCheckSeverity.Confirmable);
        JournalCatalog.SeverityOf(ClosingCheckKind.EInvoiceNotTransmitted).ShouldBe(ClosingCheckSeverity.Confirmable);
    }
}
