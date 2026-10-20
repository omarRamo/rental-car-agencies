using System.Collections.Frozen;
using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>
/// ISO 3166-1 alpha-2 country codes (nationality, document issuing country, address country), stored upper case.
/// The list holds the 249 officially assigned codes plus <c>XK</c> (Kosovo), user-assigned but printed on travel documents.
/// </summary>
public static class CountryCodes
{
    public const int Length = 2;

    /// <summary>Tunisia: Tunisian nationals present their national identity card (CIN), foreigners a passport.</summary>
    public const string Tunisia = "TN";

    private const string Assigned =
        "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ " +
        "BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ " +
        "CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ " +
        "DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR " +
        "GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU " +
        "ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ " +
        "LA LB LC LI LK LR LS LT LU LV LY " +
        "MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ " +
        "NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA " +
        "RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ " +
        "TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ " +
        "VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW XK";

    private static readonly FrozenSet<string> Known;

    static CountryCodes()
    {
        All = Assigned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Known = All.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Every accepted code, in alphabetical order (XK last).</summary>
    public static IReadOnlyList<string> All { get; }

    /// <summary>True when <paramref name="code"/> is an accepted upper-case code.</summary>
    public static bool IsAssigned(string? code) => code is { Length: Length } && Known.Contains(code);

    /// <summary>Trims and upper-cases <paramref name="value"/>, then checks it is an accepted code.</summary>
    public static Result<string> Normalize(string? value)
    {
        var code = value?.Trim().ToUpperInvariant();
        if (!IsAssigned(code))
        {
            return CustomersErrors.CountryInvalid;
        }

        return code!;
    }
}
