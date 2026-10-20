using CarRental.Application.Common.Availability;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Administration;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Referential;

namespace CarRental.Application.Tests.Referential;

public sealed class SettingsAdministrationServiceTests
{
    private readonly InMemoryAppDbContext _db = new();
    private readonly RecordingAuditTrail _audit = new();
    private readonly InlineUnitOfWork _unitOfWork = new();
    private readonly CountingAvailabilityNotifier _availability = new();
    private readonly SettingDefinitionCatalog _catalog = new(WellKnownSettings.All.Select(d => new SettingDefinitionRegistration(d)));
    private readonly Agency _agency = Agency.Register(new AgencyDetails("Agence Sousse", "Sousse")).Value;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SettingsAdministrationServiceTests()
    {
        _db.Rows<Agency>().Add(_agency);
        foreach (var definition in WellKnownSettings.All)
        {
            _db.Rows<Setting>().Add(Setting.Create(definition.Key, definition.DefaultValue).Value);
        }
    }

    private static string PreparationBuffer => WellKnownSettings.PreparationBuffer.Key;

    private SettingsAdministrationService AsRole(string role) =>
        new(_db, _unitOfWork, _catalog, TestUsers.ContextFor(Guid.CreateVersion7(), $"{role}@demo.local", role), _audit, _availability);

    private SettingsAdministrationService AsManager() => AsRole(Roles.Manager);

    [Fact]
    public async Task Only_settings_managers_read_and_change_settings()
    {
        var service = AsRole(Roles.Accountant);

        (await service.GetOverviewAsync(Ct)).Error.ShouldBe(SettingErrors.Forbidden);
        (await service.SetPlatformValueAsync(PreparationBuffer, "03:00:00", Ct)).Error.ShouldBe(SettingErrors.Forbidden);
        (await service.ResetPlatformValueAsync(PreparationBuffer, Ct)).Error.ShouldBe(SettingErrors.Forbidden);
        (await service.SetAgencyValueAsync(PreparationBuffer, _agency.Id, "03:00:00", Ct)).Error.ShouldBe(SettingErrors.Forbidden);
        (await service.RemoveAgencyValueAsync(PreparationBuffer, _agency.Id, Ct)).Error.ShouldBe(SettingErrors.Forbidden);
        _audit.Events.ShouldBeEmpty();
        _availability.AllChangedCalls.ShouldBe(0);
    }

    [Fact]
    public async Task The_overview_lists_every_declared_setting_with_its_values()
    {
        _db.Rows<Setting>().Add(Setting.Create(PreparationBuffer, "03:00:00", _agency.Id).Value);
        PlatformRow(WellKnownSettings.VatRate.Key).ChangeValue("0.07");

        var overview = (await AsManager().GetOverviewAsync(Ct)).Value;

        overview.Agencies.ShouldHaveSingleItem().ShouldBe(new SettingsAgency(_agency.Id, "Agence Sousse", IsActive: true));
        overview.Settings.Select(s => s.Key).ShouldBe(_catalog.All.Select(d => d.Key));
        var buffer = overview.Settings.Single(s => s.Key == PreparationBuffer);
        buffer.Group.ShouldBe("fleet");
        buffer.Editor.Kind.ShouldBe(SettingKind.Duration);
        buffer.DefaultValue.ShouldBe("02:00:00");
        buffer.PlatformValue.ShouldBe("02:00:00");
        buffer.AgencyValues.ShouldBe(new Dictionary<Guid, string> { [_agency.Id] = "03:00:00" });
        buffer.EffectiveFor(_agency.Id).ShouldBe("03:00:00");
        buffer.EffectiveFor(Guid.CreateVersion7()).ShouldBe("02:00:00");
        overview.Settings.Single(s => s.Key == WellKnownSettings.VatRate.Key).PlatformOrDefault.ShouldBe("0.07");
    }

    [Fact]
    public async Task A_platform_value_is_validated_by_its_definition_audited_and_drops_the_availability_cache()
    {
        var service = AsManager();

        (await service.SetPlatformValueAsync(PreparationBuffer, "2 h", Ct)).Error.Code.ShouldBe("referential.setting_value_invalid");
        (await service.SetPlatformValueAsync(PreparationBuffer, "400.00:00:00", Ct)).Error.Code.ShouldBe("referential.setting_value_invalid");
        _audit.Events.ShouldBeEmpty();

        (await service.SetPlatformValueAsync(PreparationBuffer, "03:30:00", Ct)).IsSuccess.ShouldBeTrue();

        PlatformRow(PreparationBuffer).Value.ShouldBe("03:30:00");
        var audit = _audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(ReferentialAuditActions.SettingChanged);
        audit.EntityType.ShouldBe("Setting");
        audit.EntityId.ShouldBe(PlatformRow(PreparationBuffer).Id.ToString());
        audit.Details.ShouldBe("fleet.preparation_buffer [platform]: 02:00:00 → 03:30:00");
        _availability.AllChangedCalls.ShouldBe(1);
        _unitOfWork.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task Setting_the_current_value_again_changes_nothing()
    {
        (await AsManager().SetPlatformValueAsync(PreparationBuffer, "02:00:00", Ct)).IsSuccess.ShouldBeTrue();

        _audit.Events.ShouldBeEmpty();
        _availability.AllChangedCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Reset_puts_the_platform_value_back_to_the_default()
    {
        PlatformRow(WellKnownSettings.VatRate.Key).ChangeValue("0.07");

        (await AsManager().ResetPlatformValueAsync(WellKnownSettings.VatRate.Key, Ct)).IsSuccess.ShouldBeTrue();

        PlatformRow(WellKnownSettings.VatRate.Key).Value.ShouldBe("0.19");
        var audit = _audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(ReferentialAuditActions.SettingReset);
        audit.Details.ShouldBe("billing.vat_rate [platform]: 0.07 → 0.19");
    }

    [Fact]
    public async Task A_missing_platform_row_is_created()
    {
        _db.Rows<Setting>().Remove(PlatformRow(WellKnownSettings.StampDuty.Key));

        (await AsManager().SetPlatformValueAsync(WellKnownSettings.StampDuty.Key, "0.600", Ct)).IsSuccess.ShouldBeTrue();

        PlatformRow(WellKnownSettings.StampDuty.Key).Value.ShouldBe("0.600");
        _audit.Events.ShouldHaveSingleItem().Details.ShouldBe("billing.stamp_duty [platform]: — → 0.600");
    }

    [Fact]
    public async Task An_agency_value_overrides_the_platform_value_for_that_agency()
    {
        var service = AsManager();

        (await service.SetAgencyValueAsync(PreparationBuffer, _agency.Id, "01:00:00", Ct)).IsSuccess.ShouldBeTrue();
        (await service.SetAgencyValueAsync(PreparationBuffer, _agency.Id, "01:30:00", Ct)).IsSuccess.ShouldBeTrue();

        var row = _db.Rows<Setting>().Where(s => s.Key == PreparationBuffer && s.AgencyId == _agency.Id).ShouldHaveSingleItem();
        row.Value.ShouldBe("01:30:00");
        PlatformRow(PreparationBuffer).Value.ShouldBe("02:00:00");
        _audit.Events.Select(e => e.Details).ShouldBe(
        [
            "fleet.preparation_buffer [Agence Sousse]: — → 01:00:00",
            "fleet.preparation_buffer [Agence Sousse]: 01:00:00 → 01:30:00",
        ]);
        _availability.AllChangedCalls.ShouldBe(2);
    }

    [Fact]
    public async Task An_agency_value_needs_an_existing_agency()
    {
        (await AsManager().SetAgencyValueAsync(PreparationBuffer, Guid.CreateVersion7(), "01:00:00", Ct)).Error.ShouldBe(SettingErrors.AgencyNotFound);

        _db.Rows<Setting>().ShouldNotContain(s => s.AgencyId != null);
    }

    [Fact]
    public async Task Removing_an_agency_value_brings_the_platform_value_back()
    {
        _db.Rows<Setting>().Add(Setting.Create(PreparationBuffer, "04:00:00", _agency.Id).Value);
        var service = AsManager();

        (await service.RemoveAgencyValueAsync(PreparationBuffer, _agency.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await service.RemoveAgencyValueAsync(PreparationBuffer, _agency.Id, Ct)).IsSuccess.ShouldBeTrue();

        _db.Rows<Setting>().ShouldNotContain(s => s.AgencyId == _agency.Id);
        var audit = _audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(ReferentialAuditActions.SettingReset);
        audit.Details.ShouldBe("fleet.preparation_buffer [Agence Sousse]: 04:00:00 → — (platform)");
        _availability.AllChangedCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Undeclared_keys_are_rejected()
    {
        var service = AsManager();

        (await service.SetPlatformValueAsync("unknown.key", "x", Ct)).Error.Code.ShouldBe("referential.setting_unknown");
        (await service.RemoveAgencyValueAsync("unknown.key", _agency.Id, Ct)).Error.Code.ShouldBe("referential.setting_unknown");
    }

    private Setting PlatformRow(string key) => _db.Rows<Setting>().Single(s => s.Key == key && s.AgencyId == null);

    private sealed class CountingAvailabilityNotifier : IAvailabilityChangeNotifier
    {
        public int AllChangedCalls { get; private set; }

        public void CategoryChanged(Guid categoryId)
        {
        }

        public void AllChanged() => AllChangedCalls++;
    }
}
