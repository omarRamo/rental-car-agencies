using System.Text.Json;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Persistence;

/// <summary>Domain events reach the outbox atomically and are dispatched by Hangfire (immediate trigger after commit).</summary>
public sealed class OutboxTests(IntegrationHost host)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Domain_events_are_stored_with_the_entity_and_processed()
    {
        var agency = Agency.Register(new AgencyDetails($"Outbox {Guid.NewGuid():N}", "Tunis")).Value;
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Add(agency);
            await db.SaveChangesAsync(Ct);
        }

        var message = await FindMessageAsync(agency.Id);
        message.ShouldNotBeNull();
        message.Type.ShouldBe(typeof(AgencyRegistered).FullName);
        JsonDocument.Parse(message.Payload).RootElement.GetProperty("name").GetString().ShouldBe(agency.Name);

        await WaitUntilAsync(() => host.Probe.Handled.Any(h => h.Event.AgencyId == agency.Id));
        var processed = await WaitForAsync(async () => await FindMessageAsync(agency.Id) is { ProcessedAtUtc: not null } m ? m : null);
        processed.AttemptCount.ShouldBe(0);
        var delivery = host.Probe.Handled.First(h => h.Event.AgencyId == agency.Id);
        delivery.Context.MessageId.ShouldBe(processed.Id);
        delivery.Context.Attempt.ShouldBe(1);
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_no_message()
    {
        var agency = Agency.Register(new AgencyDetails($"Rollback {Guid.NewGuid():N}", "Tunis")).Value;
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var result = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    db.Set<Agency>().Add(agency);
                    await db.SaveChangesAsync(ct);
                    return Result.Failure(Error.Conflict("tests.rollback", "Forced rollback."));
                },
                cancellationToken: Ct);
            result.IsFailure.ShouldBeTrue();
        }

        (await FindMessageAsync(agency.Id)).ShouldBeNull();
        await using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Agency>().AnyAsync(a => a.Id == agency.Id, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_failing_handler_is_retried_later_with_the_error_recorded()
    {
        var agency = Agency.Register(new AgencyDetails($"FAIL-{Guid.NewGuid():N}", "Tunis")).Value;
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<Agency>().Add(agency);
            await db.SaveChangesAsync(Ct);
        }

        var failed = await WaitForAsync(async () => await FindMessageAsync(agency.Id) is { AttemptCount: > 0 } m ? m : null);

        failed.ProcessedAtUtc.ShouldBeNull();
        failed.LastError.ShouldNotBeNull().ShouldContain("Simulated handler failure");
        failed.NextAttemptAtUtc.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Retry_delays_grow_exponentially_up_to_six_hours()
    {
        OutboxProcessor.RetryDelay(1).ShouldBe(TimeSpan.FromMinutes(1));
        OutboxProcessor.RetryDelay(2).ShouldBe(TimeSpan.FromMinutes(2));
        OutboxProcessor.RetryDelay(4).ShouldBe(TimeSpan.FromMinutes(8));
        OutboxProcessor.RetryDelay(20).ShouldBe(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Queued_emails_are_sent_through_the_outbox()
    {
        var subject = $"Outbox e-mail {Guid.NewGuid():N}";
        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEmailQueue>()
                .EnqueueAsync(new EmailMessage("someone@demo.local", subject, "<p>Bonjour</p>", "Bonjour"), Ct);
        }

        var file = await WaitForAsync(() => Task.FromResult(FindEmail(subject)));
        var content = await File.ReadAllTextAsync(file, Ct);
        content.ShouldContain("To: someone@demo.local");
        content.ShouldContain("multipart/alternative");
    }

    private string? FindEmail(string subject)
    {
        if (!Directory.Exists(host.Factory.EmailDirectory))
        {
            return null;
        }

        var encodedSubject = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(subject));
        return Directory.EnumerateFiles(host.Factory.EmailDirectory, "*.eml")
            .FirstOrDefault(f => File.ReadAllText(f).Contains(encodedSubject, StringComparison.Ordinal));
    }

    private async Task<OutboxMessage?> FindMessageAsync(Guid agencyId)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var id = agencyId.ToString();
        return await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == typeof(AgencyRegistered).FullName && m.Payload.Contains(id))
            .FirstOrDefaultAsync(Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "The outbox message was not processed in time.");
            await Task.Delay(200, Ct);
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, "The expected state was not reached in time.");
            await Task.Delay(200, Ct);
        }
    }
}
