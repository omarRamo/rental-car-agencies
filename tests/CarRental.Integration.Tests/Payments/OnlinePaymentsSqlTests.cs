using System.Net;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Support;
using CarRental.Testing;
using CarRental.Web.Components.Public.Site;
using CarRental.Web.Endpoints;
using CarRental.Web.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Payments;

/// <summary>
/// Online payments through the real application on SQL Server with the simulated gateway: payment page, return address
/// (redirect to the recorded local path only), temporal history, call log, outbox, concurrent returns and job runs
/// applying a status once, and the expiry of a payment link by the catch-up job.
/// </summary>
public sealed class OnlinePaymentsSqlTests(IntegrationHost host)
{
    private const string CatchUpJobId = "payments.online_catch_up";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pre_authorization_paid_on_the_simulated_page_is_confirmed_by_the_return_address()
    {
        // The result page belongs to the public site: it shows the payments of the agency of the site it serves.
        var agencyId = await PublicSiteAgencyAsync();
        var started = await StartAsync(agencyId, 681.4m, "/fr/paiement/{paymentId}");
        using var client = host.CreateClient();

        var page = await client.GetAsync(new Uri(started.RedirectUrl, UriKind.Relative), Ct);
        var html = await page.Content.ReadAsStringAsync(Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Payer");
        html.ShouldContain(started.OrderNumber);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(html),
            ["action"] = "pay",
        });
        var paid = await client.PostAsync(new Uri(started.RedirectUrl, UriKind.Relative), form, Ct);
        paid.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var returnAddress = HttpSignIn.PathAndQuery(paid.Headers.Location!);
        returnAddress.ShouldStartWith($"/payments/clictopay/return?order={started.OrderNumber}&orderId=");

        var returned = await client.GetAsync(new Uri(returnAddress, UriKind.Relative), Ct);

        returned.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(returned.Headers.Location!).ShouldBe($"/fr/paiement/{started.PaymentId:D}");
        returned.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var result = WebUtility.HtmlDecode(await client.GetStringAsync(new Uri($"/fr/paiement/{started.PaymentId:D}", UriKind.Relative), Ct));
        result.ShouldContain("Paiement autorisé");
        result.ShouldContain("681,400 TND");

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var payment = await db.Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == started.PaymentId, Ct);
        payment.Status.ShouldBe(PaymentStatus.PreAuthorized);
        payment.Amount.ShouldBe(Money.Tnd(681.4m));
        payment.ExternalReference.ShouldNotBeNull();
        payment.GatewayStatusCode.ShouldBe(1);
        var history = await db.Set<Payment>().TemporalAll().Where(p => p.Id == started.PaymentId).Select(p => p.Status).ToListAsync(Ct);
        history.ShouldContain(PaymentStatus.Pending);
        history.ShouldContain(PaymentStatus.PreAuthorized);
        var calls = await db.Set<GatewayCallLog>().AsNoTracking().Where(c => c.PaymentId == started.PaymentId).Select(c => new { c.Operation, c.AgencyId }).ToListAsync(Ct);
        calls.Select(c => c.Operation).ShouldContain(GatewayOperation.RegisterPreAuth);
        calls.Select(c => c.Operation).ShouldContain(GatewayOperation.GetOrderStatusExtended);
        calls.ShouldAllBe(c => c.AgencyId == agencyId, "the calls are records of the agency of the payment");
        (await OutboxCountAsync(db, typeof(PaymentStatusChanged), started.PaymentId)).ShouldBe(1);
        (await OutboxCountAsync(db, typeof(OnlinePaymentStatusChanged), started.PaymentId)).ShouldBe(1, "told to the other modules with the change");
    }

    [Fact]
    public async Task The_return_address_redirects_to_the_recorded_path_only_never_to_a_query_value()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var started = await StartAsync(agencyId, 120m, "/fr/reserver/confirmation/LOC-TEST", "/fr/reserver/paiement?echec=1");
        using var client = host.CreateClient();

        var tampered = await client.GetAsync(new Uri($"/payments/clictopay/fail?order={started.OrderNumber}&returnUrl=https%3A%2F%2Fevil.example&orderId=forged", UriKind.Relative), Ct);
        var unknown = await client.GetAsync(new Uri("/payments/clictopay/return?order=0123456789abcdef0123456789abcdef", UriKind.Relative), Ct);
        var empty = await client.GetAsync(new Uri("/payments/clictopay/return", UriKind.Relative), Ct);
        var garbage = await client.GetAsync(new Uri("/payments/clictopay/return?order=%2F%2Fevil.example", UriKind.Relative), Ct);

        tampered.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        HttpSignIn.PathAndQuery(tampered.Headers.Location!).ShouldBe("/fr/reserver/paiement?echec=1");
        HttpSignIn.PathAndQuery(unknown.Headers.Location!).ShouldBe("/");
        HttpSignIn.PathAndQuery(empty.Headers.Location!).ShouldBe("/");
        HttpSignIn.PathAndQuery(garbage.Headers.Location!).ShouldBe("/");
        await using var scope = host.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == started.PaymentId, Ct))
            .Status.ShouldBe(PaymentStatus.Pending, "the simulated customer did not pay");
    }

    [Fact]
    public async Task Concurrent_returns_and_catch_up_runs_apply_the_confirmed_status_once()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var payment = await BackdatedAsync(agencyId, OnlinePaymentFlow.PreAuthorization, 450m, recordedAgo: TimeSpan.FromMinutes(5), validity: TimeSpan.FromHours(1));
        var fake = host.Factory.Services.GetRequiredService<FakeClicToPay>();
        fake.Pay(payment.GatewayOrderId).IsSuccess.ShouldBeTrue();

        var returns = Enumerable.Range(0, 6).Select(async _ =>
        {
            // As the return address handles them: by the platform, whatever agency the order belongs to.
            await using var scope = host.CreateScope(UserContextSnapshot.System(ClicToPayEndpoints.ReturnActor));
            return await scope.ServiceProvider.GetRequiredService<IOnlinePayments>()
                .HandleReturnAsync(new OnlinePaymentReturnRequest(payment.OrderNumber, payment.GatewayOrderId, false), Ct);
        }).ToList();
        var jobs = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var scope = host.CreateScope(UserContextSnapshot.System($"system:job:{CatchUpJobId}"));
            await CatchUpJob(scope).ExecuteAsync(Ct);
        }).ToList();
        var results = await Task.WhenAll(returns);
        await Task.WhenAll(jobs);

        results.ShouldAllBe(r => r.IsSuccess && r.Value.RedirectPath == "/fr/ok");
        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        (await db.Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == payment.PaymentId, Ct)).Status.ShouldBe(PaymentStatus.PreAuthorized);
        (await OutboxCountAsync(db, typeof(PaymentStatusChanged), payment.PaymentId)).ShouldBe(1, "the transition is applied once");
        (await OutboxCountAsync(db, typeof(OnlinePaymentStatusChanged), payment.PaymentId)).ShouldBe(1, "and told to the other modules once");
        (await db.Set<Payment>().TemporalAll().CountAsync(p => p.Id == payment.PaymentId && p.Status == PaymentStatus.PreAuthorized, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_payment_link_past_its_validity_expires_with_the_catch_up_job()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var link = await BackdatedAsync(agencyId, OnlinePaymentFlow.OneStep, 300m, recordedAgo: TimeSpan.FromHours(25), validity: TimeSpan.FromHours(24), isLink: true);

        await using (var scope = host.CreateScope(UserContextSnapshot.System($"system:job:{CatchUpJobId}")))
        {
            await CatchUpJob(scope).ExecuteAsync(Ct);
        }

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var payment = await db.Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == link.PaymentId, Ct);
        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.ExpiredAtUtc.ShouldNotBeNull();
        payment.StatusChangedByName.ShouldBe($"system:job:{CatchUpJobId}");
        (await OutboxCountAsync(db, typeof(OnlinePaymentExpired), link.PaymentId)).ShouldBe(1);
        var type = typeof(OnlinePaymentStatusChanged).FullName!;
        var id = link.PaymentId.ToString();
        var stored = await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Type == type && m.Payload.Contains(id), Ct);
        var changed = (OnlinePaymentStatusChanged)check.ServiceProvider.GetRequiredService<OutboxSerializer>().Deserialize(stored.Payload, typeof(OnlinePaymentStatusChanged));
        changed.ShouldBe(new OnlinePaymentStatusChanged(
            link.PaymentId,
            payment.BookingId,
            agencyId,
            OnlinePaymentFlowInfo.OneStep,
            IsPaymentLink: true,
            PaymentStatusInfo.Pending,
            PaymentStatusInfo.Cancelled,
            Expired: true,
            PaidLate: false));
    }

    [Fact]
    public async Task The_development_page_starts_a_payment_like_the_funnel_will()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        using var client = host.CreateClient();
        var page = await client.GetStringAsync(new Uri("/dev/clictopay/start", UriKind.Relative), Ct);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HttpSignIn.AntiforgeryToken(page),
            ["AgencyId"] = agencyId.ToString(),
            ["BookingId"] = string.Empty,
            ["Amount"] = "75,500",
            ["Flow"] = "oneStep",
            ["Culture"] = "en",
        });

        var started = await client.PostAsync(new Uri("/dev/clictopay/start", UriKind.Relative), form, Ct);

        started.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var paymentPage = HttpSignIn.PathAndQuery(started.Headers.Location!);
        paymentPage.ShouldStartWith(FakeClicToPay.PagePathPrefix);
        var order = host.Factory.Services.GetRequiredService<FakeClicToPay>().FindOrder(paymentPage[FakeClicToPay.PagePathPrefix.Length..]).ShouldNotBeNull();
        order.Amount.ShouldBe(Money.Tnd(75.5m));
        order.IsPreAuthorization.ShouldBeFalse();
        order.Language.ShouldBe("en");
        await using var scope = host.CreateScope();
        var payment = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Payment>().AsNoTracking()
            .SingleAsync(p => p.Id == Guid.ParseExact(order.OrderNumber, "N"), Ct);
        payment.AgencyId.ShouldBe(agencyId);
        payment.GatewayReturnPath.ShouldBe("/en/payment/{paymentId}");
    }

    [Fact]
    public async Task The_simulated_payment_page_refuses_a_post_without_antiforgery_token()
    {
        var agencyId = await FinanceData.NewAgencyAsync(host, Ct);
        var started = await StartAsync(agencyId, 50m, "/fr/ok");
        using var client = host.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "pay" });

        var forged = await client.PostAsync(new Uri(started.RedirectUrl, UriKind.Relative), form, Ct);

        forged.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        host.Factory.Services.GetRequiredService<FakeClicToPay>().FindOrderByNumber(started.OrderNumber)!.IsPayable.ShouldBeTrue();
    }

    private static IRecurringJob CatchUpJob(AsyncServiceScope scope)
    {
        var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == CatchUpJobId);
        return (IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType);
    }

    private static Task<int> OutboxCountAsync(CarRentalDbContext db, Type eventType, Guid paymentId)
    {
        var type = eventType.FullName!;
        var id = paymentId.ToString();
        return db.Set<OutboxMessage>().AsNoTracking().CountAsync(m => m.Type == type && m.Payload.Contains(id), Ct);
    }

    /// <summary>The agency whose public site the culture addresses (<c>/fr/…</c>) serve.</summary>
    private async Task<Guid> PublicSiteAgencyAsync()
    {
        var services = host.Factory.Services;
        var agencyId = await PublicSiteAgency.DefaultAsync(services.GetRequiredService<IAgencyDirectory>(), services.GetRequiredService<IOptions<PublicSiteOptions>>().Value, Ct);
        return agencyId.ShouldNotBeNull();
    }

    /// <summary>A pre-authorization started like the funnel of the agency's public site starts it: as a visitor of that site.</summary>
    private async Task<OnlinePaymentRedirect> StartAsync(Guid agencyId, decimal amount, string returnPath, string? failPath = null)
    {
        await using var scope = host.CreateScope(TestActors.Visitor(agencyId, "198.51.100.20"));
        var started = await scope.ServiceProvider.GetRequiredService<IOnlinePayments>()
            .StartPreAuthorizationAsync(new OnlinePaymentRequest(Guid.CreateVersion7(), agencyId, amount, "fr", returnPath, failPath ?? returnPath), Ct);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : null);
        return started.Value;
    }

    /// <summary>An online payment recorded <paramref name="recordedAgo"/>, registered at the simulated gateway.</summary>
    private async Task<(Guid PaymentId, string OrderNumber, string GatewayOrderId)> BackdatedAsync(
        Guid agencyId,
        OnlinePaymentFlow flow,
        decimal amount,
        TimeSpan recordedAgo,
        TimeSpan validity,
        bool isLink = false)
    {
        var recordedAt = DateTimeOffset.UtcNow - recordedAgo;
        var payment = Payment.StartOnline(
            new OnlinePaymentDetails(agencyId, Guid.CreateVersion7(), Money.Tnd(amount), flow, "/fr/ok", "/fr/ko", recordedAt + validity, isLink),
            AuditActor.System("system:tests"),
            recordedAt).Value;
        var fake = host.Factory.Services.GetRequiredService<FakeClicToPay>();
        var registration = new GatewayRegistration(payment.OrderNumber, payment.Amount, "fr");
        var order = flow == OnlinePaymentFlow.PreAuthorization
            ? (await fake.RegisterPreAuthAsync(registration, Ct)).Value
            : (await fake.RegisterAsync(registration, Ct)).Value;
        payment.RecordGatewayRegistration(order.GatewayOrderId, order.FormUrl, recordedAt).IsSuccess.ShouldBeTrue();
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.Set<Payment>().Add(payment);
        await db.SaveChangesAsync(Ct);
        return (payment.Id, payment.OrderNumber, order.GatewayOrderId);
    }
}
