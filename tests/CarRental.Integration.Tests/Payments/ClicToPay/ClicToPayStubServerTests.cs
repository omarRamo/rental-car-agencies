using System.Collections.Concurrent;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Payments.ClicToPay;
using CarRental.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarRental.Integration.Tests.Payments.ClicToPay;

/// <summary>
/// The real <see cref="ClicToPayClient"/> over HTTP against a local stub of the Clic to Pay REST API (Kestrel on a free
/// port), with the call log of the application written to SQL Server: form-encoded requests, a time-out that leaves the
/// outcome unknown until the status read, and call-log rows holding neither credentials nor card data.
/// </summary>
public sealed class ClicToPayStubServerTests(IntegrationHost host) : IAsyncLifetime
{
    private readonly ConcurrentQueue<(string Path, string Query, Dictionary<string, string> Form)> _requests = new();
    private readonly ConcurrentDictionary<string, StubOrder> _orders = new(StringComparer.Ordinal);
    private WebApplication _stub = null!;
    private Uri _baseUrl = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _stub = builder.Build();
        var api = _stub.MapGroup("/payment/rest");
        api.MapPost("/registerPreAuth.do", async (HttpContext context) =>
        {
            var form = await ReadAsync(context);
            var orderId = Guid.NewGuid().ToString("D");
            _orders[orderId] = new StubOrder(form["orderNumber"], long.Parse(form["amount"], System.Globalization.CultureInfo.InvariantCulture));
            return Results.Json(new { orderId, formUrl = $"https://test.clictopay.com/payment/merchants/agence/payment_fr.html?mdOrder={orderId}" });
        });
        api.MapPost("/deposit.do", async (HttpContext context) =>
        {
            var form = await ReadAsync(context);
            var order = _orders[form["orderId"]];
            order.Status = 2;
            order.Deposited = long.Parse(form["amount"], System.Globalization.CultureInfo.InvariantCulture);
            if (order.SlowDeposit)
            {
                // The deposit is done, but the answer never comes back: the client times out.
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }

            return Results.Json(new { errorCode = "0", errorMessage = "Success" });
        });
        api.MapPost("/getOrderStatusExtended.do", async (HttpContext context) =>
        {
            var form = await ReadAsync(context);
            var order = _orders[form["orderId"]];
            return Results.Json(new
            {
                errorCode = "0",
                errorMessage = "Success",
                orderNumber = order.OrderNumber,
                orderStatus = order.Status,
                actionCode = 0,
                actionCodeDescription = "Request processed successfully",
                amount = order.Amount,
                currency = "788",
                cardAuthInfo = new { maskedPan = "411111**1111", expiration = "202812", cardholderName = "SALAH BEN ALI", approvalCode = "987654" },
                paymentAmountInfo = new { paymentState = order.Status == 2 ? "DEPOSITED" : "APPROVED", approvedAmount = order.Amount, depositedAmount = order.Deposited, refundedAmount = 0 },
            });
        });
        await _stub.StartAsync(Ct);
        _baseUrl = new Uri(_stub.Urls.First() + "/payment/rest/");
    }

    public async ValueTask DisposeAsync() => await _stub.DisposeAsync();

    [Fact]
    public async Task A_round_trip_writes_the_call_log_on_sql_server_without_credentials_or_card_data()
    {
        var client = Client(TimeSpan.FromSeconds(10));
        var orderNumber = Guid.CreateVersion7().ToString("N");

        var registered = (await client.RegisterPreAuthAsync(new GatewayRegistration(orderNumber, Money.Tnd(681.4m), "fr"), Ct)).Value;
        var preAuthorized = (await client.GetOrderStatusExtendedAsync(new GatewayOrderReference(orderNumber, registered.GatewayOrderId), Ct)).Value;
        (await client.DepositAsync(new GatewayOrderOperation(orderNumber, registered.GatewayOrderId, Money.Tnd(650m)), Ct)).IsSuccess.ShouldBeTrue();
        var deposited = (await client.GetOrderStatusExtendedAsync(new GatewayOrderReference(orderNumber, registered.GatewayOrderId), Ct)).Value;

        preAuthorized.State.ShouldBe(GatewayOrderState.PreAuthorized);
        preAuthorized.ApprovedAmount.ShouldBe(Money.Tnd(681.4m));
        deposited.State.ShouldBe(GatewayOrderState.Deposited);
        deposited.DepositedAmount.ShouldBe(Money.Tnd(650m));
        var register = _requests.First(r => r.Path.EndsWith("registerPreAuth.do", StringComparison.Ordinal));
        register.Query.ShouldBeEmpty();
        register.Form["amount"].ShouldBe("681400");
        register.Form["userName"].ShouldBe(ClicToPayTestKit.UserName);
        register.Form["returnUrl"].ShouldBe($"https://www.agence.tn/payments/clictopay/return?order={orderNumber}");

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var calls = await db.Set<GatewayCallLog>().AsNoTracking().Where(c => c.OrderNumber == orderNumber).OrderBy(c => c.StartedAtUtc).ToListAsync(Ct);
        calls.Select(c => c.Operation).ShouldBe(
            [GatewayOperation.RegisterPreAuth, GatewayOperation.GetOrderStatusExtended, GatewayOperation.Deposit, GatewayOperation.GetOrderStatusExtended]);
        calls.ShouldAllBe(c => c.Outcome == GatewayCallOutcome.Succeeded && c.HttpStatus == 200 && c.GatewayOrderId == registered.GatewayOrderId);
        calls.ShouldAllBe(c => c.PaymentId == Guid.ParseExact(orderNumber, "N"));
        calls[0].Amount.ShouldBe(Money.Tnd(681.4m));
        calls[2].Amount.ShouldBe(Money.Tnd(650m));
        calls[3].OrderStatus.ShouldBe(2);
        calls[3].ActionCode.ShouldBe("0");

        // Every stored value of these rows, read straight from SQL Server.
        var stored = await RawRowsAsync(orderNumber);
        stored.ShouldNotContain(ClicToPayTestKit.Password);
        stored.ShouldNotContain(ClicToPayTestKit.UserName);
        stored.ShouldNotContain("411111");
        stored.ShouldNotContain("SALAH");
        stored.ShouldNotContain("987654");
        stored.ShouldNotContain("mdOrder", Case.Sensitive, "no raw answer is stored");
    }

    [Fact]
    public async Task A_time_out_leaves_the_outcome_unknown_until_the_status_read_shows_it()
    {
        var client = Client(TimeSpan.FromSeconds(1));
        var orderNumber = Guid.CreateVersion7().ToString("N");
        var registered = (await client.RegisterPreAuthAsync(new GatewayRegistration(orderNumber, Money.Tnd(100m), "fr"), Ct)).Value;
        _orders[registered.GatewayOrderId].Status = 1;
        _orders[registered.GatewayOrderId].SlowDeposit = true;

        var deposit = await client.DepositAsync(new GatewayOrderOperation(orderNumber, registered.GatewayOrderId, Money.Tnd(100m)), Ct);
        var status = await client.GetOrderStatusExtendedAsync(new GatewayOrderReference(orderNumber, registered.GatewayOrderId), Ct);

        deposit.Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        status.Value.State.ShouldBe(GatewayOrderState.Deposited, "the deposit was processed although its answer was lost");
        _requests.Count(r => r.Form.GetValueOrDefault("orderId") == registered.GatewayOrderId && r.Path.EndsWith("deposit.do", StringComparison.Ordinal))
            .ShouldBe(1, "never sent twice");
        await using var scope = host.CreateScope();
        var lost = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<GatewayCallLog>().AsNoTracking()
            .SingleAsync(c => c.OrderNumber == orderNumber && c.Operation == GatewayOperation.Deposit, Ct);
        lost.Outcome.ShouldBe(GatewayCallOutcome.Unavailable);
        lost.HttpStatus.ShouldBeNull();
        lost.DurationMilliseconds.ShouldBeGreaterThanOrEqualTo(900);
    }

    private ClicToPayClient Client(TimeSpan timeout)
    {
        var options = ClicToPayTestKit.Options(o => o.BaseUrl = _baseUrl);
        var http = new HttpClient { BaseAddress = _baseUrl, Timeout = timeout };
        return new ClicToPayClient(
            http,
            Microsoft.Extensions.Options.Options.Create(options),
            host.Factory.Services.GetRequiredService<IGatewayCallLog>(),
            TimeProvider.System,
            NullLogger<ClicToPayClient>.Instance);
    }

    private async Task<Dictionary<string, string>> ReadAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync();
        var fields = form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal);
        _requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.QueryString.Value ?? string.Empty, fields));
        return fields;
    }

    private async Task<string> RawRowsAsync(string orderNumber)
    {
        await using var connection = new SqlConnection(host.Factory.SqlConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM payments.GatewayCallLogs WHERE OrderNumber = @order";
        command.Parameters.AddWithValue("@order", orderNumber);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values.Add(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }

        values.Count.ShouldBeGreaterThan(0);
        return string.Join('|', values);
    }

    private sealed class StubOrder(string orderNumber, long amount)
    {
        public string OrderNumber { get; } = orderNumber;

        public long Amount { get; } = amount;

        public int Status { get; set; } = 1;

        public long Deposited { get; set; }

        public bool SlowDeposit { get; set; }
    }
}
