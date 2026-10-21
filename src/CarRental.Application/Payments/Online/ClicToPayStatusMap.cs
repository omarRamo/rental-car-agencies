using System.Globalization;

namespace CarRental.Application.Payments.Online;

/// <summary>
/// Classifies the order status codes of Clic to Pay (SPEC §9): 0 registered, 1 pre-authorized, 2 deposited; the codes of
/// the reversed, refunded and declined states come from configuration once SMT documents them (TODO(spec)). Any other or
/// missing code is <see cref="GatewayOrderState.Unknown"/>: conservative, never a success.
/// </summary>
public sealed class ClicToPayStatusMap
{
    public const int RegisteredCode = 0;
    public const int PreAuthorizedCode = 1;
    public const int DepositedCode = 2;

    private readonly IReadOnlySet<int> _reversed;
    private readonly IReadOnlySet<int> _refunded;
    private readonly IReadOnlySet<int> _declined;

    private ClicToPayStatusMap(IReadOnlySet<int> reversed, IReadOnlySet<int> refunded, IReadOnlySet<int> declined)
    {
        _reversed = reversed;
        _refunded = refunded;
        _declined = declined;
    }

    /// <summary>The map of the configured codes; throws on an invalid configuration (checked at start by the options validator).</summary>
    public static ClicToPayStatusMap From(ClicToPayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = Validate(options, out var map);
        return errors.Count == 0 ? map! : throw new InvalidOperationException(string.Join(" ", errors));
    }

    /// <summary>Checks the configured codes: integers, none of 0, 1, 2, each in one list only.</summary>
    public static IReadOnlyList<string> Validate(ClicToPayOptions options, out ClicToPayStatusMap? map)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var reversed = Parse(options.ReversedStatusCodes, nameof(ClicToPayOptions.ReversedStatusCodes), errors);
        var refunded = Parse(options.RefundedStatusCodes, nameof(ClicToPayOptions.RefundedStatusCodes), errors);
        var declined = Parse(options.DeclinedStatusCodes, nameof(ClicToPayOptions.DeclinedStatusCodes), errors);
        if (reversed.Overlaps(refunded) || reversed.Overlaps(declined) || refunded.Overlaps(declined))
        {
            errors.Add("ClicToPay status codes must belong to one state only.");
        }

        map = errors.Count == 0 ? new ClicToPayStatusMap(reversed, refunded, declined) : null;
        return errors;
    }

    public GatewayOrderState Classify(int? statusCode) => statusCode switch
    {
        RegisteredCode => GatewayOrderState.Registered,
        PreAuthorizedCode => GatewayOrderState.PreAuthorized,
        DepositedCode => GatewayOrderState.Deposited,
        { } code when _reversed.Contains(code) => GatewayOrderState.Reversed,
        { } code when _refunded.Contains(code) => GatewayOrderState.Refunded,
        { } code when _declined.Contains(code) => GatewayOrderState.Declined,
        _ => GatewayOrderState.Unknown,
    };

    private static HashSet<int> Parse(string? codes, string name, List<string> errors)
    {
        var parsed = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(codes))
        {
            return parsed;
        }

        foreach (var part in codes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) || code < 0)
            {
                errors.Add($"ClicToPay:{name} must list non-negative integer codes separated by commas.");
                continue;
            }

            if (code is RegisteredCode or PreAuthorizedCode or DepositedCode)
            {
                errors.Add($"ClicToPay:{name} cannot use the codes 0, 1 and 2 (registered, pre-authorized, deposited).");
                continue;
            }

            parsed.Add(code);
        }

        return parsed;
    }
}
