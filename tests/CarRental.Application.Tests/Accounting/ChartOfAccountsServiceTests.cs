using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Domain.Accounting;

namespace CarRental.Application.Tests.Accounting;

/// <summary>
/// The chart of accounts and the mapping of the operation types (ADR-0065): seeded with the default chart "to be validated
/// by the chartered accountant", accounts added, renamed, confirmed, deactivated when no operation type uses them.
/// </summary>
public sealed class ChartOfAccountsServiceTests
{
    private readonly AccountingHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_default_chart_is_seeded_to_be_validated_with_every_operation_type_mapped_to_its_default_account()
    {
        var chart = _harness.Chart(Accountant());

        var accounts = (await chart.ListAccountsAsync(Ct)).Value;
        var mapping = (await chart.ListMappingAsync(Ct)).Value;

        accounts.Select(a => a.Number).ShouldBe(AccountingMappingCatalog.DefaultChart.Select(a => a.Number).Order(StringComparer.Ordinal));
        accounts.ShouldAllBe(a => a.IsDefault && a.NeedsValidation && a.IsActive);
        mapping.Select(m => m.Key).ShouldBe(AccountingMappingCatalog.All.Select(d => d.Key));
        mapping.ShouldAllBe(m => m.AccountNumber == m.DefaultAccount && m.AccountLabel != null && m.AccountNeedsValidation);
        accounts.Single(a => a.Number == "411").MappedKeys.ShouldContain(AccountingMappingCatalog.Customers);
    }

    [Fact]
    public async Task Provisioning_again_adds_nothing_and_keeps_what_the_accountant_changed()
    {
        var chart = _harness.Chart(Accountant());
        (await chart.CreateAccountAsync("70611", "Locations courte durée", Ct)).IsSuccess.ShouldBeTrue();
        (await chart.MapAsync(AccountingMappingCatalog.SalesRental, "70611", Ct)).IsSuccess.ShouldBeTrue();

        _harness.Provision(_harness.AgencyId);

        _harness.Db.Rows<LedgerAccount>().Count.ShouldBe(AccountingMappingCatalog.DefaultChart.Count + 1);
        _harness.Db.Rows<AccountMapping>().Count.ShouldBe(AccountingMappingCatalog.All.Count);
        _harness.Db.Rows<AccountMapping>().Single(m => m.Key == AccountingMappingCatalog.SalesRental).AccountNumber.ShouldBe("70611");
    }

    [Fact]
    public async Task Each_agency_has_its_own_chart_and_mapping()
    {
        var other = Guid.CreateVersion7();
        _harness.Provision(other);
        var chart = _harness.Chart(Accountant());

        (await chart.CreateAccountAsync("70611", "Locations courte durée", Ct)).IsSuccess.ShouldBeTrue();
        (await chart.MapAsync(AccountingMappingCatalog.SalesRental, "70611", Ct)).IsSuccess.ShouldBeTrue();
        var bankOfOther = _harness.Db.Rows<LedgerAccount>().Single(a => a.AgencyId == other && a.Number == "532");

        (await chart.ListAccountsAsync(Ct)).Value.Count.ShouldBe(AccountingMappingCatalog.DefaultChart.Count + 1, "the accounts of the agency of the user only");
        _harness.Db.Rows<LedgerAccount>().Count(a => a.AgencyId == other).ShouldBe(AccountingMappingCatalog.DefaultChart.Count);
        _harness.Db.Rows<AccountMapping>().Single(m => m.AgencyId == other && m.Key == AccountingMappingCatalog.SalesRental).AccountNumber.ShouldBe("7061");
        (await chart.RenameAccountAsync(bankOfOther.Id, "Banque d'une autre agence", Ct)).Error.ShouldBe(AccountingErrors.AccountNotFound);
        bankOfOther.Label.ShouldBe("Banque");
    }

    [Fact]
    public async Task The_support_outside_any_agency_has_no_chart()
    {
        var chart = _harness.Chart(AccountingHarness.User(Roles.Support));

        (await chart.ListAccountsAsync(Ct)).Error.ShouldBe(AccountingErrors.AgencyNotFound);
        (await chart.CreateAccountAsync("6063", "Fournitures", Ct)).Error.ShouldBe(AccountingErrors.AgencyNotFound);
    }

    [Fact]
    public async Task An_account_is_added_with_a_number_of_its_own_renamed_and_confirmed()
    {
        var accountant = Accountant();
        var chart = _harness.Chart(accountant);

        var id = (await chart.CreateAccountAsync(" 6063 ", " Fournitures d'entretien ", Ct)).Value;
        (await chart.CreateAccountAsync("6063", "Doublon", Ct)).Error.ShouldBe(AccountingErrors.AccountNumberTaken);
        (await chart.CreateAccountAsync("06063", "Zéro devant", Ct)).Error.ShouldBe(AccountingErrors.AccountNumberInvalid);
        (await chart.CreateAccountAsync("60A", "Lettre", Ct)).Error.ShouldBe(AccountingErrors.AccountNumberInvalid);
        (await chart.CreateAccountAsync("6064", " ", Ct)).Error.ShouldBe(AccountingErrors.AccountLabelInvalid);

        var created = (await chart.ListAccountsAsync(Ct)).Value.Single(a => a.Id == id);
        created.Number.ShouldBe("6063");
        created.Label.ShouldBe("Fournitures d'entretien");
        created.Class.ShouldBe(6);
        created.IsDefault.ShouldBeFalse();
        created.NeedsValidation.ShouldBeFalse();
        created.ChangedBy.ShouldBe(accountant.UserName);

        var vat = _harness.Db.Rows<LedgerAccount>().Single(a => a.Number == "4365");
        (await chart.ConfirmAccountAsync(vat.Id, Ct)).IsSuccess.ShouldBeTrue();
        vat.NeedsValidation.ShouldBeFalse();
        var bank = _harness.Db.Rows<LedgerAccount>().Single(a => a.Number == "532");
        (await chart.RenameAccountAsync(bank.Id, "Banque BIAT", Ct)).IsSuccess.ShouldBeTrue();
        bank.Label.ShouldBe("Banque BIAT");
        bank.NeedsValidation.ShouldBeFalse("renaming is looking at it");
        (await chart.RenameAccountAsync(Guid.CreateVersion7(), "Inconnu", Ct)).Error.ShouldBe(AccountingErrors.AccountNotFound);
    }

    [Fact]
    public async Task An_operation_type_is_mapped_to_an_active_account_of_the_chart_only()
    {
        var chart = _harness.Chart(Accountant());
        var id = (await chart.CreateAccountAsync("6063", "Fournitures d'entretien", Ct)).Value;
        (await chart.SetAccountActiveAsync(id, false, Ct)).IsSuccess.ShouldBeTrue();

        (await chart.MapAsync("sales.unknown", "706", Ct)).Error.ShouldBe(AccountingErrors.MappingKeyUnknown);
        (await chart.MapAsync(AccountingMappingCatalog.ExpenseMaintenance, "9999", Ct)).Error.ShouldBe(AccountingErrors.MappingAccountInvalid);
        (await chart.MapAsync(AccountingMappingCatalog.ExpenseMaintenance, "6063", Ct)).Error.ShouldBe(AccountingErrors.MappingAccountInvalid, "inactive");

        (await chart.SetAccountActiveAsync(id, true, Ct)).IsSuccess.ShouldBeTrue();
        (await chart.MapAsync(AccountingMappingCatalog.ExpenseMaintenance, "6063", Ct)).IsSuccess.ShouldBeTrue();

        var mapped = (await chart.ListMappingAsync(Ct)).Value.Single(m => m.Key == AccountingMappingCatalog.ExpenseMaintenance);
        mapped.AccountNumber.ShouldBe("6063");
        mapped.AccountNeedsValidation.ShouldBeFalse();
        mapped.ChangedAtUtc.ShouldBe(AccountingHarness.Now);
        (await chart.SetAccountActiveAsync(id, false, Ct)).Error.ShouldBe(AccountingErrors.AccountInUse, "an account mapped to an operation type stays active");
    }

    [Fact]
    public async Task Only_the_accounting_permission_reads_or_changes_the_chart()
    {
        var chart = _harness.Chart(AccountingHarness.User(Roles.Agent));
        var account = _harness.Db.Rows<LedgerAccount>()[0];

        (await chart.ListAccountsAsync(Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await chart.ListMappingAsync(Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await chart.CreateAccountAsync("6063", "Fournitures", Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await chart.RenameAccountAsync(account.Id, "Autre", Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await chart.ConfirmAccountAsync(account.Id, Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await chart.SetAccountActiveAsync(account.Id, false, Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
        (await chart.MapAsync(AccountingMappingCatalog.Bank, "532", Ct)).Error.ShouldBe(AccountingErrors.Forbidden);
    }

    private static ScopedUserContext Accountant() => AccountingHarness.User(Roles.Accountant);
}
