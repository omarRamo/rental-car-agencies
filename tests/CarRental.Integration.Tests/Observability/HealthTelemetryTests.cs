using System.Net;
using System.Text.Json;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Observability;
using CarRental.Integration.Tests.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CarRental.Integration.Tests.Observability;

/// <summary>
/// Health entries watched by the alert rules (ADR-0060), on the real application and SQL Server: the publisher of the
/// telemetry, the entry of the recurring jobs and the entry of the online payments.
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
    public async Task The_readiness_report_lists_the_jobs_and_the_payments()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var checks = JsonDocument.Parse(body).RootElement.GetProperty("checks");
        foreach (var entry in new[] { "sql", "storage", "hangfire", "jobs", "outbox", "channels", "payments" })
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
