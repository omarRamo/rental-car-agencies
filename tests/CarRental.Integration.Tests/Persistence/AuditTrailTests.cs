using System.Text.Json;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Persistence;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Persistence;

/// <summary>Entity changes are audited in the same save; the audit log is append-only (interceptor and database trigger).</summary>
public sealed class AuditTrailTests(IntegrationHost host)
{
    private static readonly UserContextSnapshot Actor = UserContextSnapshot.ForUser(
        Guid.CreateVersion7(), "auditor@tests.local", [], "198.51.100.23", "corr-audit");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Agency NewAgency(string? name = null) =>
        Agency.Register(new AgencyDetails(name ?? $"Agence {Guid.NewGuid():N}", "Sousse")).Value;

    [Fact]
    public async Task Creating_an_entity_writes_an_audit_row_with_actor_and_values()
    {
        var agency = NewAgency();
        await using (var scope = host.CreateScope(Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Add(agency);
            await db.SaveChangesAsync(Ct);
        }

        var entry = await SingleAuditAsync(agency.Id, AuditActions.EntityCreated);

        entry.EntityType.ShouldBe("Agency");
        entry.UserId.ShouldBe(Actor.UserId);
        entry.UserName.ShouldBe("auditor@tests.local");
        entry.IpAddress.ShouldBe("198.51.100.23");
        entry.CorrelationId.ShouldBe("corr-audit");
        entry.Before.ShouldBeNull();
        var after = JsonDocument.Parse(entry.After!).RootElement;
        after.GetProperty("Name").GetString().ShouldBe(agency.Name);
        after.GetProperty("City").GetString().ShouldBe("Sousse");
        after.TryGetProperty("RowVersion", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Updating_records_only_the_changed_values_before_and_after()
    {
        var agency = NewAgency();
        await using (var scope = host.CreateScope(Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Add(agency);
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.CreateScope(Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var loaded = await db.Set<Agency>().SingleAsync(a => a.Id == agency.Id, Ct);
            loaded.UpdateDetails(new AgencyDetails(loaded.Name, "Monastir")).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        var entry = await SingleAuditAsync(agency.Id, AuditActions.EntityUpdated);

        entry.Before.ShouldBe("""{"City":"Sousse"}""");
        entry.After.ShouldBe("""{"City":"Monastir"}""");
    }

    [Fact]
    public async Task Removing_a_soft_deletable_entity_marks_it_deleted_and_hides_it()
    {
        var agency = NewAgency();
        await using (var scope = host.CreateScope(Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Add(agency);
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.CreateScope(Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Remove(await db.Set<Agency>().SingleAsync(a => a.Id == agency.Id, Ct));
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            (await db.Set<Agency>().AnyAsync(a => a.Id == agency.Id, Ct)).ShouldBeFalse();
            var stored = await db.Set<Agency>().IgnoreQueryFilters([ModelConventions.SoftDeleteFilter]).SingleAsync(a => a.Id == agency.Id, Ct);
            stored.IsDeleted.ShouldBeTrue();
            stored.DeletedAtUtc.ShouldNotBeNull();
        }

        var entry = await SingleAuditAsync(agency.Id, AuditActions.EntitySoftDeleted);
        JsonDocument.Parse(entry.After!).RootElement.GetProperty("IsDeleted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Audit_rows_cannot_be_modified_or_deleted_through_ef_core()
    {
        var agency = NewAgency();
        await using (var scope = host.CreateScope(Actor))
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Add(agency);
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var entry = await db.Set<AuditLog>().FirstAsync(a => a.EntityId == agency.Id.ToString(), Ct);
            db.Entry(entry).Property(nameof(AuditLog.Details)).CurrentValue = "tampered";
            await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

            db.ChangeTracker.Clear();
            db.Set<AuditLog>().Remove(await db.Set<AuditLog>().FirstAsync(a => a.EntityId == agency.Id.ToString(), Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task The_database_rejects_updates_and_deletes_of_audit_rows()
    {
        await using var connection = new SqlConnection(host.Factory.SqlConnectionString);
        await connection.OpenAsync(Ct);

        await using var update = new SqlCommand("UPDATE [audit].[AuditLogs] SET [Details] = N'tampered'", connection);
        var updateError = await Should.ThrowAsync<SqlException>(() => update.ExecuteNonQueryAsync(Ct));
        updateError.Number.ShouldBe(51000);

        await using var delete = new SqlCommand("DELETE FROM [audit].[AuditLogs]", connection);
        (await Should.ThrowAsync<SqlException>(() => delete.ExecuteNonQueryAsync(Ct))).Number.ShouldBe(51000);

        await using var deleteAccess = new SqlCommand("DELETE FROM [audit].[DocumentAccessLogs]", connection);
        (await Should.ThrowAsync<SqlException>(() => deleteAccess.ExecuteNonQueryAsync(Ct))).Number.ShouldBe(51000);
    }

    [Fact]
    public async Task Secrets_are_masked_in_the_audit_log()
    {
        var email = $"secret-{Guid.NewGuid():N}@demo.local";
        Guid userId;
        await using (var scope = host.CreateScope(Actor))
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Secret", CreatedAtUtc = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, "Secret-Passw0rd-2026")).Succeeded.ShouldBeTrue(); // gitleaks:allow (test value)
            await users.ResetAuthenticatorKeyAsync(user);
            userId = user.Id;
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var created = await db.Set<AuditLog>().SingleAsync(a => a.Action == AuditActions.EntityCreated && a.EntityType == "User" && a.EntityId == userId.ToString(), Ct);
        var values = JsonDocument.Parse(created.After!).RootElement;
        values.GetProperty("PasswordHash").GetString().ShouldBe("***");
        values.GetProperty("Email").GetString().ShouldBe(email);
        values.TryGetProperty("ConcurrencyStamp", out _).ShouldBeFalse();

        var token = await db.Set<AuditLog>().FirstAsync(a => a.EntityType == "UserToken" && a.EntityId!.StartsWith(userId.ToString()), Ct);
        JsonDocument.Parse(token.After!).RootElement.GetProperty("Value").GetString().ShouldBe("***");
    }

    [Fact]
    public async Task Explicit_events_are_recorded_by_the_audit_trail()
    {
        var subject = Guid.CreateVersion7().ToString();
        await using (var scope = host.CreateScope(Actor))
        {
            var trail = scope.ServiceProvider.GetRequiredService<CarRental.Application.Common.Auditing.IAuditTrail>();
            await trail.RecordAsync(new CarRental.Application.Common.Auditing.AuditEvent("tests.explicit_event") { EntityType = "Test", EntityId = subject, Details = "context" }, Ct);
        }

        var entry = await SingleAuditAsync(subject, "tests.explicit_event");
        entry.Details.ShouldBe("context");
        entry.UserName.ShouldBe("auditor@tests.local");
    }

    private async Task<AuditLog> SingleAuditAsync(Guid entityId, string action) => await SingleAuditAsync(entityId.ToString(), action);

    private async Task<AuditLog> SingleAuditAsync(string entityId, string action)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        return await db.Set<AuditLog>().AsNoTracking().SingleAsync(a => a.EntityId == entityId && a.Action == action, Ct);
    }
}
