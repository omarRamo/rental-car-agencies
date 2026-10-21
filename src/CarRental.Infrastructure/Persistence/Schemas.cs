namespace CarRental.Infrastructure.Persistence;

/// <summary>
/// One SQL schema per module (ADR-0003, ADR-0012). Tables of a module live in its schema; EF configurations are in
/// <c>Persistence/Configurations/{Module}</c>.
/// </summary>
public static class Schemas
{
    public const string Identity = "identity";
    public const string Audit = "audit";
    public const string Infra = "infra";
    public const string Referential = "referential";
    public const string Fleet = "fleet";
    public const string Pricing = "pricing";
    public const string Bookings = "bookings";
    public const string Customers = "customers";
    public const string Payments = "payments";
    public const string Billing = "billing";
    public const string Cash = "cash";
    public const string Channels = "channels";
    public const string Messaging = "messaging";
    public const string Reporting = "reporting";
    public const string Accounting = "accounting";

    /// <summary>Owned by Hangfire, which creates and upgrades it at startup.</summary>
    public const string Hangfire = "hangfire";
}
