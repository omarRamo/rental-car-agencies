using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Referential;

/// <summary>
/// A host name of an agency's own public site (ADR-0069): <c>www.agence-tunis.tn</c> serves the storefront of the
/// agency without the address segment of the platform (<c>/fr/…</c> instead of <c>/agence-tunis/fr/…</c>). An agency may
/// hold several host names; exactly one is <see cref="IsPrimary"/> when it holds any, and the others redirect to it. A
/// host name is unique on the whole platform: it is a route key, like <see cref="Agency.Slug"/>.
/// </summary>
public sealed class AgencyDomain : Entity, IAgencyOwned
{
    private AgencyDomain()
    {
    }

    private AgencyDomain(Guid id, Guid agencyId, string hostName, bool isPrimary, DateTimeOffset now)
        : base(id)
    {
        AgencyId = agencyId;
        HostName = hostName;
        IsPrimary = isPrimary;
        CreatedAtUtc = now;
    }

    public Guid AgencyId { get; private set; }

    /// <summary>The host name as stored: lower case, ASCII (punycode for an internationalized name), no port.</summary>
    public string HostName { get; private set; } = null!;

    /// <summary>The host the links of the agency's site carry; its other hosts redirect to it.</summary>
    public bool IsPrimary { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Attaches <paramref name="hostName"/> to the agency; the caller checks that the host is free on the platform.</summary>
    public static Result<AgencyDomain> Create(Guid agencyId, string? hostName, bool isPrimary, DateTimeOffset now)
    {
        if (agencyId == Guid.Empty)
        {
            return ReferentialErrors.AgencyRequired;
        }

        var normalized = AgencyHostName.Normalize(hostName);
        return normalized.IsFailure
            ? normalized.Error
            : new AgencyDomain(NewId(), agencyId, normalized.Value, isPrimary, now);
    }

    public void MakePrimary() => IsPrimary = true;

    public void MakeSecondary() => IsPrimary = false;
}

/// <summary>
/// The rules of a host name of an agency's site: at most <see cref="MaxLength"/> characters once normalized, at least
/// two labels of 1 to 63 letters, digits or hyphens (no leading or trailing hyphen), a last label that is not all
/// digits, no scheme, port, path or user part. Internationalized names are stored in their ASCII (punycode) form.
/// </summary>
public static class AgencyHostName
{
    public const int MaxLength = 253;

    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    /// <summary>The host name as stored when it is valid.</summary>
    public static Result<string> Normalize(string? value)
    {
        var host = value?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
        if (host.Length == 0 || host.Length > MaxLength || host.Any(c => c is ':' or '/' or '\\' or '@' or '?' or '#' || char.IsWhiteSpace(c)))
        {
            return ReferentialErrors.AgencyDomainInvalid;
        }

        try
        {
            host = Idn.GetAscii(host);
        }
        catch (ArgumentException)
        {
            return ReferentialErrors.AgencyDomainInvalid;
        }

        if (host.Length > MaxLength)
        {
            return ReferentialErrors.AgencyDomainInvalid;
        }

        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(label => !IsLabel(label)) || labels[^1].All(char.IsAsciiDigit))
        {
            return ReferentialErrors.AgencyDomainInvalid;
        }

        return host;
    }

    private static bool IsLabel(string label) =>
        label.Length is >= 1 and <= 63
        && label[0] != '-'
        && label[^1] != '-'
        && label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
