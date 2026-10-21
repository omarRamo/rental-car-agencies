using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Referential;

/// <summary>
/// Settings are read from an in-memory snapshot (ADR-0044): every committed change is seen at once, whatever wrote it;
/// an uncommitted change is seen only by its own transaction and is never cached.
/// </summary>
public sealed class SettingsCacheTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_value_written_directly_in_the_database_is_read_at_once()
    {
        var agencyId = await NewAgencyAsync();
        var platform = await ReadAsync(agencyId);

        await WriteAsync(agencyId, "Valeur une");
        (await ReadAsync(agencyId)).ShouldBe("Valeur une");

        await WriteAsync(agencyId, "Valeur deux");
        (await ReadAsync(agencyId)).ShouldBe("Valeur deux");

        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Setting>().Remove(await db.Set<Setting>().SingleAsync(s => s.Key == TestSettings.Motto.Key && s.AgencyId == agencyId, Ct));
            await db.SaveChangesAsync(Ct);
        }

        (await ReadAsync(agencyId)).ShouldBe(platform);
    }

    [Fact]
    public async Task A_transaction_sees_its_own_change_and_a_rolled_back_change_is_never_cached()
    {
        var agencyId = await NewAgencyAsync();
        await WriteAsync(agencyId, "Validée");
        (await ReadAsync(agencyId)).ShouldBe("Validée"); // warm the cache

        string? seenInside = null;
        await using (var scope = host.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var outcome = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                    var setting = await db.Set<Setting>().SingleAsync(s => s.Key == TestSettings.Motto.Key && s.AgencyId == agencyId, ct);
                    setting.ChangeValue("Non validée").IsSuccess.ShouldBeTrue();
                    await db.SaveChangesAsync(ct);
                    seenInside = await scope.ServiceProvider.GetRequiredService<ISettingsReader>().GetAsync(TestSettings.Motto, agencyId, ct);
                    return Result.Failure(Error.Conflict("tests.rollback", "Rolled back on purpose."));
                },
                cancellationToken: Ct);
            outcome.IsFailure.ShouldBeTrue();
        }

        seenInside.ShouldBe("Non validée");
        (await ReadAsync(agencyId)).ShouldBe("Validée");
    }

    private async Task<string> ReadAsync(Guid agencyId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsReader>().GetAsync(TestSettings.Motto, agencyId, Ct);
    }

    private async Task WriteAsync(Guid agencyId, string value)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var existing = await db.Set<Setting>().SingleOrDefaultAsync(s => s.Key == TestSettings.Motto.Key && s.AgencyId == agencyId, Ct);
        if (existing is null)
        {
            db.Set<Setting>().Add(Setting.Create(TestSettings.Motto.Key, value, agencyId).Value);
        }
        else
        {
            existing.ChangeValue(value).IsSuccess.ShouldBeTrue();
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> NewAgencyAsync()
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = Agency.Register(new AgencyDetails($"Agence cache {Guid.NewGuid():N}", "Tunis")).Value;
        db.Set<Agency>().Add(agency);
        await db.SaveChangesAsync(Ct);
        return agency.Id;
    }
}
