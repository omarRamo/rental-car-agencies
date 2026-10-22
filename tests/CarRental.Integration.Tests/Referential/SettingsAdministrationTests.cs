using System.Net;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Administration;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using CarRental.Web.Components.Admin.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Referential;

/// <summary>The settings screen's service on SQL Server: agency values, platform values, audit, history and outbox.</summary>
public sealed class SettingsAdministrationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agency_value_replaces_the_platform_value_for_that_agency_until_it_is_removed()
    {
        var agencyId = await NewAgencyAsync();
        var manager = await ManagerAsync(agencyId);
        var buffer = WellKnownSettings.PreparationBuffer;

        await using (var scope = host.CreateScope(manager))
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsAdministrationService>();
            (await settings.SetAgencyValueAsync(buffer.Key, agencyId, "03:00:00", Ct)).IsSuccess.ShouldBeTrue();
        }

        (await ReadAsync(buffer, agencyId)).ShouldBe(TimeSpan.FromHours(3));
        (await ReadAsync(buffer, agencyId: null)).ShouldBe(TimeSpan.FromHours(2));

        await using (var scope = host.CreateScope(manager))
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsAdministrationService>();
            var overview = (await settings.GetOverviewAsync(Ct)).Value;
            overview.Agencies.ShouldContain(a => a.Id == agencyId);
            overview.Settings.Single(s => s.Key == buffer.Key).AgencyValues[agencyId].ShouldBe("03:00:00");

            (await settings.RemoveAgencyValueAsync(buffer.Key, agencyId, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await ReadAsync(buffer, agencyId)).ShouldBe(TimeSpan.FromHours(2));

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var events = await db.Set<AuditLog>()
            .Where(a => a.Details != null && a.Details.StartsWith($"{buffer.Key} [") && a.UserId == manager.UserId)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Action, a.Details })
            .ToListAsync(Ct);
        events.Select(e => e.Action).ShouldBe([ReferentialAuditActions.SettingChanged, ReferentialAuditActions.SettingReset]);
        events[0].Details.ShouldEndWith("]: — → 03:00:00");
        events[1].Details.ShouldEndWith("]: 03:00:00 → — (platform)");
        (await db.Set<AuditLog>().CountAsync(a => a.EntityType == nameof(Setting) && a.UserId == manager.UserId
            && (a.Action == AuditActions.EntityCreated || a.Action == AuditActions.EntityDeleted), Ct)).ShouldBe(2);
        (await db.Set<OutboxMessage>().AnyAsync(m => m.Type == typeof(SettingRemoved).FullName && m.Payload.Contains(agencyId.ToString()), Ct))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task A_platform_value_is_edited_reset_and_kept_in_the_history()
    {
        var manager = await SupportAsync();

        await using (var scope = host.CreateScope(manager))
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsAdministrationService>();
            (await settings.SetPlatformValueAsync(TestSettings.Delay.Key, "2 hours", Ct)).Error.Code.ShouldBe("referential.setting_value_invalid");
            (await settings.SetPlatformValueAsync(TestSettings.Delay.Key, "03:15:00", Ct)).IsSuccess.ShouldBeTrue();
        }

        (await ReadAsync(TestSettings.Delay, agencyId: null)).ShouldBe(new TimeSpan(3, 15, 0));

        await using (var scope = host.CreateScope(manager))
        {
            (await scope.ServiceProvider.GetRequiredService<SettingsAdministrationService>().ResetPlatformValueAsync(TestSettings.Delay.Key, Ct))
                .IsSuccess.ShouldBeTrue();
        }

        (await ReadAsync(TestSettings.Delay, agencyId: null)).ShouldBe(TimeSpan.FromHours(2));
        await using var check = host.CreateScope();
        var values = await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Setting>()
            .TemporalAll()
            .Where(s => s.Key == TestSettings.Delay.Key && s.AgencyId == null)
            .Select(s => s.Value)
            .ToListAsync(Ct);
        values.ShouldContain("03:15:00");
        values.ShouldContain("02:00:00");
    }

    [Fact]
    public async Task A_platform_only_setting_ignores_agency_rows_and_refuses_new_ones()
    {
        var agencyId = await NewAgencyAsync();
        var retention = WellKnownSettings.OutboxProcessedRetention;
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Setting>().Add(Setting.Create(retention.Key, "2.00:00:00", agencyId).Value);
            await db.SaveChangesAsync(Ct);
        }

        (await ReadAsync(retention, agencyId)).ShouldBe(TimeSpan.FromDays(30));

        await using var check = host.CreateScope(await ManagerAsync(agencyId));
        (await check.ServiceProvider.GetRequiredService<SettingsAdministrationService>().SetAgencyValueAsync(retention.Key, agencyId, "3.00:00:00", Ct))
            .Error.ShouldBe(SettingErrors.PlatformOnly);
    }

    [Fact]
    public void Duration_editors_convert_to_their_largest_exact_unit_without_overflow()
    {
        DurationUnits.Split(TimeSpan.FromHours(2)).ShouldBe((2, DurationUnit.Hours));
        DurationUnits.Split(TimeSpan.FromHours(36)).ShouldBe((36, DurationUnit.Hours));
        DurationUnits.Split(TimeSpan.FromDays(30)).ShouldBe((30, DurationUnit.Days));
        DurationUnits.Split(TimeSpan.FromMinutes(59)).ShouldBe((59, DurationUnit.Minutes));
        DurationUnits.Split(TimeSpan.Zero).ShouldBe((0, DurationUnit.Minutes));
        DurationUnits.Combine(3, DurationUnit.Hours).ShouldBe(TimeSpan.FromHours(3));
        DurationUnits.Combine(int.MaxValue, DurationUnit.Days).ShouldBe(TimeSpan.MaxValue);
        DurationUnits.MaximumAmount(DurationUnit.Days).ShouldBe(365);
        DurationUnits.MaximumAmount(DurationUnit.Hours).ShouldBe(365 * 24);
        WellKnownSettings.PreparationBuffer.Validate(SettingValues.FormatDuration(DurationUnits.Combine(int.MaxValue, DurationUnit.Days))).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_settings_managers_read_and_change_settings()
    {
        var (accountantId, accountantEmail) = await TestStaff.CreateAsync(host, Roles.Accountant);
        var accountant = await TestStaff.ActorAsync(host, accountantId, accountantEmail, Roles.Accountant);
        await using var scope = host.CreateScope(accountant);
        var settings = scope.ServiceProvider.GetRequiredService<SettingsAdministrationService>();

        (await settings.GetOverviewAsync(Ct)).Error.ShouldBe(SettingErrors.Forbidden);
        (await settings.SetPlatformValueAsync(TestSettings.Motto.Key, "Hacked", Ct)).Error.ShouldBe(SettingErrors.Forbidden);
    }

    [Fact]
    public async Task The_settings_screen_is_reserved_to_settings_managers()
    {
        var (_, managerEmail) = await TestStaff.CreateAsync(host, Roles.Manager);
        var (_, accountantEmail) = await TestStaff.CreateAsync(host, Roles.Accountant);
        using var managerClient = host.CreateClient();
        using var accountantClient = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(managerClient, managerEmail, "/admin", Ct);
        await HttpSignIn.SignInWithTwoFactorAsync(accountantClient, accountantEmail, "/admin", Ct);

        var managerPage = await managerClient.GetAsync(new Uri("/admin/settings", UriKind.Relative), Ct);
        var accountantPage = await accountantClient.GetAsync(new Uri("/admin/settings", UriKind.Relative), Ct);

        managerPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await managerPage.Content.ReadAsStringAsync(Ct)).ShouldNotContain("À venir");
        accountantPage.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(accountantPage.Headers.Location!).ShouldContain("/account/access-denied");
    }

    private async Task<T> ReadAsync<T>(SettingDefinition<T> definition, Guid? agencyId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsReader>().GetAsync(definition, agencyId, Ct);
    }

    /// <summary>A manager of <paramref name="agencyId"/> (the main demo agency by default).</summary>
    private async Task<UserContextSnapshot> ManagerAsync(Guid? agencyId = null)
    {
        var (id, email) = await TestStaff.CreateAsync(host, Roles.Manager, agencyId: agencyId);
        return await TestStaff.ActorAsync(host, id, email, Roles.Manager, "198.51.100.30");
    }

    /// <summary>The support, across agencies: the only one who changes the platform values (ADR-0067).</summary>
    private async Task<UserContextSnapshot> SupportAsync()
    {
        var (id, email) = await TestStaff.CreateAsync(host, Roles.Support);
        return await TestStaff.ActorAsync(host, id, email, Roles.Support, "198.51.100.30");
    }

    /// <summary>An agency of its own: parallel tests read the settings of the demo agency.</summary>
    private async Task<Guid> NewAgencyAsync()
    {
        var agency = Agency.Register(new AgencyDetails($"Agence {Guid.NewGuid():N}", "Sousse")).Value;
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<Agency>().Add(agency);
        await db.SaveChangesAsync(Ct);
        return agency.Id;
    }
}
