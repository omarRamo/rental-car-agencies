using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Cash.Expenses;

/// <summary>
/// An expense of an agency (SPEC §8 "Dépenses", aggregate): its readable number <c>DEP-AAAA-NNNNNN</c>, date, category,
/// supplier, the supplier's reference, the amounts HT / VAT / TTC, the means of payment — cash is an outflow of the open
/// cash session of the user, recorded with it; card, transfer and cheque go through the bank —, the receipt (photo or
/// PDF in a private container) and, for an expense of the fleet, the vehicle and the maintenance or claim it pays.
/// </summary>
/// <remarks>
/// <para>Editable until its month is closed (the services check the period); an expense paid in cash keeps its date,
/// amounts and means of payment once its movement is in a cash session.</para>
/// <para>Deleting is logical, with a reason, the user and the instant (the audit log and the temporal history keep every
/// version); the movement of a cash expense is reversed in a cash session.</para>
/// </remarks>
public sealed class Expense : AggregateRoot, ISoftDeletable, IAgencyOwned
{
    public const int SupplierMinLength = 2;
    public const int SupplierMaxLength = 120;
    public const int TaxIdMaxLength = 30;
    public const int ReferenceMaxLength = 60;
    public const int DescriptionMaxLength = 500;
    public const int PaymentReferenceMaxLength = 60;
    public const int NumberMaxLength = 20;
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 500;
    public const int ReceiptFileNameMaxLength = 200;
    public const int ReceiptPathMaxLength = 300;

    /// <summary>Earliest date of an expense (guard against typing errors).</summary>
    public static readonly DateOnly EarliestDate = new(2000, 1, 1);

    private Expense()
    {
    }

    private Expense(Guid id, ExpenseDetails details, ExpenseNumber number, Guid? cashSessionId, AuditActor by, DateTimeOffset at)
        : base(id)
    {
        AgencyId = details.AgencyId;
        Number = number.Value;
        Year = number.Year;
        Sequence = number.Sequence;
        PaymentMethod = details.PaymentMethod;
        CashSessionId = cashSessionId;
        Apply(details);
        RecordedAtUtc = at;
        RecordedByUserId = by.UserId;
        RecordedByName = CashText.ActorName(by);
        ChangedAtUtc = at;
        ChangedByName = RecordedByName;
        Revision = 1;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>Readable number, <c>DEP-{year}-{sequence}</c>: the piece of its journal entries.</summary>
    public string Number { get; private set; } = null!;

    public int Year { get; private set; }

    public int Sequence { get; private set; }

    /// <summary>Date of the supplier's invoice or receipt (Tunis): the date of its entry in the purchases journal.</summary>
    public DateOnly Date { get; private set; }

    public ExpenseCategory Category { get; private set; }

    public string Supplier { get; private set; } = null!;

    /// <summary>Tax identifier (matricule fiscal) of the supplier, when the receipt shows it.</summary>
    public string? SupplierTaxId { get; private set; }

    /// <summary>Number of the supplier's invoice or receipt.</summary>
    public string? Reference { get; private set; }

    public string? Description { get; private set; }

    public Money ExclVat { get; private set; }

    public Money Vat { get; private set; }

    public Money InclVat { get; private set; }

    public decimal? VatRate { get; private set; }

    public ExpensePaymentMethod PaymentMethod { get; private set; }

    /// <summary>Cheque number or transfer reference.</summary>
    public string? PaymentReference { get; private set; }

    /// <summary>Cash: the session whose revenue cash paid the expense.</summary>
    public Guid? CashSessionId { get; private set; }

    public Guid? VehicleId { get; private set; }

    /// <summary>The maintenance of the vehicle this expense pays (its cost counts through the fleet).</summary>
    public Guid? MaintenanceEventId { get; private set; }

    /// <summary>The claim of the vehicle this expense pays (its cost counts through the claim).</summary>
    public Guid? ClaimId { get; private set; }

    public string? ReceiptPath { get; private set; }

    public string? ReceiptContentType { get; private set; }

    public string? ReceiptFileName { get; private set; }

    public long? ReceiptLength { get; private set; }

    public string? ReceiptHash { get; private set; }

    public DateTimeOffset? ReceiptUploadedAtUtc { get; private set; }

    public string? ReceiptUploadedBy { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public Guid? RecordedByUserId { get; private set; }

    public string? RecordedByName { get; private set; }

    public DateTimeOffset ChangedAtUtc { get; private set; }

    public string? ChangedByName { get; private set; }

    /// <summary>Version of the expense shown to the user (1, 2, 3…): a change made on an older version is refused.</summary>
    public int Revision { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public Guid? DeletedByUserId { get; private set; }

    public string? DeletedByName { get; private set; }

    public string? DeletionReason { get; private set; }

    /// <summary>Cash expense deleted: the session in which its outflow was reversed.</summary>
    public Guid? ReversedInSessionId { get; private set; }

    public bool IsPaidInCash => PaymentMethod == ExpensePaymentMethod.Cash;

    public bool HasReceipt => ReceiptPath is not null;

    public ExpenseAmounts Amounts => ExpenseAmounts.Restore(ExclVat, Vat, InclVat, VatRate);

    /// <summary>Whether, and why not, the expense counts as a direct cost of its vehicle in the reports.</summary>
    public ExpenseVehicleCostRule VehicleCostRule => ExpenseVehicleCosts.RuleOf(Category, VehicleId, MaintenanceEventId, ClaimId);

    /// <summary>
    /// Records an expense with its number; <paramref name="cashSessionId"/> is the open session of the user for an expense
    /// paid in cash (the caller records the movement in it, in the same transaction), absent otherwise.
    /// </summary>
    public static Result<Expense> Record(ExpenseDetails details, ExpenseNumber number, Guid? cashSessionId, DateOnly today, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(by);
        if (details.AgencyId == Guid.Empty)
        {
            return ExpenseErrors.AgencyNotFound;
        }

        var checkedDetails = Validate(details, today);
        if (checkedDetails.IsFailure)
        {
            return checkedDetails.Error;
        }

        var cash = details.PaymentMethod == ExpensePaymentMethod.Cash;
        if (cash && (cashSessionId is null || cashSessionId == Guid.Empty))
        {
            return ExpenseErrors.CashSessionRequired;
        }

        if (!cash && cashSessionId is not null)
        {
            throw new ArgumentException("Only an expense paid in cash has a cash session.", nameof(cashSessionId));
        }

        var expense = new Expense(NewId(), checkedDetails.Value, number, cashSessionId, by, at);
        expense.Raise(new ExpenseRecorded(expense.Id, expense.AgencyId, expense.Category, expense.PaymentMethod));
        return expense;
    }

    /// <summary>
    /// Changes the expense (the version shown must be the current one). An expense paid in cash keeps its date, amounts and
    /// means of payment; a bank expense does not become a cash one.
    /// </summary>
    public Result Change(ExpenseDetails details, int revision, DateOnly today, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(by);
        if (IsDeleted)
        {
            return ExpenseErrors.Deleted;
        }

        if (revision != Revision)
        {
            return ExpenseErrors.Stale;
        }

        if (details.AgencyId != AgencyId)
        {
            return ExpenseErrors.AgencyNotFound;
        }

        var checkedDetails = Validate(details, today);
        if (checkedDetails.IsFailure)
        {
            return checkedDetails.Error;
        }

        var changed = checkedDetails.Value;
        var sameAmounts = changed.Amounts.ExclVat == ExclVat && changed.Amounts.Vat == Vat && changed.Amounts.InclVat == InclVat;
        if (IsPaidInCash && (changed.PaymentMethod != ExpensePaymentMethod.Cash || changed.Date != Date || !sameAmounts))
        {
            return ExpenseErrors.CashAmountsLocked;
        }

        if (sameAmounts)
        {
            // The same amounts typed another way (HT and VAT instead of TTC and rate): the rate entered first is kept.
            changed = changed with { Amounts = Amounts };
        }

        if (!IsPaidInCash && changed.PaymentMethod == ExpensePaymentMethod.Cash)
        {
            return ExpenseErrors.PaymentMethodChangeNotAllowed;
        }

        PaymentMethod = changed.PaymentMethod;
        Apply(changed);
        Touch(by, at);
        Raise(new ExpenseChanged(Id, AgencyId));
        return Result.Success();
    }

    /// <summary>Attaches (or replaces) the receipt; the previous file stays in the private container (an accounting piece).</summary>
    public Result AttachReceipt(ExpenseReceipt receipt, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(by);
        if (IsDeleted)
        {
            return ExpenseErrors.Deleted;
        }

        if (string.IsNullOrWhiteSpace(receipt.Path) || receipt.Path.Length > ReceiptPathMaxLength || receipt.Length <= 0 || string.IsNullOrWhiteSpace(receipt.ContentType))
        {
            return ExpenseErrors.ReceiptInvalid;
        }

        var name = CashText.Clean(receipt.FileName);
        ReceiptPath = receipt.Path;
        ReceiptContentType = receipt.ContentType;
        ReceiptFileName = name is { Length: > ReceiptFileNameMaxLength } ? name[..ReceiptFileNameMaxLength] : name;
        ReceiptLength = receipt.Length;
        ReceiptHash = receipt.ContentHash;
        ReceiptUploadedAtUtc = at;
        ReceiptUploadedBy = CashText.ActorName(by);
        Touch(by, at);
        Raise(new ExpenseReceiptAttached(Id, AgencyId));
        return Result.Success();
    }

    /// <summary>
    /// Deletes the expense with a reason (logical deletion, audited); for a cash expense, <paramref name="reversedInSessionId"/>
    /// is the session in which the caller records the reversal of its outflow, in the same transaction.
    /// </summary>
    public Result Delete(string? reason, Guid? reversedInSessionId, AuditActor by, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (IsDeleted)
        {
            return ExpenseErrors.Deleted;
        }

        var reasonChecked = CheckDeletionReason(reason);
        if (reasonChecked.IsFailure)
        {
            return reasonChecked;
        }

        if (IsPaidInCash && reversedInSessionId is null)
        {
            return ExpenseErrors.CashSessionRequired;
        }

        if (!IsPaidInCash && reversedInSessionId is not null)
        {
            throw new ArgumentException("Only a cash expense is reversed in a session.", nameof(reversedInSessionId));
        }

        IsDeleted = true;
        DeletedAtUtc = at;
        DeletedByUserId = by.UserId;
        DeletedByName = CashText.ActorName(by);
        DeletionReason = CashText.Clean(reason);
        ReversedInSessionId = reversedInSessionId;
        Touch(by, at);
        Raise(new ExpenseDeleted(Id, AgencyId));
        return Result.Success();
    }

    /// <summary>Checks the reason of a deletion (the caller checks it before putting the cash of the expense back in a session).</summary>
    public static Result CheckDeletionReason(string? reason)
    {
        var text = CashText.Clean(reason);
        return text is null || text.Length < ReasonMinLength || text.Length > ReasonMaxLength ? ExpenseErrors.DeletionReasonRequired : Result.Success();
    }

    /// <summary>Checks and normalizes what the user entered (the vehicle, maintenance and claim are checked by the caller against the other modules).</summary>
    public static Result<ExpenseDetails> Validate(ExpenseDetails details, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Date < EarliestDate || details.Date > today)
        {
            return ExpenseErrors.DateInvalid;
        }

        if (!Enum.IsDefined(details.Category))
        {
            return ExpenseErrors.CategoryInvalid;
        }

        if (!Enum.IsDefined(details.PaymentMethod))
        {
            return ExpenseErrors.PaymentMethodInvalid;
        }

        var supplier = CashText.Clean(details.Supplier);
        if (supplier is null || supplier.Length < SupplierMinLength || supplier.Length > SupplierMaxLength)
        {
            return ExpenseErrors.SupplierInvalid;
        }

        var taxId = CashText.Clean(details.SupplierTaxId);
        var reference = CashText.Clean(details.Reference);
        var description = CashText.Clean(details.Description);
        var paymentReference = CashText.Clean(details.PaymentReference);
        if (taxId is { Length: > TaxIdMaxLength }
            || reference is { Length: > ReferenceMaxLength }
            || description is { Length: > DescriptionMaxLength }
            || paymentReference is { Length: > PaymentReferenceMaxLength })
        {
            return ExpenseErrors.TextTooLong;
        }

        if (details.PaymentMethod == ExpensePaymentMethod.Cheque && paymentReference is null)
        {
            return ExpenseErrors.ChequeNumberRequired;
        }

        ArgumentNullException.ThrowIfNull(details.Amounts);
        if (details.VehicleId is { } vehicle && (vehicle == Guid.Empty || !ExpenseVehicleCosts.MayLinkVehicle(details.Category)))
        {
            return ExpenseErrors.VehicleNotAllowed;
        }

        if ((details.MaintenanceEventId is not null || details.ClaimId is not null) && details.VehicleId is null)
        {
            return ExpenseErrors.LinkInvalid;
        }

        if (details.MaintenanceEventId is not null && details.ClaimId is not null)
        {
            return ExpenseErrors.LinkInvalid;
        }

        return details with
        {
            Supplier = supplier,
            SupplierTaxId = taxId,
            Reference = reference,
            Description = description,
            PaymentReference = details.PaymentMethod == ExpensePaymentMethod.Cash ? null : paymentReference,
        };
    }

    private void Apply(ExpenseDetails details)
    {
        Date = details.Date;
        Category = details.Category;
        Supplier = details.Supplier!;
        SupplierTaxId = details.SupplierTaxId;
        Reference = details.Reference;
        Description = details.Description;
        ExclVat = details.Amounts.ExclVat;
        Vat = details.Amounts.Vat;
        InclVat = details.Amounts.InclVat;
        VatRate = details.Amounts.VatRate;
        PaymentReference = details.PaymentReference;
        VehicleId = details.VehicleId;
        MaintenanceEventId = details.MaintenanceEventId;
        ClaimId = details.ClaimId;
    }

    private void Touch(AuditActor by, DateTimeOffset at)
    {
        ChangedAtUtc = at;
        ChangedByName = CashText.ActorName(by);
        Revision++;
    }
}

/// <summary>What the user enters for an expense.</summary>
public sealed record ExpenseDetails(
    Guid AgencyId,
    DateOnly Date,
    ExpenseCategory Category,
    string? Supplier,
    ExpenseAmounts Amounts,
    ExpensePaymentMethod PaymentMethod,
    string? PaymentReference = null,
    string? SupplierTaxId = null,
    string? Reference = null,
    string? Description = null,
    Guid? VehicleId = null,
    Guid? MaintenanceEventId = null,
    Guid? ClaimId = null);

/// <summary>A receipt stored in the private container: its path, real type, size, SHA-256 and the name sent by the device (display only).</summary>
public sealed record ExpenseReceipt(string Path, string ContentType, long Length, string ContentHash, string? FileName);

/// <summary>The readable number of an expense, <c>DEP-{year}-{sequence}</c> (six digits at least).</summary>
public sealed record ExpenseNumber(int Year, int Sequence)
{
    public const string Prefix = "DEP";

    public string Value => string.Create(CultureInfo.InvariantCulture, $"{Prefix}-{Year:D4}-{Sequence:D6}");

    public override string ToString() => Value;
}

/// <summary>
/// The last number given to the expenses of an agency in a year: incremented in the transaction of the expense, its
/// <c>rowversion</c> refuses two numbers given at once (the second expense is recorded again). Unlike the invoices, a gap
/// is harmless (a refused expense does not give its number back when it fails after the save).
/// </summary>
public sealed class ExpenseSequence : AggregateRoot, IAgencyOwned
{
    private ExpenseSequence()
    {
    }

    private ExpenseSequence(Guid id, Guid agencyId, int year)
        : base(id)
    {
        AgencyId = agencyId;
        Year = year;
    }

    public Guid AgencyId { get; private set; }

    public int Year { get; private set; }

    public int LastValue { get; private set; }

    public static ExpenseSequence Start(Guid agencyId, int year) => new(NewId(), agencyId, year);

    public ExpenseNumber Next()
    {
        LastValue++;
        return new ExpenseNumber(Year, LastValue);
    }
}

/// <summary>
/// The rule that keeps a cost from counting twice in the margins of the vehicles (ADR-0046, ADR-0063): the
/// <c>expenses</c> source counts an expense of its vehicle, excluding VAT, on its date — fuel, maintenance, repairs and
/// other expenses — unless it pays a maintenance of the fleet (the <c>fleet</c> source counts the actual cost of the
/// maintenance) or a claim (the <c>claims</c> source counts its costs); insurance and road tax are the prices of the
/// documents of the vehicle (<c>fleet</c> source) and leasing rents are in its depreciation.
/// </summary>
public static class ExpenseVehicleCosts
{
    /// <summary>Categories an expense of which may name a vehicle.</summary>
    public static bool MayLinkVehicle(ExpenseCategory category) => category is ExpenseCategory.Fuel or ExpenseCategory.Maintenance
        or ExpenseCategory.Repairs or ExpenseCategory.Insurance or ExpenseCategory.RoadTax or ExpenseCategory.Leasing or ExpenseCategory.Other;

    public static ExpenseVehicleCostRule RuleOf(ExpenseCategory category, Guid? vehicleId, Guid? maintenanceEventId, Guid? claimId)
    {
        if (vehicleId is null)
        {
            return ExpenseVehicleCostRule.NoVehicle;
        }

        if (maintenanceEventId is not null)
        {
            return ExpenseVehicleCostRule.CountedByMaintenance;
        }

        if (claimId is not null)
        {
            return ExpenseVehicleCostRule.CountedByClaim;
        }

        return category switch
        {
            ExpenseCategory.Insurance or ExpenseCategory.RoadTax => ExpenseVehicleCostRule.CountedByVehicleDocument,
            ExpenseCategory.Leasing => ExpenseVehicleCostRule.CountedByDepreciation,
            ExpenseCategory.Fuel or ExpenseCategory.Maintenance or ExpenseCategory.Repairs or ExpenseCategory.Other => ExpenseVehicleCostRule.Counted,
            _ => ExpenseVehicleCostRule.NoVehicle,
        };
    }
}
