using System.Collections.Frozen;
using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Accounting;

/// <summary>
/// The account of an operation type (SPEC §8 "correspondance par type d'opération"): rental revenue and each invoice line
/// nature, VAT collected per rate, daily contribution, stamp duty, customers, deposits received (a liability, never
/// revenue), cash, bank, Clic to Pay clearing, Booking.com receivable and commissions, expenses per category, VAT
/// deductible, suppliers, cash differences. One row per agency and key of <see cref="AccountingMappingCatalog"/>: each
/// agency maps its operation types to the accounts of its own chart (ADR-0067).
/// </summary>
public sealed class AccountMapping : AggregateRoot, IAgencyOwned
{
    public const int KeyMaxLength = 64;

    private AccountMapping()
    {
    }

    private AccountMapping(Guid id, Guid agencyId, string key, string accountNumber, DateTimeOffset at, string? by)
        : base(id)
    {
        AgencyId = agencyId;
        Key = key;
        AccountNumber = accountNumber;
        ChangedAtUtc = at;
        ChangedBy = by;
    }

    /// <summary>The agency whose operation type is mapped.</summary>
    public Guid AgencyId { get; private set; }

    public string Key { get; private set; } = null!;

    public string AccountNumber { get; private set; } = null!;

    public DateTimeOffset ChangedAtUtc { get; private set; }

    public string? ChangedBy { get; private set; }

    /// <summary>
    /// Maps a key of the catalogue to an account of the chart of <paramref name="agencyId"/> (the caller checks that the
    /// account exists and is active).
    /// </summary>
    public static Result<AccountMapping> Create(Guid agencyId, string key, string accountNumber, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (agencyId == Guid.Empty)
        {
            return AccountingErrors.AgencyNotFound;
        }

        if (!AccountingMappingCatalog.IsKnown(key))
        {
            return AccountingErrors.MappingKeyUnknown;
        }

        var number = LedgerAccount.ValidateNumber(accountNumber);
        if (number.IsFailure)
        {
            return AccountingErrors.MappingAccountInvalid;
        }

        var mapping = new AccountMapping(NewId(), agencyId, key, number.Value, at, by.UserName);
        mapping.Raise(new AccountMappingChanged(mapping.Id, agencyId, key));
        return mapping;
    }

    public Result MapTo(string accountNumber, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        var number = LedgerAccount.ValidateNumber(accountNumber);
        if (number.IsFailure)
        {
            return AccountingErrors.MappingAccountInvalid;
        }

        AccountNumber = number.Value;
        ChangedAtUtc = at;
        ChangedBy = by.UserName;
        Raise(new AccountMappingChanged(Id, AgencyId, Key));
        return Result.Success();
    }
}

/// <summary>An operation type of the mapping: its stable key, its group and the account of the default chart.</summary>
public sealed record MappingDefinition(string Key, MappingGroup Group, string DefaultAccount);

/// <summary>An account of the default chart.</summary>
public sealed record DefaultAccount(string Number, string Label);

/// <summary>
/// The operation types of the mapping and the default chart of accounts, inspired by the Tunisian accounting system (système
/// comptable des entreprises). TODO(spec): every default number is to be validated by the chartered accountant (they are
/// seeded "à valider"); the platform posts nothing that the mapping does not name.
/// </summary>
public static class AccountingMappingCatalog
{
    // Sales: one key per accounting nature of an invoice line (Billing InvoiceLineNature, same names).
    public const string SalesRental = "sales.rental";
    public const string SalesDiscount = "sales.discount";
    public const string SalesExtras = "sales.extras";
    public const string SalesProtection = "sales.protection";
    public const string SalesDelivery = "sales.delivery";
    public const string SalesDailyContribution = "sales.daily_contribution";
    public const string SalesExtraMileage = "sales.extra_mileage";
    public const string SalesFuel = "sales.fuel";
    public const string SalesLateReturn = "sales.late_return";
    public const string SalesCleaning = "sales.cleaning";
    public const string SalesOtherCharge = "sales.other_charge";
    public const string SalesDamage = "sales.damage";
    public const string SalesCancellationFee = "sales.cancellation_fee";
    public const string SalesFineReinvoicing = "sales.fine_reinvoicing";
    public const string SalesManagementFee = "sales.management_fee";

    // Taxes
    public const string VatCollected = "taxes.vat_collected";
    public const string VatCollected19 = "taxes.vat_collected.19";
    public const string VatCollected13 = "taxes.vat_collected.13";
    public const string VatCollected7 = "taxes.vat_collected.7";
    public const string VatDeductible = "taxes.vat_deductible";
    public const string StampDuty = "taxes.stamp_duty";

    // Third parties
    public const string Customers = "third_parties.customers";
    public const string Suppliers = "third_parties.suppliers";
    public const string DepositsReceived = "third_parties.deposits_received";
    public const string BookingComReceivable = "third_parties.booking_com";

    // Treasury
    public const string Bank = "treasury.bank";
    public const string Cash = "treasury.cash";
    public const string CashDeposits = "treasury.cash_deposits";
    public const string CardTerminal = "treasury.card_terminal";
    public const string Cheques = "treasury.cheques";
    public const string BankTransfers = "treasury.bank_transfers";
    public const string ClicToPayClearing = "treasury.clictopay_clearing";
    public const string CardImprintDeposits = "treasury.card_imprint_deposits";
    public const string ChequeDeposits = "treasury.cheque_deposits";
    public const string InternalTransfers = "treasury.internal_transfers";

    // Expenses: one key per category (Cash ExpenseCategory, same names) and the channel commissions.
    public const string ExpenseFuel = "expenses.fuel";
    public const string ExpenseMaintenance = "expenses.maintenance";
    public const string ExpenseRepairs = "expenses.repairs";
    public const string ExpenseInsurance = "expenses.insurance";
    public const string ExpenseRoadTax = "expenses.road_tax";
    public const string ExpenseLeasing = "expenses.leasing";
    public const string ExpenseCommissionBookingCom = "expenses.commission_booking_com";
    public const string ExpenseCommissionClicToPay = "expenses.commission_clictopay";
    public const string ExpensePremisesRent = "expenses.premises_rent";
    public const string ExpenseUtilities = "expenses.utilities";
    public const string ExpenseOther = "expenses.other";

    // Differences
    public const string CashShortage = "differences.cash_shortage";
    public const string CashSurplus = "differences.cash_surplus";
    public const string Suspense = "differences.suspense";

    private static readonly FrozenDictionary<string, MappingDefinition> ByKey;

    static AccountingMappingCatalog()
    {
        All =
        [
            new(SalesRental, MappingGroup.Sales, "7061"),
            new(SalesDiscount, MappingGroup.Sales, "709"),
            new(SalesExtras, MappingGroup.Sales, "7062"),
            new(SalesProtection, MappingGroup.Sales, "7063"),
            new(SalesDelivery, MappingGroup.Sales, "7064"),
            new(SalesDailyContribution, MappingGroup.Sales, "4367"),
            new(SalesExtraMileage, MappingGroup.Sales, "7065"),
            new(SalesFuel, MappingGroup.Sales, "7066"),
            new(SalesLateReturn, MappingGroup.Sales, "7067"),
            new(SalesCleaning, MappingGroup.Sales, "7068"),
            new(SalesOtherCharge, MappingGroup.Sales, "7069"),
            new(SalesDamage, MappingGroup.Sales, "7581"),
            new(SalesCancellationFee, MappingGroup.Sales, "7582"),
            new(SalesFineReinvoicing, MappingGroup.Sales, "4671"),
            new(SalesManagementFee, MappingGroup.Sales, "7583"),
            new(VatCollected, MappingGroup.Taxes, "4366"),
            new(VatCollected19, MappingGroup.Taxes, "43661"),
            new(VatCollected13, MappingGroup.Taxes, "43662"),
            new(VatCollected7, MappingGroup.Taxes, "43663"),
            new(VatDeductible, MappingGroup.Taxes, "4365"),
            new(StampDuty, MappingGroup.Taxes, "4368"),
            new(Customers, MappingGroup.ThirdParties, "411"),
            new(Suppliers, MappingGroup.ThirdParties, "401"),
            new(DepositsReceived, MappingGroup.ThirdParties, "165"),
            new(BookingComReceivable, MappingGroup.ThirdParties, "4118"),
            new(Bank, MappingGroup.Treasury, "532"),
            new(Cash, MappingGroup.Treasury, "541"),
            new(CashDeposits, MappingGroup.Treasury, "5412"),
            new(CardTerminal, MappingGroup.Treasury, "532"),
            new(Cheques, MappingGroup.Treasury, "532"),
            new(BankTransfers, MappingGroup.Treasury, "532"),
            new(ClicToPayClearing, MappingGroup.Treasury, "5811"),
            new(CardImprintDeposits, MappingGroup.Treasury, "5812"),
            new(ChequeDeposits, MappingGroup.Treasury, "5813"),
            new(InternalTransfers, MappingGroup.Treasury, "581"),
            new(ExpenseFuel, MappingGroup.Expenses, "6061"),
            new(ExpenseMaintenance, MappingGroup.Expenses, "6151"),
            new(ExpenseRepairs, MappingGroup.Expenses, "6152"),
            new(ExpenseInsurance, MappingGroup.Expenses, "616"),
            new(ExpenseRoadTax, MappingGroup.Expenses, "6632"),
            new(ExpenseLeasing, MappingGroup.Expenses, "612"),
            new(ExpenseCommissionBookingCom, MappingGroup.Expenses, "6226"),
            new(ExpenseCommissionClicToPay, MappingGroup.Expenses, "6271"),
            new(ExpensePremisesRent, MappingGroup.Expenses, "6132"),
            new(ExpenseUtilities, MappingGroup.Expenses, "6065"),
            new(ExpenseOther, MappingGroup.Expenses, "6288"),
            new(CashShortage, MappingGroup.Differences, "658"),
            new(CashSurplus, MappingGroup.Differences, "758"),
            new(Suspense, MappingGroup.Differences, "471"),
        ];
        ByKey = All.ToFrozenDictionary(d => d.Key, StringComparer.Ordinal);
        DefaultChart =
        [
            new("165", "Dépôts et cautionnements reçus (cautions des clients)"),
            new("401", "Fournisseurs d'exploitation"),
            new("411", "Clients"),
            new("4118", "Clients — Booking.com (prépayé à recevoir)"),
            new("4365", "État, TVA récupérable sur autres biens et services"),
            new("4366", "État, TVA collectée"),
            new("43661", "État, TVA collectée 19 %"),
            new("43662", "État, TVA collectée 13 %"),
            new("43663", "État, TVA collectée 7 %"),
            new("4367", "État, contribution journalière (loi de finances 2026)"),
            new("4368", "État, droit de timbre sur factures"),
            new("4671", "Débours refacturés (amendes)"),
            new("471", "Écarts de rapprochement à justifier"),
            new("532", "Banque"),
            new("541", "Caisse — espèces de recette"),
            new("5412", "Caisse — cautions en espèces"),
            new("581", "Virements internes"),
            new("5811", "Clic to Pay — encaissements en ligne à recevoir"),
            new("5812", "Cautions par empreinte de carte"),
            new("5813", "Cautions par chèque en portefeuille"),
            new("6061", "Carburants"),
            new("6065", "Eau, électricité et charges"),
            new("612", "Redevances de leasing"),
            new("6132", "Loyers des locaux"),
            new("6151", "Entretien des véhicules"),
            new("6152", "Réparations des véhicules"),
            new("616", "Primes d'assurance"),
            new("6226", "Commissions Booking.com"),
            new("6271", "Commissions Clic to Pay"),
            new("6288", "Autres charges externes"),
            new("658", "Différences de caisse (manquants)"),
            new("6632", "Vignettes des véhicules"),
            new("7061", "Locations de véhicules"),
            new("7062", "Options et équipements"),
            new("7063", "Protections et rachats de franchise"),
            new("7064", "Livraisons et reprises"),
            new("7065", "Kilométrage supplémentaire"),
            new("7066", "Carburant refacturé"),
            new("7067", "Retards de restitution"),
            new("7068", "Nettoyage"),
            new("7069", "Autres frais refacturés"),
            new("709", "Rabais, remises et ristournes accordés"),
            new("7581", "Indemnités de dommages refacturées"),
            new("7582", "Frais d'annulation"),
            new("7583", "Frais de gestion"),
            new("758", "Différences de caisse (excédents)"),
        ];
    }

    /// <summary>Every operation type, in display order.</summary>
    public static IReadOnlyList<MappingDefinition> All { get; }

    /// <summary>The default chart of accounts (TODO(spec): to be validated by the chartered accountant).</summary>
    public static IReadOnlyList<DefaultAccount> DefaultChart { get; }

    public static bool IsKnown(string? key) => key is not null && ByKey.ContainsKey(key);

    public static MappingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>
    /// The key of the VAT collected at <paramref name="rate"/> (0.19 → <c>taxes.vat_collected.19</c>) when the catalogue has
    /// one, else the generic key of the VAT collected.
    /// </summary>
    public static string VatCollectedKey(decimal rate)
    {
        var key = $"{VatCollected}.{(rate * 100m).ToString("0.##", CultureInfo.InvariantCulture)}";
        return ByKey.ContainsKey(key) ? key : VatCollected;
    }
}
