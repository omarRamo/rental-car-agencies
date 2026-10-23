using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CarRental.Domain.Common;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CarRental.Infrastructure.Persistence.Auditing;

/// <summary>Builds <see cref="AuditLog"/> rows (with before/after JSON) from change tracker entries.</summary>
internal static class AuditEntryFactory
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>
    /// The audit row of a change, for the agency of the changed row (ADR-0067): its own agency, the agency itself; for an
    /// account and the Identity rows of a user (claims, logins, tokens, roles: <paramref name="agencyOfUser"/>), the agency
    /// of that user — the platform for the support, whatever agency the operation works for; else
    /// <paramref name="operationAgencyId"/> (the agency of the operation, none for the platform).
    /// </summary>
    public static AuditLog? Create(
        EntityEntry entry,
        AuditActor actor,
        DateTimeOffset now,
        Guid? operationAgencyId = null,
        Func<Guid, Guid?>? agencyOfUser = null)
    {
        var entityType = entry.Metadata;
        string action;
        string? before = null;
        string? after = null;

        switch (entry.State)
        {
            case EntityState.Added:
                action = AuditActions.EntityCreated;
                after = Serialize(ValuesOf(entry, original: false, changedOnly: false));
                break;

            case EntityState.Deleted:
                action = AuditActions.EntityDeleted;
                before = Serialize(ValuesOf(entry, original: true, changedOnly: false));
                break;

            case EntityState.Modified:
                var changed = ValuesOf(entry, original: false, changedOnly: true).ToList();
                if (changed.Count == 0)
                {
                    return null;
                }

                action = IsSoftDeletion(entry) ? AuditActions.EntitySoftDeleted : AuditActions.EntityUpdated;
                before = Serialize(ValuesOf(entry, original: true, changedOnly: true));
                after = Serialize(changed);
                break;

            default:
                return null;
        }

        return AuditLog.Record(now, actor, action, entityType.GetAuditName(), KeyOf(entry), before, after, agencyId: AgencyOf(entry, operationAgencyId, agencyOfUser));
    }

    private static Guid? AgencyOf(EntityEntry entry, Guid? operationAgencyId, Func<Guid, Guid?>? agencyOfUser) => entry.Entity switch
    {
        IAgencyOwned owned => owned.AgencyId,
        IAgencyShared shared => shared.AgencyId,
        IAgencyOrPlatform record => record.AgencyId,
        Agency agency => agency.Id,

        // An account is the agency's of its user, the platform's for the support (no agency), even while the support works
        // inside an agency: never the agency of the operation.
        ApplicationUser user => user.AgencyId,
        _ when UserIdOf(entry) is { } userId => agencyOfUser?.Invoke(userId),
        _ => operationAgencyId,
    };

    /// <summary>The user an Identity row belongs to (its foreign key to the account), or <see langword="null"/>.</summary>
    private static Guid? UserIdOf(EntityEntry entry)
    {
        var foreignKey = entry.Metadata.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(ApplicationUser) && fk.Properties.Count == 1);
        if (foreignKey is null)
        {
            return null;
        }

        var property = entry.Property(foreignKey.Properties[0].Name);
        return (entry.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue) as Guid?;
    }

    private static bool IsSoftDeletion(EntityEntry entry) =>
        entry.Entity is ISoftDeletable
        && entry.Property(nameof(ISoftDeletable.IsDeleted)) is { IsModified: true } isDeleted
        && isDeleted.OriginalValue is false
        && isDeleted.CurrentValue is true;

    /// <summary>Primary key as text (composite keys joined with '|'), or null while the database has not generated it.</summary>
    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return null;
        }

        var parts = new List<string>(key.Properties.Count);
        foreach (var property in key.Properties)
        {
            var propertyEntry = entry.Property(property.Name);
            if (propertyEntry.IsTemporary)
            {
                return null;
            }

            parts.Add(Convert.ToString(propertyEntry.CurrentValue, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return string.Join('|', parts);
    }

    private static IEnumerable<(string Name, object? Value, bool Sensitive)> ValuesOf(EntityEntry entry, bool original, bool changedOnly)
    {
        foreach (var value in PropertyValues(entry.Properties, prefix: null, entry.State, original, changedOnly))
        {
            yield return value;
        }

        foreach (var complex in entry.ComplexProperties)
        {
            foreach (var value in ComplexValues(complex, complex.Metadata.Name, entry.State, original, changedOnly))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<(string Name, object? Value, bool Sensitive)> ComplexValues(
        ComplexPropertyEntry complex,
        string prefix,
        EntityState state,
        bool original,
        bool changedOnly)
    {
        // An optional complex property that is null has no nested values (original values are read per property).
        if (!original && complex.CurrentValue is null)
        {
            yield break;
        }

        foreach (var value in PropertyValues(complex.Properties, prefix, state, original, changedOnly))
        {
            yield return value;
        }

        foreach (var nested in complex.ComplexProperties)
        {
            foreach (var value in ComplexValues(nested, $"{prefix}.{nested.Metadata.Name}", state, original, changedOnly))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<(string Name, object? Value, bool Sensitive)> PropertyValues(
        IEnumerable<PropertyEntry> properties,
        string? prefix,
        EntityState state,
        bool original,
        bool changedOnly)
    {
        foreach (var property in properties)
        {
            var metadata = property.Metadata;
            if (metadata.IsAuditIgnored() || IsStoreGenerated(metadata, state) || metadata.ClrType == typeof(byte[]))
            {
                continue;
            }

            if (changedOnly && (!property.IsModified || Equals(property.OriginalValue, property.CurrentValue)))
            {
                continue;
            }

            var name = prefix is null ? metadata.Name : $"{prefix}.{metadata.Name}";
            yield return (name, ToStoredValue(metadata, original ? property.OriginalValue : property.CurrentValue), metadata.IsAuditSensitive());
        }
    }

    /// <summary>Values computed by the database (identity, rowversion, temporal periods) are not user changes.</summary>
    private static bool IsStoreGenerated(IProperty property, EntityState state) =>
        property.ValueGenerated == ValueGenerated.OnAddOrUpdate
        || (state == EntityState.Added && property.ValueGenerated == ValueGenerated.OnAdd && property.IsKey() && property.ClrType != typeof(Guid));

    private static object? ToStoredValue(IProperty property, object? value)
    {
        if (value is null or Enum)
        {
            return value;
        }

        var converter = property.GetTypeMapping().Converter;
        return converter is null ? value : converter.ConvertToProvider(value);
    }

    private static string Serialize(IEnumerable<(string Name, object? Value, bool Sensitive)> values)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var (name, value, sensitive) in values)
            {
                writer.WritePropertyName(name);
                if (sensitive && value is not null)
                {
                    writer.WriteStringValue(AuditAnnotations.MaskedValue);
                }
                else
                {
                    WriteValue(writer, value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case DateTimeOffset instant:
                writer.WriteStringValue(instant);
                break;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.WriteStringValue(time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan duration:
                writer.WriteStringValue(duration.ToString("c", CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case Enum enumValue:
                writer.WriteStringValue(enumValue.ToString());
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
