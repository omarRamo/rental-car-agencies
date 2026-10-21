using System.Net;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Payments.ClicToPay;

namespace CarRental.Integration.Tests.Payments.ClicToPay;

/// <summary>
/// The REST client of Clic to Pay against a scripted handler (no network): form-encoded POSTs with the credentials in the
/// body, amounts in the minor unit (never rounded again), answers read defensively (unknown fields ignored, missing ones
/// handled, numbers as strings), conservative status codes, retries for status reads only, and a call log without
/// credentials or card data.
/// </summary>
public sealed class ClicToPayClientTests
{
    private const string OrderNumber = "0199a1b2c3d47e8f9a0b1c2d3e4f5a6b";
    private const string GatewayOrderId = "8f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private const string FormUrl = "https://test.clictopay.com/payment/merchants/agence/payment_fr.html?mdOrder=8f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pre_authorization_is_a_form_encoded_post_with_the_amount_in_millimes_and_the_credentials_in_the_body()
    {
        var handler = new ScriptedHandler().Answer($$"""{"orderId":"{{GatewayOrderId}}","formUrl":"{{FormUrl}}"}""");
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);

        var registered = await client.RegisterPreAuthAsync(new GatewayRegistration(OrderNumber, Money.Tnd(681.4m), "en"), Ct);

        registered.Value.ShouldBe(new GatewayRegisteredOrder(GatewayOrderId, FormUrl));
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Address.ShouldBe(new Uri("https://test.clictopay.com/payment/rest/registerPreAuth.do"));
        request.Address.Query.ShouldBeEmpty("credentials never travel in an address");
        request.ContentType.ShouldBe("application/x-www-form-urlencoded");
        request.Field("userName").ShouldBe(ClicToPayTestKit.UserName);
        request.Field("password").ShouldBe(ClicToPayTestKit.Password);
        request.Field("orderNumber").ShouldBe(OrderNumber);
        request.Field("amount").ShouldBe("681400");
        request.Field("returnUrl").ShouldBe($"https://www.agence.tn/payments/clictopay/return?order={OrderNumber}");
        request.Field("failUrl").ShouldBe($"https://www.agence.tn/payments/clictopay/fail?order={OrderNumber}");
        request.Field("language").ShouldBe("en");
        request.Form.Keys.ShouldNotContain("currency");
        request.Form.Keys.ShouldNotContain("description");
        request.Form.Keys.ShouldNotContain("sessionTimeoutSecs");
        var call = log.Entries.ShouldHaveSingleItem();
        call.Operation.ShouldBe(GatewayOperation.RegisterPreAuth);
        call.Outcome.ShouldBe(GatewayCallOutcome.Succeeded);
        call.OrderNumber.ShouldBe(OrderNumber);
        call.GatewayOrderId.ShouldBe(GatewayOrderId);
        call.HttpStatus.ShouldBe(200);
        call.Amount.ShouldBe(Money.Tnd(681.4m));
        call.GatewayErrorCode.ShouldBeNull();
    }

    [Theory]
    [InlineData("1.000", 1000, "1000")]
    [InlineData("681.4", 1000, "681400")]
    [InlineData("0.001", 1000, "1")]
    [InlineData("10.0005", 1000, "10001")]
    [InlineData("681.4", 100, "68140")]
    [InlineData("12.3", 10, "123")]
    [InlineData("12", 1, "12")]
    public async Task The_amount_is_sent_in_the_minor_unit_of_the_configured_multiplier(string tnd, int multiplier, string expected)
    {
        var handler = new ScriptedHandler().Answer($$"""{"orderId":"{{GatewayOrderId}}","formUrl":"{{FormUrl}}"}""");
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(o => o.AmountMultiplier = multiplier), new MemoryCallLog());

        // Money rounds to the millime (AwayFromZero): 10.0005 TND is 10.001 TND.
        (await client.RegisterAsync(new GatewayRegistration(OrderNumber, Money.Tnd(decimal.Parse(tnd, System.Globalization.CultureInfo.InvariantCulture)), null), Ct)).IsSuccess.ShouldBeTrue();

        handler.Requests.ShouldHaveSingleItem().Field("amount").ShouldBe(expected);
        handler.Requests.Single().Address.AbsolutePath.ShouldEndWith("/register.do");
    }

    [Fact]
    public async Task An_amount_the_multiplier_cannot_express_is_refused_before_any_call()
    {
        var handler = new ScriptedHandler();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(o => o.AmountMultiplier = 100), new MemoryCallLog());

        (await client.RegisterAsync(new GatewayRegistration(OrderNumber, Money.Tnd(681.405m), null), Ct)).Error.ShouldBe(PaymentsErrors.GatewayAmountNotRepresentable);
        (await client.DepositAsync(new GatewayOrderOperation(OrderNumber, GatewayOrderId, Money.Tnd(0.005m)), Ct)).Error.ShouldBe(PaymentsErrors.GatewayAmountNotRepresentable);

        handler.Requests.ShouldBeEmpty();
        ClicToPayProtocol.FromMinorUnits(681400, 1000).ShouldBe(Money.Tnd(681.4m));
        ClicToPayProtocol.FromMinorUnits(68141, 100).ShouldBe(Money.Tnd(681.41m));
        ClicToPayProtocol.ToMinorUnits(Money.Zero, 1000).Error.ShouldBe(PaymentsErrors.GatewayAmountNotRepresentable);
    }

    [Fact]
    public async Task Currency_description_and_session_timeout_are_sent_only_when_configured()
    {
        var handler = new ScriptedHandler().Answer($$"""{"orderId":"{{GatewayOrderId}}","formUrl":"{{FormUrl}}"}""");
        var options = ClicToPayTestKit.Options(o =>
        {
            o.Currency = "788";
            o.SendSessionTimeout = true;
        });
        var client = ClicToPayTestKit.Client(handler, options, new MemoryCallLog());

        await client.RegisterAsync(new GatewayRegistration(OrderNumber, Money.Tnd(100m), "xx-invalid", " LOC-2027-000123 ", TimeSpan.FromHours(24)), Ct);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Field("currency").ShouldBe("788");
        request.Field("description").ShouldBe("LOC-2027-000123");
        request.Field("sessionTimeoutSecs").ShouldBe("86400");
        request.Field("language").ShouldBe("fr", "an invalid language falls back to ClicToPay:Language");
    }

    [Fact]
    public async Task An_error_code_of_the_gateway_is_a_refusal_kept_in_the_call_log()
    {
        var handler = new ScriptedHandler().Answer("""{"errorCode":"1","errorMessage":"Order with this number was already processed"}""");
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);

        (await client.RegisterPreAuthAsync(new GatewayRegistration(OrderNumber, Money.Tnd(10m), null), Ct)).Error.ShouldBe(PaymentsErrors.GatewayRejected);

        var call = log.Entries.ShouldHaveSingleItem();
        call.Outcome.ShouldBe(GatewayCallOutcome.GatewayError);
        call.GatewayErrorCode.ShouldBe("1");
        call.GatewayErrorMessage.ShouldBe("Order with this number was already processed");
    }

    [Theory]
    [InlineData("""{"orderId":"8f1c"}""")]
    [InlineData("""{"formUrl":"https://test.clictopay.com/pay"}""")]
    [InlineData("""{"orderId":"8f1c","formUrl":"http://phishing.example/pay"}""")]
    [InlineData("""{"orderId":"8f1c","formUrl":"not an address"}""")]
    [InlineData("""["orderId"]""")]
    [InlineData("<html>Maintenance</html>")]
    [InlineData("")]
    public async Task An_answer_without_a_usable_order_and_payment_page_is_invalid(string body)
    {
        var handler = new ScriptedHandler().Answer(body);
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);

        (await client.RegisterPreAuthAsync(new GatewayRegistration(OrderNumber, Money.Tnd(10m), null), Ct)).Error.ShouldBe(PaymentsErrors.GatewayInvalidResponse);

        log.Entries.ShouldHaveSingleItem().Outcome.ShouldBe(GatewayCallOutcome.InvalidResponse);
    }

    [Fact]
    public async Task Operations_are_never_retried_and_a_lost_answer_is_unavailable()
    {
        var handler = new ScriptedHandler()
            .Status(HttpStatusCode.ServiceUnavailable)
            .Fail(new TaskCanceledException("time-out", new TimeoutException()))
            .Fail(new HttpRequestException("connection reset"))
            .Status(HttpStatusCode.BadGateway)
            .Status(HttpStatusCode.NotFound);
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);
        var operation = new GatewayOrderOperation(OrderNumber, GatewayOrderId, Money.Tnd(100m));

        (await client.DepositAsync(operation, Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        (await client.RefundAsync(operation, Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        (await client.ReverseAsync(new GatewayOrderReference(OrderNumber, GatewayOrderId), Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        (await client.RegisterAsync(new GatewayRegistration(OrderNumber, Money.Tnd(1m), null), Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);
        (await client.RegisterPreAuthAsync(new GatewayRegistration(OrderNumber, Money.Tnd(1m), null), Ct)).Error.ShouldBe(PaymentsErrors.GatewayInvalidResponse);

        handler.Requests.Count.ShouldBe(5, "one request per operation, never sent again");
        log.Entries.Select(e => e.Outcome).ShouldBe(
            [GatewayCallOutcome.Unavailable, GatewayCallOutcome.Unavailable, GatewayCallOutcome.Unavailable, GatewayCallOutcome.Unavailable, GatewayCallOutcome.InvalidResponse]);
        log.Entries[0].HttpStatus.ShouldBe(503);
        log.Entries[1].HttpStatus.ShouldBeNull();
    }

    [Fact]
    public async Task An_acknowledgement_has_error_code_zero_as_text_or_number_or_none()
    {
        var handler = new ScriptedHandler()
            .Answer("""{"errorCode":"0","errorMessage":"Success"}""")
            .Answer("""{"errorCode":0}""")
            .Answer("{}")
            .Answer("""{"errorCode":7,"errorMessage":"System error"}""");
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);
        var operation = new GatewayOrderOperation(OrderNumber, GatewayOrderId, Money.Tnd(650m), Guid.CreateVersion7());

        (await client.DepositAsync(operation, Ct)).IsSuccess.ShouldBeTrue();
        (await client.RefundAsync(operation, Ct)).IsSuccess.ShouldBeTrue();
        (await client.ReverseAsync(new GatewayOrderReference(OrderNumber, GatewayOrderId), Ct)).IsSuccess.ShouldBeTrue();
        (await client.DepositAsync(operation, Ct)).Error.ShouldBe(PaymentsErrors.GatewayRejected);

        handler.Requests[0].Address.AbsolutePath.ShouldEndWith("/deposit.do");
        handler.Requests[0].Field("orderId").ShouldBe(GatewayOrderId);
        handler.Requests[0].Field("amount").ShouldBe("650000");
        handler.Requests[1].Address.AbsolutePath.ShouldEndWith("/refund.do");
        handler.Requests[2].Address.AbsolutePath.ShouldEndWith("/reverse.do");
        handler.Requests[2].Form.Keys.ShouldNotContain("amount");
        log.Entries[0].GatewayErrorCode.ShouldBeNull("0 is success, not an error");
        log.Entries[1].RefundId.ShouldBe(operation.RefundId);
        log.Entries[3].GatewayErrorCode.ShouldBe("7");
    }

    [Fact]
    public async Task A_status_is_parsed_defensively_and_card_data_never_reaches_the_call_log()
    {
        var handler = new ScriptedHandler().Answer($$"""
            {
              "errorCode": "0",
              "errorMessage": "Success",
              "orderNumber": "{{OrderNumber}}",
              "orderStatus": 1,
              "actionCode": 0,
              "actionCodeDescription": "Approved",
              "amount": 681400,
              "currency": "788",
              "cardAuthInfo": { "maskedPan": "411111**1111", "expiration": "202812", "cardholderName": "SALAH BEN ALI", "approvalCode": "123456" },
              "paymentAmountInfo": { "paymentState": "APPROVED", "approvedAmount": 681400, "depositedAmount": 0, "refundedAmount": 0 },
              "bankInfo": { "bankName": "STB" },
              "somethingNew": { "nested": [1, 2, 3] }
            }
            """);
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);

        var status = (await client.GetOrderStatusExtendedAsync(new GatewayOrderReference(OrderNumber, GatewayOrderId), Ct)).Value;

        status.State.ShouldBe(GatewayOrderState.PreAuthorized);
        status.StatusCode.ShouldBe(1);
        status.OrderNumber.ShouldBe(OrderNumber);
        status.GatewayOrderId.ShouldBe(GatewayOrderId);
        status.Amount.ShouldBe(Money.Tnd(681.4m));
        status.ApprovedAmount.ShouldBe(Money.Tnd(681.4m));
        status.DepositedAmount.ShouldBeNull("zero deposited is no deposit");
        status.RefundedAmount.ShouldBe(Money.Zero);
        status.ActionCode.ShouldBe("0");
        status.ActionCodeDescription.ShouldBe("Approved");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Address.AbsolutePath.ShouldEndWith("/getOrderStatusExtended.do");
        request.Field("orderId").ShouldBe(GatewayOrderId);
        request.Form.Keys.ShouldNotContain("orderNumber");
        var call = log.Entries.ShouldHaveSingleItem();
        call.OrderStatus.ShouldBe(1);
        call.ActionCode.ShouldBe("0");
        call.GatewayErrorMessage.ShouldBeNull();
        var logged = string.Join('|', typeof(GatewayCallLogEntry).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.GetValue(call) as string));
        logged.ShouldNotContain("4111");
        logged.ShouldNotContain("1111");
        logged.ShouldNotContain("SALAH");
        logged.ShouldNotContain("123456");
        logged.ShouldNotContain(ClicToPayTestKit.Password);
        logged.ShouldNotContain(ClicToPayTestKit.UserName);
    }

    [Fact]
    public async Task Numbers_given_as_text_and_missing_amount_details_are_read()
    {
        var handler = new ScriptedHandler()
            .Answer("""{"ErrorCode":"0","OrderStatus":"2","Amount":"650000"}""")
            .Answer("""{"errorCode":"0","orderStatus":5}""")
            .Answer("""{"errorCode":"0","orderStatus":3}""")
            .Answer("""{"errorCode":"0","orderStatus":6,"actionCode":"116"}""");
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), new MemoryCallLog());
        var order = new GatewayOrderReference(OrderNumber, null);

        var deposited = (await client.GetOrderStatusExtendedAsync(order, Ct)).Value;
        (await client.GetOrderStatusExtendedAsync(order, Ct)).Value.State.ShouldBe(GatewayOrderState.Unknown);
        (await client.GetOrderStatusExtendedAsync(order, Ct)).Value.State.ShouldBe(GatewayOrderState.Reversed);
        var declined = (await client.GetOrderStatusExtendedAsync(order, Ct)).Value;

        deposited.State.ShouldBe(GatewayOrderState.Deposited);
        deposited.Amount.ShouldBe(Money.Tnd(650m));
        deposited.DepositedAmount.ShouldBeNull();
        deposited.RefundedAmount.ShouldBeNull();
        declined.State.ShouldBe(GatewayOrderState.Declined);
        declined.ActionCode.ShouldBe("116");
        handler.Requests[0].Field("orderNumber").ShouldBe(OrderNumber, "read by our order number while the gateway order id is unknown");
        handler.Requests[0].Form.Keys.ShouldNotContain("orderId");
    }

    [Fact]
    public async Task A_status_answer_without_status_or_with_an_error_code_confirms_nothing()
    {
        var handler = new ScriptedHandler()
            .Answer("""{"errorCode":"0","errorMessage":"Success"}""")
            .Answer("""{"errorCode":"6","errorMessage":"Order not found"}""");
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);
        var order = new GatewayOrderReference(OrderNumber, GatewayOrderId);

        (await client.GetOrderStatusExtendedAsync(order, Ct)).Error.ShouldBe(PaymentsErrors.GatewayInvalidResponse);
        (await client.GetOrderStatusExtendedAsync(order, Ct)).Error.ShouldBe(PaymentsErrors.GatewayRejected);

        log.Entries[1].GatewayErrorCode.ShouldBe("6");
        log.Entries[1].GatewayErrorMessage.ShouldBe("Order not found");
    }

    [Fact]
    public async Task Status_reads_alone_are_retried_after_a_transport_failure()
    {
        var handler = new ScriptedHandler()
            .Status(HttpStatusCode.ServiceUnavailable)
            .Fail(new HttpRequestException("connection reset"))
            .Answer("""{"errorCode":"0","orderStatus":2,"amount":100000}""");
        var log = new MemoryCallLog();
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(), log);

        var status = await client.GetOrderStatusExtendedAsync(new GatewayOrderReference(OrderNumber, GatewayOrderId), Ct);

        status.Value.State.ShouldBe(GatewayOrderState.Deposited);
        handler.Requests.Count.ShouldBe(3);
        log.Entries.Select(e => e.Outcome).ShouldBe([GatewayCallOutcome.Unavailable, GatewayCallOutcome.Unavailable, GatewayCallOutcome.Succeeded]);
    }

    [Fact]
    public async Task A_status_read_gives_up_after_the_configured_attempts()
    {
        var handler = new ScriptedHandler().Status(HttpStatusCode.ServiceUnavailable).Status(HttpStatusCode.ServiceUnavailable).Status(HttpStatusCode.ServiceUnavailable);
        var client = ClicToPayTestKit.Client(handler, ClicToPayTestKit.Options(o => o.StatusReadAttempts = 2), new MemoryCallLog());

        (await client.GetOrderStatusExtendedAsync(new GatewayOrderReference(OrderNumber, GatewayOrderId), Ct)).Error.ShouldBe(PaymentsErrors.GatewayUnavailable);

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public void The_options_refuse_the_simulated_gateway_outside_development_and_testing_and_check_the_real_one()
    {
        var production = new ClicToPayOptionsValidator(new TestEnvironment("Production"));
        var testing = new ClicToPayOptionsValidator(new TestEnvironment(ClicToPayOptionsValidator.TestingEnvironment));

        production.Validate(null, new ClicToPayOptions { Enabled = true, UseFake = true }).Failed.ShouldBeTrue();
        testing.Validate(null, new ClicToPayOptions { Enabled = true, UseFake = true }).Succeeded.ShouldBeTrue();
        production.Validate(null, new ClicToPayOptions()).Succeeded.ShouldBeTrue("switched off, nothing else is required");
        production.Validate(null, new ClicToPayOptions { Enabled = true }).Failures!.ShouldContain(f => f.Contains("UserName", StringComparison.Ordinal));
        production.Validate(null, ClicToPayTestKit.Options()).Succeeded.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.ReturnUrl = "/payments/clictopay/return")).Failed.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.BaseUrl = new Uri("http://test.clictopay.com/payment/rest/"))).Failed.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.BaseUrl = new Uri("https://test.clictopay.com/payment/rest"))).Failed.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.AmountMultiplier = 3)).Failed.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.Language = "FR")).Failed.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.Currency = "TND")).Failed.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.DeclinedStatusCodes = "1")).Failed.ShouldBeTrue();
        production.Validate(null, ClicToPayTestKit.Options(o => o.TimeoutSeconds = 0)).Failed.ShouldBeTrue();
    }

    private sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CarRental.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
