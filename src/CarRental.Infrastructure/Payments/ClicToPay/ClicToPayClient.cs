using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Payments.ClicToPay;

/// <summary>
/// The Clic to Pay REST API (SPEC §9, ADR-0038) as a typed <see cref="HttpClient"/>: form-encoded POSTs under
/// <c>ClicToPay:BaseUrl</c> with the API credentials in the body (never in an address), a time-out per call, retries
/// for status reads only (idempotent) — registrations, deposits, reversals and refunds are sent once — and one call-log
/// row per call (operation, order, HTTP status, error and status codes, duration; no credentials, no card data, no raw
/// answer). Logs and traces carry the same non-sensitive fields.
/// </summary>
internal sealed partial class ClicToPayClient(
    HttpClient http,
    IOptions<ClicToPayOptions> options,
    IGatewayCallLog callLog,
    TimeProvider timeProvider,
    ILogger<ClicToPayClient> logger) : IClicToPayGateway
{
    private static readonly Counter<long> Calls = CarRentalTelemetry.Meter.CreateCounter<long>(
        "carrental.clictopay.calls",
        description: "Calls to the Clic to Pay API by operation and outcome.");

    public bool IsEnabled => true;

    public Task<Result<GatewayRegisteredOrder>> RegisterAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        RegisterCoreAsync(GatewayOperation.Register, registration, cancellationToken);

    public Task<Result<GatewayRegisteredOrder>> RegisterPreAuthAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        RegisterCoreAsync(GatewayOperation.RegisterPreAuth, registration, cancellationToken);

    public Task<Result> DepositAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default) =>
        OperateWithAmountAsync(GatewayOperation.Deposit, operation, cancellationToken);

    public Task<Result> RefundAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default) =>
        OperateWithAmountAsync(GatewayOperation.Refund, operation, cancellationToken);

    public async Task<Result> ReverseAsync(GatewayOrderReference order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.GatewayOrderId);
        var call = new CallContext(GatewayOperation.Reverse, order.OrderNumber, order.GatewayOrderId, null, null);
        var exchange = await SendAsync(call, ClicToPayProtocol.Order(options.Value, order.GatewayOrderId), cancellationToken);
        return await AcknowledgementAsync(call, exchange, cancellationToken);
    }

    public async Task<Result<GatewayOrderStatus>> GetOrderStatusExtendedAsync(GatewayOrderReference order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var settings = options.Value;
        var attempts = Math.Clamp(settings.StatusReadAttempts, 1, 5);
        var call = new CallContext(GatewayOperation.GetOrderStatusExtended, order.OrderNumber, order.GatewayOrderId, null, null);
        for (var attempt = 1; ; attempt++)
        {
            var exchange = await SendAsync(call, ClicToPayProtocol.Status(settings, order), cancellationToken);
            if (exchange.Root is not { } root)
            {
                await RecordAsync(call, exchange, exchange.Failure ?? GatewayCallOutcome.InvalidResponse, cancellationToken: cancellationToken);
                if (exchange.Failure == GatewayCallOutcome.Unavailable && attempt < attempts)
                {
                    // The only operation retried: a status read changes nothing at the gateway.
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, settings.StatusRetryDelayMilliseconds) * attempt), timeProvider, cancellationToken);
                    continue;
                }

                return exchange.Failure == GatewayCallOutcome.Unavailable ? PaymentsErrors.GatewayUnavailable : PaymentsErrors.GatewayInvalidResponse;
            }

            var (code, message) = ClicToPayProtocol.Error(root);
            var fields = ClicToPayProtocol.ReadStatus(root);
            if (!ClicToPayProtocol.IsSuccess(code))
            {
                await RecordAsync(call, exchange, GatewayCallOutcome.GatewayError, code, message, fields.OrderStatus, fields.ActionCode, cancellationToken);
                return PaymentsErrors.GatewayRejected;
            }

            if (fields.OrderStatus is null)
            {
                await RecordAsync(call, exchange, GatewayCallOutcome.InvalidResponse, code, message, cancellationToken: cancellationToken);
                return PaymentsErrors.GatewayInvalidResponse;
            }

            await RecordAsync(call, exchange, GatewayCallOutcome.Succeeded, code, message, fields.OrderStatus, fields.ActionCode, cancellationToken);
            return ToStatus(order, fields, settings);
        }
    }

    private static GatewayOrderStatus ToStatus(GatewayOrderReference order, StatusFields fields, ClicToPayOptions settings)
    {
        var multiplier = settings.AmountMultiplier;
        Money? Amount(long? minor) => minor is { } value and >= 0 ? ClicToPayProtocol.FromMinorUnits(value, multiplier) : null;
        return new GatewayOrderStatus(
            ClicToPayStatusMap.From(settings).Classify(fields.OrderStatus),
            fields.OrderStatus,
            fields.OrderNumber,
            order.GatewayOrderId,
            Amount(fields.Amount),
            Amount(fields.ApprovedAmount) is { IsPositive: true } approved ? approved : null,
            Amount(fields.DepositedAmount) is { IsPositive: true } deposited ? deposited : null,
            Amount(fields.RefundedAmount),
            fields.ActionCode,
            fields.ActionCodeDescription);
    }

    private async Task<Result<GatewayRegisteredOrder>> RegisterCoreAsync(GatewayOperation operation, GatewayRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var settings = options.Value;
        var amount = ClicToPayProtocol.ToMinorUnits(registration.Amount, settings.AmountMultiplier);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        var call = new CallContext(operation, registration.OrderNumber, null, registration.Amount, null);
        var exchange = await SendAsync(call, ClicToPayProtocol.Registration(settings, registration, amount.Value), cancellationToken);
        if (exchange.Root is not { } root)
        {
            await RecordAsync(call, exchange, exchange.Failure ?? GatewayCallOutcome.InvalidResponse, cancellationToken: cancellationToken);
            return exchange.Failure == GatewayCallOutcome.Unavailable ? PaymentsErrors.GatewayUnavailable : PaymentsErrors.GatewayInvalidResponse;
        }

        var (code, message) = ClicToPayProtocol.Error(root);
        var (orderId, formUrl) = ClicToPayProtocol.Registered(root);
        var answered = call with { GatewayOrderId = orderId };
        if (!ClicToPayProtocol.IsSuccess(code))
        {
            await RecordAsync(answered, exchange, GatewayCallOutcome.GatewayError, code, message, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        if (orderId is null || orderId.Length > GatewayCallLog.GatewayOrderIdMaxLength
            || formUrl is null || formUrl.Length > Payment.GatewayFormUrlMaxLength || !IsPaymentPage(formUrl))
        {
            await RecordAsync(answered, exchange, GatewayCallOutcome.InvalidResponse, code, message, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayInvalidResponse;
        }

        await RecordAsync(answered, exchange, GatewayCallOutcome.Succeeded, code, message, cancellationToken: cancellationToken);
        return new GatewayRegisteredOrder(orderId, formUrl);
    }

    /// <summary>The payment page must be an absolute HTTPS address (HTTP on the local machine for the test stubs).</summary>
    private static bool IsPaymentPage(string formUrl) =>
        Uri.TryCreate(formUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private async Task<Result> OperateWithAmountAsync(GatewayOperation operation, GatewayOrderOperation request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GatewayOrderId);
        var settings = options.Value;
        var amount = ClicToPayProtocol.ToMinorUnits(request.Amount, settings.AmountMultiplier);
        if (amount.IsFailure)
        {
            return amount.Error;
        }

        var call = new CallContext(operation, request.OrderNumber, request.GatewayOrderId, request.Amount, request.RefundId);
        var exchange = await SendAsync(call, ClicToPayProtocol.OrderAmount(settings, request.GatewayOrderId, amount.Value), cancellationToken);
        return await AcknowledgementAsync(call, exchange, cancellationToken);
    }

    /// <summary>An answer that only acknowledges an operation: error code 0 (or none) is a success, anything else a refusal.</summary>
    private async Task<Result> AcknowledgementAsync(CallContext call, Exchange exchange, CancellationToken cancellationToken)
    {
        if (exchange.Root is not { } root)
        {
            await RecordAsync(call, exchange, exchange.Failure ?? GatewayCallOutcome.InvalidResponse, cancellationToken: cancellationToken);
            return exchange.Failure == GatewayCallOutcome.Unavailable ? PaymentsErrors.GatewayUnavailable : PaymentsErrors.GatewayInvalidResponse;
        }

        var (code, message) = ClicToPayProtocol.Error(root);
        if (!ClicToPayProtocol.IsSuccess(code))
        {
            await RecordAsync(call, exchange, GatewayCallOutcome.GatewayError, code, message, cancellationToken: cancellationToken);
            return PaymentsErrors.GatewayRejected;
        }

        await RecordAsync(call, exchange, GatewayCallOutcome.Succeeded, code, message, cancellationToken: cancellationToken);
        return Result.Success();
    }

    /// <summary>One POST: the parsed JSON object of the answer, or why there is none (no answer, unexpected answer).</summary>
    private async Task<Exchange> SendAsync(CallContext call, List<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var timestamp = timeProvider.GetTimestamp();
        using var activity = CarRentalTelemetry.ActivitySource.StartActivity($"clictopay {ClicToPayProtocol.PathOf(call.Operation)}", ActivityKind.Client);
        activity?.SetTag("clictopay.operation", call.Operation.ToString());
        activity?.SetTag("clictopay.order_number", call.OrderNumber);
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await http.PostAsync(new Uri(ClicToPayProtocol.PathOf(call.Operation), UriKind.Relative), content, cancellationToken);
            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500)
            {
                return new Exchange(started, timeProvider.GetElapsedTime(timestamp), status, null, GatewayCallOutcome.Unavailable);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new Exchange(started, timeProvider.GetElapsedTime(timestamp), status, null, GatewayCallOutcome.InvalidResponse);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? new Exchange(started, timeProvider.GetElapsedTime(timestamp), status, document.RootElement.Clone(), null)
                    : new Exchange(started, timeProvider.GetElapsedTime(timestamp), status, null, GatewayCallOutcome.InvalidResponse);
            }
            catch (JsonException)
            {
                return new Exchange(started, timeProvider.GetElapsedTime(timestamp), status, null, GatewayCallOutcome.InvalidResponse);
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient time-out (ClicToPay:TimeoutSeconds): the request may have been processed.
            return new Exchange(started, timeProvider.GetElapsedTime(timestamp), null, null, GatewayCallOutcome.Unavailable);
        }
        catch (HttpRequestException)
        {
            // Network failure, or an answer above the buffer limit: no usable answer.
            return new Exchange(started, timeProvider.GetElapsedTime(timestamp), null, null, GatewayCallOutcome.Unavailable);
        }
    }

    private async Task RecordAsync(
        CallContext call,
        Exchange exchange,
        GatewayCallOutcome outcome,
        string? errorCode = null,
        string? errorMessage = null,
        int? orderStatus = null,
        string? actionCode = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(1, new KeyValuePair<string, object?>("operation", call.Operation.ToString()), new KeyValuePair<string, object?>("outcome", outcome.ToString()));
        Activity.Current?.SetTag("clictopay.outcome", outcome.ToString());
        LogCall(call.Operation, call.OrderNumber, outcome, exchange.HttpStatus, errorCode, orderStatus, (long)exchange.Duration.TotalMilliseconds);
        await callLog.RecordAsync(
            new GatewayCallLogEntry(
                call.Operation,
                call.OrderNumber,
                outcome,
                exchange.StartedAtUtc,
                exchange.Duration,
                call.GatewayOrderId,
                call.Amount,
                exchange.HttpStatus,
                errorCode is ClicToPayProtocol.SuccessCode ? null : errorCode,
                errorCode is null or ClicToPayProtocol.SuccessCode ? null : errorMessage,
                orderStatus,
                actionCode,
                call.RefundId,
                Activity.Current?.TraceId.ToString()),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Clic to Pay {Operation} for order {OrderNumber}: {Outcome} (HTTP {HttpStatus}, error code {ErrorCode}, order status {OrderStatus}) in {DurationMilliseconds} ms.")]
    private partial void LogCall(GatewayOperation operation, string orderNumber, GatewayCallOutcome outcome, int? httpStatus, string? errorCode, int? orderStatus, long durationMilliseconds);

    private sealed record CallContext(GatewayOperation Operation, string OrderNumber, string? GatewayOrderId, Money? Amount, Guid? RefundId);

    /// <summary>What came back from one POST: the JSON object, or the failure kind when there is none.</summary>
    private sealed record Exchange(DateTimeOffset StartedAtUtc, TimeSpan Duration, int? HttpStatus, JsonElement? Root, GatewayCallOutcome? Failure);
}
