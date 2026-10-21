using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;

namespace CarRental.Application.Tests.Payments;

/// <summary>
/// Conservative mapping of the gateway statuses (SPEC §9): 0, 1 and 2 are known, other codes only once configured from the
/// SMT documentation; an unknown code is never a success. The amounts confirmed come from the gateway first.
/// </summary>
public sealed class GatewayStatusMappingTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 28, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, GatewayOrderState.Registered)]
    [InlineData(1, GatewayOrderState.PreAuthorized)]
    [InlineData(2, GatewayOrderState.Deposited)]
    [InlineData(3, GatewayOrderState.Unknown)]
    [InlineData(4, GatewayOrderState.Unknown)]
    [InlineData(5, GatewayOrderState.Unknown)]
    [InlineData(6, GatewayOrderState.Unknown)]
    [InlineData(-1, GatewayOrderState.Unknown)]
    [InlineData(null, GatewayOrderState.Unknown)]
    public void Only_the_codes_of_the_specification_are_known_by_default(int? code, GatewayOrderState expected)
    {
        ClicToPayStatusMap.From(new ClicToPayOptions()).Classify(code).ShouldBe(expected);
    }

    [Fact]
    public void The_codes_documented_by_SMT_are_configured()
    {
        var map = ClicToPayStatusMap.From(new ClicToPayOptions { ReversedStatusCodes = "3", RefundedStatusCodes = " 4 ", DeclinedStatusCodes = "6, 8" });

        map.Classify(3).ShouldBe(GatewayOrderState.Reversed);
        map.Classify(4).ShouldBe(GatewayOrderState.Refunded);
        map.Classify(6).ShouldBe(GatewayOrderState.Declined);
        map.Classify(8).ShouldBe(GatewayOrderState.Declined);
        map.Classify(5).ShouldBe(GatewayOrderState.Unknown);
        map.Classify(2).ShouldBe(GatewayOrderState.Deposited);
    }

    [Theory]
    [InlineData("1", null, null)]
    [InlineData(null, "two", null)]
    [InlineData(null, null, "-6")]
    [InlineData("3", "3", null)]
    [InlineData(null, "2", null)]
    public void Invalid_status_code_settings_are_refused(string? reversed, string? refunded, string? declined)
    {
        var options = new ClicToPayOptions { ReversedStatusCodes = reversed, RefundedStatusCodes = refunded, DeclinedStatusCodes = declined };

        ClicToPayStatusMap.Validate(options, out var map).ShouldNotBeEmpty();
        map.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => ClicToPayStatusMap.From(options));
    }

    [Fact]
    public void A_registered_order_is_confirmed_unpaid_and_expires_after_the_validity()
    {
        var payment = Online(100m, OnlinePaymentFlow.PreAuthorization);
        var status = new GatewayOrderStatus(GatewayOrderState.Registered, 0, GatewayOrderId: "gw-1");

        GatewayStatusInterpreter.Interpret(payment, status, Now.AddMinutes(30))!.State.ShouldBe(GatewayPaymentState.Registered);
        var expired = GatewayStatusInterpreter.Interpret(payment, status, Now.AddHours(1))!;
        expired.State.ShouldBe(GatewayPaymentState.Expired);
        expired.GatewayOrderId.ShouldBe("gw-1");
        expired.Detail.ShouldBe("status 0");
    }

    [Fact]
    public void A_pre_authorization_is_confirmed_with_the_approved_amount_else_the_order_amount()
    {
        var payment = Online(100m, OnlinePaymentFlow.PreAuthorization);

        GatewayStatusInterpreter.Interpret(payment, new GatewayOrderStatus(GatewayOrderState.PreAuthorized, 1, Amount: Money.Tnd(120m), ApprovedAmount: Money.Tnd(100m)), Now)!
            .Amount.ShouldBe(Money.Tnd(100m));
        GatewayStatusInterpreter.Interpret(payment, new GatewayOrderStatus(GatewayOrderState.PreAuthorized, 1, Amount: Money.Tnd(100m)), Now)!
            .Amount.ShouldBe(Money.Tnd(100m));
    }

    [Fact]
    public void The_deposited_amount_comes_from_the_gateway_then_from_the_capture_requested()
    {
        var payment = Online(681.4m, OnlinePaymentFlow.PreAuthorization);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Money.Tnd(681.4m)), AuditActor.System("t"), Now).IsSuccess.ShouldBeTrue();
        payment.RequestGatewayCapture(Money.Tnd(650m), Now).IsSuccess.ShouldBeTrue();

        GatewayStatusInterpreter.Interpret(payment, new GatewayOrderStatus(GatewayOrderState.Deposited, 2, Amount: Money.Tnd(681.4m), DepositedAmount: Money.Tnd(640m)), Now)!
            .Amount.ShouldBe(Money.Tnd(640m));
        GatewayStatusInterpreter.Interpret(payment, new GatewayOrderStatus(GatewayOrderState.Deposited, 2, Amount: Money.Tnd(681.4m)), Now)!
            .Amount.ShouldBe(Money.Tnd(650m), "the only deposit of the order is the one requested");

        var oneStep = Online(80m, OnlinePaymentFlow.OneStep);
        GatewayStatusInterpreter.Interpret(oneStep, new GatewayOrderStatus(GatewayOrderState.Deposited, 2, Amount: Money.Tnd(80m)), Now)!.Amount.ShouldBe(Money.Tnd(80m));
        GatewayStatusInterpreter.Interpret(oneStep, new GatewayOrderStatus(GatewayOrderState.Refunded, 4, Amount: Money.Tnd(80m)), Now)!.State.ShouldBe(GatewayPaymentState.Deposited);
    }

    [Fact]
    public void An_unknown_code_confirms_nothing_unless_the_gateway_acknowledged_our_reversal()
    {
        var payment = Online(100m, OnlinePaymentFlow.PreAuthorization);
        payment.ApplyConfirmedGatewayStatus(new ConfirmedGatewayStatus(GatewayPaymentState.PreAuthorized, Money.Tnd(100m)), AuditActor.System("t"), Now);
        var unknown = new GatewayOrderStatus(GatewayOrderState.Unknown, 3, ActionCode: "0", ActionCodeDescription: "ok");

        GatewayStatusInterpreter.Interpret(payment, unknown, Now).ShouldBeNull();
        payment.RequestGatewayReverse(Now).IsSuccess.ShouldBeTrue();
        GatewayStatusInterpreter.Interpret(payment, unknown, Now).ShouldBeNull("a reversal not acknowledged yet proves nothing");
        payment.AcknowledgeGatewayOperation(PendingGatewayOperation.Reverse).IsSuccess.ShouldBeTrue();

        var reversed = GatewayStatusInterpreter.Interpret(payment, unknown, Now)!;
        reversed.State.ShouldBe(GatewayPaymentState.Reversed);
        reversed.Detail.ShouldBe("status 3, action 0 (ok); reverse.do acknowledged");
    }

    [Fact]
    public void Configured_reversed_and_declined_codes_are_confirmed()
    {
        var payment = Online(100m, OnlinePaymentFlow.PreAuthorization);

        GatewayStatusInterpreter.Interpret(payment, new GatewayOrderStatus(GatewayOrderState.Reversed, 3), Now)!.State.ShouldBe(GatewayPaymentState.Reversed);
        GatewayStatusInterpreter.Interpret(payment, new GatewayOrderStatus(GatewayOrderState.Declined, 6), Now)!.State.ShouldBe(GatewayPaymentState.Declined);
        GatewayStatusInterpreter.Detail(new GatewayOrderStatus(GatewayOrderState.Unknown, null)).ShouldBe("status ?");
    }

    [Fact]
    public void Return_and_failure_addresses_carry_our_order_number()
    {
        var options = new ClicToPayOptions { ReturnUrl = "https://www.agence.tn/payments/clictopay/return", FailUrl = "https://www.agence.tn/payments/clictopay/fail?src=ctp" };

        ClicToPayUrls.ReturnUrlFor(options, "abc").ShouldBe("https://www.agence.tn/payments/clictopay/return?order=abc");
        ClicToPayUrls.FailUrlFor(options, "abc").ShouldBe("https://www.agence.tn/payments/clictopay/fail?src=ctp&order=abc");
        ClicToPayUrls.Append("/x", "orderId", "a b").ShouldBe("/x?orderId=a%20b");
    }

    private static Payment Online(decimal amount, OnlinePaymentFlow flow) =>
        Payment.StartOnline(
            new OnlinePaymentDetails(Guid.CreateVersion7(), Guid.CreateVersion7(), Money.Tnd(amount), flow, "/fr/ok", "/fr/ko", Now.AddHours(1)),
            AuditActor.System("system:web"),
            Now).Value;
}
