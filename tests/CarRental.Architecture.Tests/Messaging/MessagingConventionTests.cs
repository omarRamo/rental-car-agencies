using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Inbox;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging;
using CarRental.Web.Resources.Admin.WhatsApp;

namespace CarRental.Architecture.Tests.Messaging;

/// <summary>Every messaging error and every value shown by the WhatsApp inbox has a French and an English text.</summary>
public sealed class MessagingConventionTests
{
    private static readonly CultureInfo French = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static TheoryData<string> DisplayedKeys()
    {
        var keys = new TheoryData<string>();
        void Add<TEnum>(string prefix)
            where TEnum : struct, Enum
        {
            foreach (var value in Enum.GetValues<TEnum>())
            {
                keys.Add($"{prefix}.{value}");
            }
        }

        Add<InboxView>("List.View");
        Add<MessageStatusInfo>("Status");
        Add<MessageAuthorInfo>("Author");
        Add<MediaKindInfo>("Message.Media");
        Add<GearboxPreferenceInfo>("Bot.Gearbox");
        Add<InboxTemplateKind>("Template");
        foreach (var kind in Enum.GetValues<InboxTemplateKind>())
        {
            for (var parameter = 1; parameter <= InboxTemplates.ParameterCount(kind); parameter++)
            {
                keys.Add($"Template.{kind}.Param{parameter}");
            }
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(DisplayedKeys))]
    public void Every_value_shown_by_the_inbox_is_translated(string key)
    {
        var manager = new ResourceManager(typeof(WhatsAppResources).FullName!, Assemblies.Web);

        manager.GetString(key, French).ShouldNotBeNullOrWhiteSpace(key);
        manager.GetString(key, English).ShouldNotBeNullOrWhiteSpace(key);
    }

    [Fact]
    public void Every_error_code_of_the_messaging_module_has_a_user_facing_text()
    {
        var manager = new ResourceManager("CarRental.Web.Resources.SharedResources", Assemblies.Web);
        var errors = Errors();

        errors.Count.ShouldBeGreaterThan(20);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("messaging.");
            manager.GetString($"Error.{error.Code}", French).ShouldNotBeNullOrWhiteSpace(error.Code);
            manager.GetString($"Error.{error.Code}", English).ShouldNotBeNullOrWhiteSpace(error.Code);
        }
    }

    [Fact]
    public void Error_codes_of_the_messaging_module_are_unique()
    {
        var codes = Errors().Select(e => e.Code).ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }

    [Fact]
    public void Contract_enums_keep_the_values_of_the_domain()
    {
        Same<ConversationMode, ConversationModeInfo>();
        Same<MessageDirection, MessageDirectionInfo>();
        Same<MessageKind, MessageKindInfo>();
        Same<MessageAuthor, MessageAuthorInfo>();
        Same<MessageStatus, MessageStatusInfo>();
        Same<MediaKind, MediaKindInfo>();
        Same<GearboxPreference, GearboxPreferenceInfo>();
    }

    private static void Same<TDomain, TContract>()
        where TDomain : struct, Enum
        where TContract : struct, Enum
    {
        var domain = Enum.GetValues<TDomain>().ToDictionary(v => v.ToString(), v => Convert.ToInt32(v, CultureInfo.InvariantCulture));
        var contract = Enum.GetValues<TContract>().ToDictionary(v => v.ToString(), v => Convert.ToInt32(v, CultureInfo.InvariantCulture));
        contract.ShouldBe(domain, typeof(TContract).Name);
    }

    private static List<Error> Errors() =>
        [.. typeof(MessagingErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];
}
