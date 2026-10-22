using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Cash.Seeding;

/// <summary>
/// Demo cash sessions of the demo agent (development and recette only): yesterday's session, closed with a small
/// justified variance and locked by the demo manager, and today's session, open. Manual movements only: no payment is
/// seeded, so that no movement points to a booking that does not exist. Dates are relative to the first seeding day;
/// idempotent (nothing is added once the demo agent has a session).
/// </summary>
internal sealed partial class CashDemoSeeder(
    IAppDbContext db,
    IReferentialQueries referential,
    IIdentityQueries identity,
    TimeProvider timeProvider,
    ILogger<CashDemoSeeder> logger) : IDataSeeder
{
    /// <summary>Name of the demo agency created by the Referential demo seeder (order 200).</summary>
    public const string DemoAgencyName = "Agence Démo Tunis";

    /// <summary>Demo agent and manager created by the Identity demo seeder (order 300).</summary>
    public const string DemoAgentEmail = "agent@demo.local";

    public const string DemoManagerEmail = "gerant@demo.local";

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 800;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Name == DemoAgencyName);
        var agent = agency is null ? null : await FindAsync(agency.Id, Roles.Agent, DemoAgentEmail, cancellationToken);
        var manager = agency is null ? null : await FindAsync(agency.Id, Roles.Manager, DemoManagerEmail, cancellationToken);
        if (agency is null || agent is null || manager is null)
        {
            LogMissingDemoData();
            return;
        }

        if (await db.Set<CashSession>().AnyAsync(s => s.CashierId == agent.UserId, cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var today = TunisTime.DateOf(now);
        var yesterday = today.AddDays(-1);
        var agentActor = new AuditActor(agent.UserId, agent.Email, null, null);
        var managerActor = new AuditActor(manager.UserId, manager.Email, null, null);

        // Yesterday: float 200.000 + 50.000 − 35.500 = 214.500 expected, 213.500 counted.
        var previous = CashSession.Open(agency.Id, agent.UserId, agent.DisplayName, Money.Tnd(200m), At(yesterday, 8, 30)).Value;
        Ensure(previous.AddManualMovement(CashFlowDirection.In, Money.Tnd(50m), "Apport de monnaie de la banque", agentActor, At(yesterday, 9, 0)));
        Ensure(previous.AddManualMovement(CashFlowDirection.Out, Money.Tnd(35.5m), "Achat de fournitures de bureau", agentActor, At(yesterday, 15, 10)));
        Ensure(previous.Close(Money.Tnd(213.5m), Money.Zero, "Erreur de rendu de monnaie (1 dinar)", agentActor, At(yesterday, 19, 0)));
        Ensure(previous.Lock(managerActor, At(yesterday, 19, 30)));

        // Today: open since the morning (or since now, when seeded earlier in the day).
        var openedToday = At(today, 8, 30) < now ? At(today, 8, 30) : now;
        var current = CashSession.Open(agency.Id, agent.UserId, agent.DisplayName, Money.Tnd(150m), openedToday).Value;
        Ensure(current.AddManualMovement(CashFlowDirection.In, Money.Tnd(20m), "Monnaie pour le rendu", agentActor, openedToday));

        db.Set<CashSession>().AddRange(previous, current);
        await db.SaveChangesAsync(cancellationToken);
        LogSeeded(agent.Email);
    }

    private static DateTimeOffset At(DateOnly date, int hour, int minute) => TunisTime.FromLocal(date, new TimeOnly(hour, minute));

    private static void Ensure(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo cash data could not be built: {result.Error}.");
        }
    }

    private async Task<UserContactInfo?> FindAsync(Guid agencyId, string role, string email, CancellationToken cancellationToken) =>
        (await identity.GetActiveUsersInRoleAsync(agencyId, role, cancellationToken))
            .FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo agency, agent or manager not found: no demo cash session.")]
    private partial void LogMissingDemoData();

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo cash sessions created for {Agent}.")]
    private partial void LogSeeded(string agent);
}
