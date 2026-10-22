using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Domain.Messaging.Agent;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Messaging.Agent;
using CarRental.Infrastructure.Observability;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CarRental.Integration.Tests.Observability;

/// <summary>
/// Health entries watched by the alert rules (ADR-0060), on the real application and SQL Server: the publisher of the
/// telemetry, the entry of the recurring jobs, the entry of the online payments and the entry of the bot turns.
/// </summary>
public sealed class HealthTelemetryTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_entry_is_published_as_telemetry()
    {
        host.Factory.Services.GetServices<IHealthCheckPublisher>().ShouldContain(p => p is HealthStatusPublisher);
    }

    [Fact]
    public async Task The_readiness_report_lists_the_jobs_the_payments_and_the_bot()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var checks = JsonDocument.Parse(body).RootElement.GetProperty("checks");
        foreach (var entry in new[] { "sql", "storage", "hangfire", "jobs", "outbox", "channels", "payments", "whatsapp" })
        {
            checks.TryGetProperty(entry, out _).ShouldBeTrue($"{entry}: {body}");
        }
    }

    [Fact]
    public async Task The_jobs_entry_reads_every_registered_recurring_job()
    {
        var registered = host.Factory.Services.GetServices<RecurringJobDescriptor>().Select(d => d.JobId).Distinct().Count();

        var entry = await EntryAsync("jobs");

        entry.Data["jobs"].ShouldBe(registered);
        entry.Data["missing"].ShouldBe(0, entry.Description);
    }

    [Fact]
    public async Task A_flagged_online_payment_turns_the_payments_entry_degraded()
    {
        var paymentId = await FlaggedPaymentAsync();
        try
        {
            // Other tests flag and clear payments of the shared database meanwhile: only the lower bound is certain.
            var statistics = await StatisticsAsync();
            var entry = await EntryAsync("payments");

            statistics.FlaggedCount.ShouldBeGreaterThanOrEqualTo(1);
            entry.Status.ShouldBe(HealthStatus.Degraded);
            entry.Description.ShouldNotBeNull().ShouldContain("/admin/payments");
            ((int)entry.Data["flagged"]).ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await using var scope = host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var payment = await db.Set<Payment>().SingleAsync(p => p.Id == paymentId, Ct);
            payment.ClearGatewayAttention().IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task A_customer_message_waiting_for_the_bot_too_long_turns_the_whatsapp_entry_degraded()
    {
        var now = DateTimeOffset.UtcNow;
        var waiting = now - WhatsAppHealthCheck.MinimumStaleAfter - TimeSpan.FromMinutes(10);

        // In the demo agency, whose WhatsApp module the host switched on: the entry watches those agencies only.
        var conversation = Conversation.Start(host.DemoAgencyId, "+3375" + RandomNumberGenerator.GetInt32(0, 10_000_000).ToString("D7", CultureInfo.InvariantCulture), "Client test", waiting).Value;
        var message = conversation.ReceiveCustomerMessage($"wamid.TEST{Guid.NewGuid():N}", TextContent.Create("Bonjour ?").Value, waiting, null, waiting);
        message.BotTurn.ShouldBe(BotTurnState.Pending);

        // A retry due in two hours keeps the executor of the host away from this turn: it waits as a stuck one would.
        var retry = BotTurnRetry.For(conversation.Id, conversation.AgencyId);
        retry.Failed(now, TimeSpan.FromHours(2));
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            db.Set<Conversation>().Add(conversation);
            db.Set<Message>().Add(message);
            db.Set<BotTurnRetry>().Add(retry);
            await db.SaveChangesAsync(Ct);
        }

        try
        {
            var entry = await EntryAsync("whatsapp");

            entry.Status.ShouldBe(HealthStatus.Degraded, entry.Description);
            entry.Description.ShouldNotBeNull().ShouldContain("/admin/whatsapp");
            ((int)entry.Data["staleConversations"]).ShouldBeGreaterThanOrEqualTo(1);
            ((double)entry.Data["oldestPendingMinutes"]).ShouldBeGreaterThanOrEqualTo(30d);
        }
        finally
        {
            await using var scope = host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var stuck = await db.Set<Message>().SingleAsync(m => m.Id == message.Id, Ct);
            stuck.CompleteBotTurn(BotTurnState.NoReply);
            db.Set<BotTurnRetry>().Remove(await db.Set<BotTurnRetry>().SingleAsync(r => r.Id == conversation.Id, Ct));
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public void A_turn_may_wait_for_its_retries_and_model_calls_before_the_entry_reports_it()
    {
        WhatsAppHealthCheck.StaleAfter(new BotTurnOptions()).ShouldBe(WhatsAppHealthCheck.MinimumStaleAfter, "2 s, then 20 s, 1 and 5 minutes of retries, and the model calls");
        WhatsAppHealthCheck.StaleAfter(new BotTurnOptions { RetryDelays = "00:10:00;00:30:00" }).ShouldBe(TimeSpan.FromMinutes(50) + TimeSpan.FromSeconds(2));
    }

    private async Task<HealthReportEntry> EntryAsync(string name)
    {
        await using var scope = host.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == name, Ct);
        return report.Entries[name];
    }

    private async Task<OnlinePaymentHealthStatistics> StatisticsAsync()
    {
        await using var scope = host.CreateScope();
        return await OnlinePaymentMonitoring.ReadStatisticsAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), DateTimeOffset.UtcNow, Ct);
    }

    private async Task<Guid> FlaggedPaymentAsync()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var now = DateTimeOffset.UtcNow;
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var payment = Payment.StartOnline(
            new OnlinePaymentDetails(agencyId, Guid.CreateVersion7(), Money.Tnd(150m), OnlinePaymentFlow.PreAuthorization, "/fr/ok", "/fr/ko", now.AddHours(1)),
            AuditActor.System("system:tests"),
            now).Value;
        payment.FlagGatewayAttention(GatewayAttention.UnknownStatus, "status 9 (test)", now).IsSuccess.ShouldBeTrue();
        db.Set<Payment>().Add(payment);
        await db.SaveChangesAsync(Ct);
        return payment.Id;
    }
}
