using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarRental.Infrastructure.Persistence.Auditing;

/// <summary>Model annotations read by the audit interceptor, set from the EF configurations of each module.</summary>
public static class AuditAnnotations
{
    /// <summary>The entity type is never audited (append-only logs, outbox, counters).</summary>
    public const string Exclude = "CarRental:Audit:Exclude";

    /// <summary>The value is replaced by <c>***</c> in the audit log (password hashes, tokens, secrets).</summary>
    public const string Sensitive = "CarRental:Audit:Sensitive";

    /// <summary>The property is left out of the audit log (technical noise such as concurrency stamps).</summary>
    public const string Ignore = "CarRental:Audit:Ignore";

    /// <summary>Name of the entity type in the audit log (defaults to the CLR type name).</summary>
    public const string Name = "CarRental:Audit:Name";

    public const string MaskedValue = "***";

    public static bool IsExcludedFromAudit(this IReadOnlyEntityType entityType) =>
        entityType.FindAnnotation(Exclude)?.Value is true;

    public static bool IsAuditSensitive(this IReadOnlyPropertyBase property) =>
        property.FindAnnotation(Sensitive)?.Value is true;

    public static bool IsAuditIgnored(this IReadOnlyPropertyBase property) =>
        property.FindAnnotation(Ignore)?.Value is true;

    public static string GetAuditName(this IReadOnlyEntityType entityType) =>
        entityType.FindAnnotation(Name)?.Value as string ?? entityType.ClrType.Name;
}

/// <summary>Fluent helpers used in entity configurations.</summary>
public static class AuditConfigurationExtensions
{
    public static EntityTypeBuilder<TEntity> ExcludeFromAudit<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasAnnotation(AuditAnnotations.Exclude, true);
    }

    public static EntityTypeBuilder<TEntity> HasAuditName<TEntity>(this EntityTypeBuilder<TEntity> builder, string name)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.HasAnnotation(AuditAnnotations.Name, name);
    }

    /// <summary>Masks the value in the audit log (a change is still recorded as <c>***</c>).</summary>
    public static PropertyBuilder<TProperty> IsAuditSensitive<TProperty>(this PropertyBuilder<TProperty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasAnnotation(AuditAnnotations.Sensitive, true);
    }

    /// <summary>Leaves the property out of the audit log.</summary>
    public static PropertyBuilder<TProperty> IsAuditIgnored<TProperty>(this PropertyBuilder<TProperty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasAnnotation(AuditAnnotations.Ignore, true);
    }
}
