namespace CarRental.Application.Payments.Online;

/// <summary>
/// Settings of the Clic to Pay gateway (section <c>ClicToPay</c>; ADR-0038). Validated at start. On Azure the credentials
/// are Key Vault secrets <c>ClicToPay--UserName</c> and <c>ClicToPay--Password</c>. Everything not stated by SPEC §9 is a
/// <c>TODO(spec)</c> to confirm in the Clic to Pay test environment (<c>docs/recette-clictopay.md</c>).
/// </summary>
public sealed class ClicToPayOptions
{
    public const string SectionName = "ClicToPay";

    /// <summary>Online payment is available (off by default: production opts in with its credentials).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Uses the in-app simulated gateway (<see cref="FakeClicToPay"/>, pages under <c>/dev/clictopay</c>) instead of the
    /// REST API. Development and Testing only: refused at start in any other environment.
    /// </summary>
    public bool UseFake { get; set; }

    /// <summary>Root of the REST API, ending with a slash (test environment by default, production URL given by SMT).</summary>
    public Uri BaseUrl { get; set; } = new("https://test.clictopay.com/payment/rest/");

    /// <summary>API user name (secret on Azure: <c>ClicToPay--UserName</c>).</summary>
    public string? UserName { get; set; }

    /// <summary>API password (secret on Azure: <c>ClicToPay--Password</c>). Never logged, never stored.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Minor units per dinar sent in <c>amount</c>: 1000 (millimes, TND has three decimals). TODO(spec): confirm with a
    /// payment of 1.000 TND in the test environment (the SDK cited by the specification is ambiguous).
    /// </summary>
    public int AmountMultiplier { get; set; } = 1000;

    /// <summary>
    /// ISO 4217 numeric currency sent as <c>currency</c> (e.g. <c>788</c> for TND); empty: not sent, the merchant account
    /// default applies. TODO(spec): confirm with SMT.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>Language of the payment page when the request gives none (two letters, ISO 639-1).</summary>
    public string Language { get; set; } = "fr";

    /// <summary>
    /// Address the gateway sends the browser back to after a payment (<c>returnUrl</c>); the order number is appended as
    /// <c>order</c>. Absolute HTTPS address with the real gateway (built from <c>PublicSite:BaseUrl</c> when relative).
    /// </summary>
    public string ReturnUrl { get; set; } = "/payments/clictopay/return";

    /// <summary>Address the gateway sends the browser back to after a failure (<c>failUrl</c>); same rules as <see cref="ReturnUrl"/>.</summary>
    public string FailUrl { get; set; } = "/payments/clictopay/fail";

    /// <summary>Time-out of one HTTP call to the gateway, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Attempts of a status read (<c>getOrderStatusExtended.do</c>, idempotent) on a transport failure; other operations are never retried.</summary>
    public int StatusReadAttempts { get; set; } = 3;

    /// <summary>Pause before a new status read attempt, multiplied by the attempt number (milliseconds).</summary>
    public int StatusRetryDelayMilliseconds { get; set; } = 500;

    /// <summary>
    /// Sends the validity of a payment link as <c>sessionTimeoutSecs</c> (BPC parameter). TODO(spec): off until SMT
    /// confirms the parameter and its maximum; until then the gateway session may end before the link validity.
    /// </summary>
    public bool SendSessionTimeout { get; set; }

    /// <summary>
    /// Order status codes meaning "reversed", comma separated (e.g. <c>3</c>). TODO(spec): empty until SMT documents
    /// them; an unknown code is never taken for a success (the payment stays pending and is flagged).
    /// </summary>
    public string? ReversedStatusCodes { get; set; }

    /// <summary>Order status codes meaning "refunded" (e.g. <c>4</c>). TODO(spec): see <see cref="ReversedStatusCodes"/>.</summary>
    public string? RefundedStatusCodes { get; set; }

    /// <summary>Order status codes meaning "declined" (e.g. <c>6</c>). TODO(spec): see <see cref="ReversedStatusCodes"/>.</summary>
    public string? DeclinedStatusCodes { get; set; }
}

/// <summary>Return and failure addresses of an order: the configured address with our order number appended as <c>order</c>.</summary>
public static class ClicToPayUrls
{
    public const string OrderParameter = "order";

    /// <summary>The order id parameter appended by the gateway to the return address (BPC behaviour, TODO(spec)).</summary>
    public const string GatewayOrderIdParameter = "orderId";

    public static string ReturnUrlFor(ClicToPayOptions options, string orderNumber)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Append(options.ReturnUrl, OrderParameter, orderNumber);
    }

    public static string FailUrlFor(ClicToPayOptions options, string orderNumber)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Append(options.FailUrl, OrderParameter, orderNumber);
    }

    /// <summary>Appends a query parameter (escaped) to an absolute or relative address.</summary>
    public static string Append(string url, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
    }
}
