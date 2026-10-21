using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using CarRental.Application.Channels.Administration;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Web.Resources.Admin.Channels;

namespace CarRental.Architecture.Tests.Channels;

/// <summary>
/// Conventions of the Channels module (ADR-0052): every error has a French and an English text, every value shown by
/// the channels screens is translated, and both languages use the same format placeholders.
/// </summary>
public sealed partial class ChannelsConventionTests
{
    private static readonly CultureInfo[] Cultures = [ResourceCatalog.French, ResourceCatalog.English];

    private static readonly ResourceManager Shared = new(ResourceCatalog.SharedResources, Assemblies.Web);

    private static readonly ResourceManager Screens = new(typeof(ChannelsResources).FullName!, Assemblies.Web);

    public static TheoryData<string> DisplayedKeys()
    {
        var keys = new TheoryData<string>();
        void Add<TEnum>(string prefix, string suffix = "")
            where TEnum : struct, Enum
        {
            foreach (var name in Enum.GetNames<TEnum>())
            {
                keys.Add($"{prefix}.{name}{suffix}");
            }
        }

        Add<ChannelAdapterKind>("Adapter");
        Add<ChannelAdapterKind>("Adapter", ".Help");
        Add<ChannelState>("State");
        Add<ChannelState>("State", ".Help");
        Add<ChannelPaymentMode>("PaymentMode");
        Add<ChannelMappingKind>("MappingKind");
        Add<ChannelQuotaKind>("QuotaKind");
        Add<ChannelQuotaKind>("Restrictions.Add");
        Add<ChannelMessageDirection>("Direction");
        Add<ChannelMessageOperation>("Operation");
        Add<ChannelMessageStatus>("MessageStatus");
        Add<ChannelSyncOperation>("SyncOperation");
        Add<ChannelSyncStatus>("SyncStatus");
        Add<SyncQueueFilter>("Sync.Filter");
        Add<ChannelBookingSource>("Source");
        Add<ChannelBookingState>("BookingState");
        Add<ChannelCommissionSource>("CommissionSource");
        Add<ChannelReplicationKind>("Replication");
        Add<SalesChannel>("SalesChannel");
        return keys;
    }

    [Fact]
    public void Every_error_code_of_the_channels_module_has_a_user_facing_text()
    {
        var errors = Errors();

        errors.Count.ShouldBeGreaterThan(50);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("channels.");
            foreach (var culture in Cultures)
            {
                Shared.GetString($"Error.{error.Code}", culture).ShouldNotBeNullOrWhiteSpace($"Error.{error.Code} ({culture.Name})");
            }
        }
    }

    [Fact]
    public void Error_codes_of_the_channels_module_are_unique()
    {
        var codes = Errors().Select(e => e.Code).ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }

    [Theory]
    [MemberData(nameof(DisplayedKeys))]
    public void Every_value_shown_by_the_channels_screens_is_translated(string key)
    {
        foreach (var culture in Cultures)
        {
            Screens.GetString(key, culture).ShouldNotBeNullOrWhiteSpace($"{key} ({culture.Name})");
        }
    }

    [Fact]
    public void French_and_english_texts_of_the_channels_screens_use_the_same_placeholders()
    {
        var french = ResourceCatalog.Entries(typeof(ChannelsResources).FullName!, ResourceCatalog.French);
        var english = ResourceCatalog.Entries(typeof(ChannelsResources).FullName!, ResourceCatalog.English);

        french.Count.ShouldBeGreaterThan(200);
        foreach (var (key, text) in french)
        {
            Placeholders(english[key]).ShouldBe(Placeholders(text), key);
        }
    }

    private static List<Error> Errors() =>
        [.. typeof(ChannelsErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];

    private static List<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d+(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
