using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Customers.Contracts;

/// <summary>
/// Business settings of the customer accounts and of the API of the application (ADR-0070), declared to the Referential
/// catalogue by <c>AddCustomersModule()</c>; an agency may tune them.
/// </summary>
public static class CustomerAccountSettings
{
    /// <summary>Wrong passwords in a row that lock an account (5).</summary>
    public static readonly SettingDefinition<int> LockoutFailures =
        SettingDefinitions.WholeNumber("customer_accounts.lockout_failures", 5, 3, 20);

    /// <summary>How long an account stays locked (15 minutes).</summary>
    public static readonly SettingDefinition<TimeSpan> LockoutDuration =
        SettingDefinitions.Duration("customer_accounts.lockout_duration", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));

    /// <summary>How long the application stays signed in without the customer typing the password again (30 days).</summary>
    public static readonly SettingDefinition<TimeSpan> RefreshTokenLifetime =
        SettingDefinitions.Duration("customer_accounts.refresh_token_lifetime", TimeSpan.FromDays(30), TimeSpan.FromHours(1), TimeSpan.FromDays(90));

    /// <summary>Validity of a code e-mailed to the customer (15 minutes).</summary>
    public static readonly SettingDefinition<TimeSpan> CodeValidity =
        SettingDefinitions.Duration("customer_accounts.code_validity", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));

    /// <summary>A customer books from the application only once the e-mail address of the account is verified (true).</summary>
    public static readonly SettingDefinition<bool> RequireVerifiedEmailToBook =
        SettingDefinitions.Boolean("customer_accounts.require_verified_email_to_book", true);

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        LockoutFailures,
        LockoutDuration,
        RefreshTokenLifetime,
        CodeValidity,
        RequireVerifiedEmailToBook,
    ];
}
