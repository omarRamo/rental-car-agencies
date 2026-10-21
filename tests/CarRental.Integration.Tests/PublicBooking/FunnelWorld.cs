using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Common.Security;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Bookings;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>A request sent through the funnel services, and how to reach its booking again.</summary>
internal sealed record FunnelRequest(Guid BookingId, string Number, string Email, string LastName, FunnelGuarantee Guarantee);

/// <summary>
/// The booking funnel driven through its services on an agency of the test (the public pages serve the demo agency
/// only): a web request submitted as the public site does, its guarantee paid on the simulated bank, the agent's
/// decisions — for the scenarios that need settings of their own (payment link mode, cancellation fee, short holds).
/// </summary>
internal static class FunnelWorld
{
    public static readonly UserContextSnapshot Funnel = UserContextSnapshot.System("system:public-funnel");

    /// <summary>A request for the ECMR category of <paramref name="agency"/>, its main driver typed in step 2.</summary>
    public static async Task<FunnelRequest> SubmitAsync(
        IntegrationHost host,
        BookingAgency agency,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        CancellationToken cancellationToken,
        FunnelDriverInput? driver = null,
        string culture = "fr")
    {
        var suffix = Visitor.Unique();
        var main = driver ?? Driver(suffix);
        await using var scope = host.CreateScope(Funnel);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var started = await funnel.StartAsync(
            new FunnelStartRequest(agency.AgencyId, culture, SalesChannel.Web, null, agency.Ecmr, "ECMR", agency.OfficeId, agency.OfficeId, pickupAt, returnAt, "?lieu=X", null),
            cancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : null);
        var token = started.Value.Token;

        var saved = await funnel.SaveDriversAsync(token, new FunnelDriversInput(main, false, null, true, false, false), cancellationToken);
        saved.IsSuccess.ShouldBeTrue(saved.IsFailure ? saved.Error.ToString() : null);
        saved.Value.IsValid.ShouldBeTrue(string.Join(", ", saved.Value.Errors.Select(e => $"{e.Field}:{e.Code}")));

        var submitted = await funnel.SubmitAsync(token, termsAccepted: true, "http://localhost/", culture, cancellationToken);
        submitted.IsSuccess.ShouldBeTrue(submitted.IsFailure ? submitted.Error.ToString() : null);
        return new FunnelRequest(submitted.Value.BookingId, submitted.Value.Number, main.Email!, main.LastName!, submitted.Value.Guarantee);
    }

    /// <summary>A valid French driver (passport, licence held 20 years) with a unique name, e-mail and phone.</summary>
    public static FunnelDriverInput Driver(string suffix, string? email = null) => new(
        "Amel",
        $"World{suffix}",
        "FR",
        "1984-03-14",
        Visitor.RandomPhone(),
        email ?? $"world.{suffix.ToLowerInvariant()}@example.test",
        $"L{suffix}",
        "2004-06-01",
        null,
        "FR",
        true,
        $"P{suffix}",
        null,
        "2035-01-31");

    /// <summary>The pre-authorization of the request paid on the simulated bank, then the return read server to server.</summary>
    public static async Task<OnlinePaymentInfo> HoldGuaranteeAsync(IntegrationHost host, FunnelRequest request, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope(Funnel);
        var funnel = scope.ServiceProvider.GetRequiredService<FunnelService>();
        var confirmation = PublicBookingPaths.Confirmation("fr", request.Number);
        var started = await funnel.StartGuaranteeAsync(request.BookingId, "fr", confirmation, confirmation, cancellationToken);
        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.ToString() : null);
        var redirect = started.Value.ShouldNotBeNull();
        return await PayAsync(host, redirect.RedirectUrl, cancellationToken);
    }

    /// <summary>Pays the order of a payment page of the simulated bank and handles the return like the browser would.</summary>
    public static async Task<OnlinePaymentInfo> PayAsync(IntegrationHost host, string paymentPageUrl, CancellationToken cancellationToken)
    {
        paymentPageUrl.ShouldStartWith(FakeClicToPay.PagePathPrefix);
        var gatewayOrderId = paymentPageUrl[FakeClicToPay.PagePathPrefix.Length..];
        await using var scope = host.CreateScope();
        var fake = scope.ServiceProvider.GetRequiredService<FakeClicToPay>();
        var order = fake.FindOrder(gatewayOrderId).ShouldNotBeNull();
        fake.Pay(gatewayOrderId).IsSuccess.ShouldBeTrue();
        var payments = scope.ServiceProvider.GetRequiredService<IOnlinePayments>();
        var handled = await payments.HandleReturnAsync(new OnlinePaymentReturnRequest(order.OrderNumber, gatewayOrderId, FromFailureAddress: false), cancellationToken);
        handled.IsSuccess.ShouldBeTrue(handled.IsFailure ? handled.Error.ToString() : null);
        return (await payments.FindAsync(handled.Value.PaymentId, cancellationToken)).ShouldNotBeNull();
    }

    /// <summary>The validation by the agent (capture of the guarantee, or the payment link mode).</summary>
    public static Task<Result<ValidationOutcome>> ValidateAsync(IntegrationHost host, Guid bookingId, CancellationToken cancellationToken, ValidationPaymentChoice choice = ValidationPaymentChoice.Automatic) =>
        BookingWorld.RunAsync<BookingWorkflowService, Result<ValidationOutcome>>(host, BookingWorld.Agent, s => s.ValidateAsync(bookingId, choice, cancellationToken));

    public static async Task<BookingStatusInfo> StatusAsync(IntegrationHost host, Guid bookingId, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var booking = await scope.ServiceProvider.GetRequiredService<IBookingsQueries>().GetSummaryAsync(bookingId, cancellationToken);
        return booking.ShouldNotBeNull().Status;
    }

    public static async Task<IReadOnlyList<OnlinePaymentInfo>> PaymentsAsync(IntegrationHost host, Guid bookingId, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOnlinePayments>().GetBookingOnlinePaymentsAsync(bookingId, cancellationToken);
    }

    /// <summary>Waits for a state reached through the outbox (condition, never a fixed delay).</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> reached, string because, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var value = await probe();
            if (reached(value))
            {
                return value;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, because);
            await Task.Delay(200, cancellationToken);
        }
    }
}
