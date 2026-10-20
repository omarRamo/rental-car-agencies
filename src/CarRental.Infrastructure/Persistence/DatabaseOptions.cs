using System.ComponentModel.DataAnnotations;

namespace CarRental.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// From <c>ConnectionStrings:Sql</c>. In Azure: Entra authentication (<c>Authentication=Active Directory Default</c>),
    /// never a SQL password.
    /// </summary>
    [Required(ErrorMessage = "ConnectionStrings:Sql is required.")]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Applies pending migrations at startup. On in Development (and tests); off elsewhere, where the EF migrations bundle
    /// runs in the deployment pipeline before the new code goes live.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }
}
