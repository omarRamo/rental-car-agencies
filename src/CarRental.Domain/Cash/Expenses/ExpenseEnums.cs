namespace CarRental.Domain.Cash.Expenses;

/// <summary>Categories of the expenses (SPEC §8 "Dépenses").</summary>
public enum ExpenseCategory
{
    /// <summary>Carburant.</summary>
    Fuel = 1,

    /// <summary>Entretien (oil change, tyres, brakes…).</summary>
    Maintenance = 2,

    /// <summary>Réparations.</summary>
    Repairs = 3,

    /// <summary>Assurance.</summary>
    Insurance = 4,

    /// <summary>Vignette (road tax).</summary>
    RoadTax = 5,

    /// <summary>Loyers de leasing.</summary>
    Leasing = 6,

    /// <summary>Commissions Booking.com billed apart (those deducted from a payout are posted by the Booking.com reconciliation).</summary>
    CommissionBookingCom = 7,

    /// <summary>Commissions Clic to Pay billed apart (those of a settlement of the merchant statement are posted with it).</summary>
    CommissionClicToPay = 8,

    /// <summary>Loyer des locaux.</summary>
    PremisesRent = 9,

    /// <summary>Charges: water, electricity, telephone, internet…</summary>
    Utilities = 10,

    /// <summary>Autres dépenses.</summary>
    Other = 11,
}

/// <summary>How an expense was paid: cash goes through the open cash session of the user, the other means through the bank.</summary>
public enum ExpensePaymentMethod
{
    /// <summary>Espèces: an outflow of the revenue cash of the user's open session.</summary>
    Cash = 1,

    /// <summary>The bank card of the agency.</summary>
    Card = 2,

    /// <summary>Virement bancaire.</summary>
    BankTransfer = 3,

    /// <summary>Chèque.</summary>
    Cheque = 4,
}

/// <summary>Why an expense counts, or not, as a direct cost of a vehicle in the reports (ADR-0063, no cost counted twice).</summary>
public enum ExpenseVehicleCostRule
{
    /// <summary>Counted by the <c>expenses</c> source, excluding VAT, on its date.</summary>
    Counted = 1,

    /// <summary>Not linked to a vehicle: not a direct cost of a vehicle.</summary>
    NoVehicle = 2,

    /// <summary>Linked to a maintenance of the fleet: the <c>fleet</c> source counts its actual cost.</summary>
    CountedByMaintenance = 3,

    /// <summary>Linked to a claim: the <c>claims</c> source counts its costs and reimbursements.</summary>
    CountedByClaim = 4,

    /// <summary>Insurance or road tax: the <c>fleet</c> source spreads the price of the document of the vehicle over its validity.</summary>
    CountedByVehicleDocument = 5,

    /// <summary>Leasing rent: in the depreciation of the economics of the vehicle.</summary>
    CountedByDepreciation = 6,
}
