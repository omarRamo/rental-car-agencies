using CarRental.Application.Cash.Seeding;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarRental.Application.Tests.Cash;

/// <summary>Demo cash sessions: yesterday closed and locked with a justified variance, today open; idempotent.</summary>
public sealed class CashDemoSeederTests
{
    private readonly FinanceHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly UserContactInfo _agent = new(Guid.CreateVersion7(), "Agent Démo", CashDemoSeeder.DemoAgentEmail);
    private readonly UserContactInfo _manager = new(Guid.CreateVersion7(), "Gérant Démo", CashDemoSeeder.DemoManagerEmail);

    private CashDemoSeeder Seeder() =>
        new(_harness.Db, _harness.Referential, _harness.Identity, _harness.Time, NullLogger<CashDemoSeeder>.Instance);

    private void AddDemoUsers()
    {
        _harness.Identity.UsersInRoles.Add((Roles.Agent, _agent));
        _harness.Identity.UsersInRoles.Add((Roles.Manager, _manager));
    }

    [Fact]
    public async Task The_demo_agent_gets_a_locked_session_yesterday_and_an_open_session_today()
    {
        AddDemoUsers();
        var seeder = Seeder();

        await seeder.SeedAsync(Ct);

        seeder.Stage.ShouldBe(SeedStage.Demo);
        seeder.Order.ShouldBe(800);
        var sessions = _harness.Db.Rows<CashSession>().OrderBy(s => s.BusinessDate).ToList();
        sessions.Count.ShouldBe(2);
        var yesterday = sessions[0];
        yesterday.BusinessDate.ShouldBe(new DateOnly(2027, 6, 27));
        yesterday.Status.ShouldBe(CashSessionStatus.Locked);
        yesterday.CashierId.ShouldBe(_agent.UserId);
        yesterday.CashierName.ShouldBe("Agent Démo");
        yesterday.ExpectedRevenueCash.ShouldBe(Money.Tnd(214.5m));
        yesterday.RevenueVariance.ShouldBe(Money.Tnd(-1m));
        yesterday.VarianceJustification.ShouldNotBeNullOrWhiteSpace();
        yesterday.LockedByUserId.ShouldBe(_manager.UserId);
        var today = sessions[1];
        today.BusinessDate.ShouldBe(new DateOnly(2027, 6, 28));
        today.Status.ShouldBe(CashSessionStatus.Open);
        today.OpenedAtUtc.ShouldBeLessThanOrEqualTo(_harness.Time.GetUtcNow());
        today.ExpectedRevenueCash.ShouldBe(Money.Tnd(170m));
        sessions.ShouldAllBe(s => s.AgencyId == _harness.AgencyId);
        _harness.Db.Saves.ShouldBe(1);
    }

    [Fact]
    public async Task Seeding_again_adds_nothing()
    {
        AddDemoUsers();
        await Seeder().SeedAsync(Ct);
        _harness.Time.Advance(TimeSpan.FromDays(3));

        await Seeder().SeedAsync(Ct);

        _harness.Db.Rows<CashSession>().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Without_the_demo_users_nothing_is_seeded()
    {
        await Seeder().SeedAsync(Ct);

        _harness.Db.Rows<CashSession>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Seeded_before_the_opening_hour_today_opens_now()
    {
        // 06:00 in Tunis, before the 08:30 opening of the demo session.
        var early = new FinanceHarness(new DateTimeOffset(2027, 6, 28, 5, 0, 0, TimeSpan.Zero));
        early.Identity.UsersInRoles.Add((Roles.Agent, _agent));
        early.Identity.UsersInRoles.Add((Roles.Manager, _manager));

        await new CashDemoSeeder(early.Db, early.Referential, early.Identity, early.Time, NullLogger<CashDemoSeeder>.Instance).SeedAsync(Ct);

        early.Db.Rows<CashSession>().Single(s => s.Status == CashSessionStatus.Open).OpenedAtUtc.ShouldBe(early.Time.GetUtcNow());
    }
}
