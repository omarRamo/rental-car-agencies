using System.Net;
using System.Text.Json;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Identity;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Operations;

/// <summary>Outbox operations (ADR-0028): nightly purge, readiness entry, failed messages screen and audited retry.</summary>
public sealed class OutboxOperationsTests(IntegrationHost host)
{
    private const int MaxAttempts = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_purge_deletes_processed_messages_older_than_the_retention_and_keeps_the_others()
    {
        var now = DateTimeOffset.UtcNow;
        var old = await InsertAsync(occurredAt: now.AddDays(-40), processedAt: now.AddDays(-31));
        var recent = await InsertAsync(occurredAt: now.AddDays(-5), processedAt: now.AddDays(-4));
        var oldFailed = await InsertAsync(occurredAt: now.AddDays(-60), attempts: MaxAttempts, lastError: "old failure");

        try
        {
            await using (var scope = host.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<OutboxPurgeJob>().ExecuteAsync(Ct);
            }

            await using var check = host.CreateScope();
            var ids = await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
                .Where(m => m.Id == old || m.Id == recent || m.Id == oldFailed)
                .Select(m => m.Id)
                .ToListAsync(Ct);
            ids.ShouldBe([recent, oldFailed], ignoreOrder: true);
        }
        finally
        {
            await MarkProcessedAsync(oldFailed);
        }
    }

    [Fact]
    public async Task A_failed_message_turns_the_outbox_readiness_entry_degraded()
    {
        var failed = await InsertAsync(occurredAt: DateTimeOffset.UtcNow.AddMinutes(-3), attempts: MaxAttempts, lastError: "SMTP refused");
        try
        {
            using var client = host.CreateClient();

            var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            var outbox = JsonDocument.Parse(body).RootElement.GetProperty("checks").GetProperty("outbox");
            outbox.GetProperty("status").GetString().ShouldBe("Degraded", body);
            outbox.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("failed");
        }
        finally
        {
            await MarkProcessedAsync(failed);
        }
    }

    [Fact]
    public async Task The_support_sees_a_failed_message_and_retries_it_until_it_is_processed()
    {
        var agencyId = Guid.CreateVersion7();
        var failed = await InsertAsync(
            occurredAt: DateTimeOffset.UtcNow.AddHours(-20),
            attempts: MaxAttempts,
            lastError: "System.InvalidOperationException: smtp down",
            message: new AgencyRegistered(agencyId, $"Retry {agencyId:N}", $"retry-{agencyId:N}"));
        var manager = await SupportAsync();

        await using (var scope = host.CreateScope(manager))
        {
            var administration = scope.ServiceProvider.GetRequiredService<OutboxAdministrationService>();
            var overview = (await administration.GetOverviewAsync(Ct)).Value;
            overview.Statistics.FailedCount.ShouldBeGreaterThanOrEqualTo(1);
            overview.MaxAttempts.ShouldBe(MaxAttempts);

            var listed = await ListAllAsync(administration, OutboxListFilter.Failed);
            var entry = listed.Where(e => e.Id == failed).ShouldHaveSingleItem();
            entry.State.ShouldBe(OutboxEntryState.Failed);
            entry.AttemptCount.ShouldBe(MaxAttempts);
            entry.LastError.ShouldBe("System.InvalidOperationException: smtp down");
            entry.ShortType.ShouldBe(nameof(AgencyRegistered));
            entry.NextAttemptAtUtc.ShouldBeNull();
            (await ListAllAsync(administration, OutboxListFilter.Pending)).ShouldNotContain(e => e.Id == failed);

            (await administration.RetryAsync(failed, Ct)).IsSuccess.ShouldBeTrue();
        }

        await WaitUntilAsync(async () => await StateOfAsync(failed) is { ProcessedAtUtc: not null });
        host.Probe.Handled.ShouldContain(h => h.Event.AgencyId == agencyId);

        await using var check = host.CreateScope(manager);
        var audit = await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<AuditLog>()
            .SingleAsync(a => a.Action == AuditActions.OutboxMessageRetried && a.EntityId == failed.ToString(), Ct);
        audit.UserId.ShouldBe(manager.UserId);
        audit.Details.ShouldBe($"{typeof(AgencyRegistered).FullName} after {MaxAttempts} failed attempt(s)");
        (await check.ServiceProvider.GetRequiredService<OutboxAdministrationService>().RetryAsync(failed, Ct)).Error
            .ShouldBe(OutboxErrors.MessageNotFound);
    }

    [Fact]
    public async Task Only_the_support_reads_and_retries_messages()
    {
        var failed = await InsertAsync(occurredAt: DateTimeOffset.UtcNow, attempts: MaxAttempts, lastError: "boom");
        try
        {
            // The messages of every agency go through the outbox: not even an agency's manager reads them (ADR-0067).
            foreach (var role in new[] { Roles.Agent, Roles.Manager })
            {
                var (staffId, staffEmail) = await TestStaff.CreateAsync(host, role);
                await using var scope = host.CreateScope(await TestStaff.ActorAsync(host, staffId, staffEmail, role));
                var administration = scope.ServiceProvider.GetRequiredService<OutboxAdministrationService>();

                (await administration.GetOverviewAsync(Ct)).Error.ShouldBe(OutboxErrors.Forbidden);
                (await administration.ListAsync(OutboxListFilter.All, new PageRequest(), Ct)).Error.ShouldBe(OutboxErrors.Forbidden);
                (await administration.RetryAsync(failed, Ct)).Error.ShouldBe(OutboxErrors.Forbidden);
            }

            (await StateOfAsync(failed))!.AttemptCount.ShouldBe(MaxAttempts);
        }
        finally
        {
            await MarkProcessedAsync(failed);
        }
    }

    [Fact]
    public async Task The_outbox_screen_is_reserved_to_the_support()
    {
        var (_, supportEmail) = await TestStaff.CreateAsync(host, Roles.Support);
        var (_, managerEmail) = await TestStaff.CreateAsync(host, Roles.Manager);
        using var supportClient = host.CreateClient();
        using var managerClient = host.CreateClient();
        await HttpSignIn.SignInWithTwoFactorAsync(supportClient, supportEmail, "/admin", Ct);
        await HttpSignIn.SignInWithTwoFactorAsync(managerClient, managerEmail, "/admin", Ct);

        var supportPage = await supportClient.GetAsync(new Uri("/admin/outbox", UriKind.Relative), Ct);
        var managerPage = await managerClient.GetAsync(new Uri("/admin/outbox", UriKind.Relative), Ct);

        supportPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        managerPage.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(managerPage.Headers.Location!).ShouldContain("/account/access-denied");
    }

    private static async Task<IReadOnlyList<OutboxEntry>> ListAllAsync(OutboxAdministrationService administration, OutboxListFilter filter) =>
        (await administration.ListAsync(filter, new PageRequest(1, 100), Ct)).Value.Items;

    private async Task<UserContextSnapshot> SupportAsync()
    {
        var (id, email) = await TestStaff.CreateAsync(host, Roles.Support);
        return await TestStaff.ActorAsync(host, id, email, Roles.Support, "198.51.100.20");
    }

    /// <summary>
    /// Writes a message in the wanted state in one insert, so that the processor never sees it half-made. Without a
    /// message, the payload belongs to a type without handler (marked processed when it runs).
    /// </summary>
    private async Task<Guid> InsertAsync(
        DateTimeOffset occurredAt,
        DateTimeOffset? processedAt = null,
        int attempts = 0,
        string? lastError = null,
        AgencyRegistered? message = null)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<OutboxSerializer>();
        var outboxMessage = message is null
            ? OutboxMessage.Create("CarRental.Tests.UnhandledMessage", "{}", occurredAt, "corr-tests")
            : OutboxMessage.Create(typeof(AgencyRegistered).FullName!, serializer.Serialize(message), occurredAt, "corr-tests");
        if (processedAt is { } at)
        {
            outboxMessage.MarkProcessed(at);
        }

        var entry = db.Add(outboxMessage);
        entry.Property(m => m.AttemptCount).CurrentValue = attempts;
        entry.Property(m => m.LastError).CurrentValue = lastError;
        await db.SaveChangesAsync(Ct);
        return outboxMessage.Id;
    }

    /// <summary>Takes a failed message of a test out of the queue, so that the readiness entry recovers.</summary>
    private async Task MarkProcessedAsync(Guid messageId)
    {
        await using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
            .Where(m => m.Id == messageId && m.ProcessedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.ProcessedAtUtc, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private async Task<OutboxMessage?> StateOfAsync(Guid messageId)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, Ct);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!await condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "The retried message was not processed in time.");
            await Task.Delay(200, Ct);
        }
    }
}
