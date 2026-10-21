using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using CarRental.Web.Resources.Admin.Handovers;

namespace CarRental.Architecture.Tests.Bookings.Handover;

/// <summary>
/// The hand-overs (ADR-0041, ADR-0042): every error has a French and an English text, every value shown by the screens,
/// the contract PDF and its e-mail is translated, and the contract enums mirror the domain ones — the final invoice
/// (phase 3) labels a return charge with <see cref="ReturnChargeInfo.LabelKeyOf"/> from these resources.
/// </summary>
public sealed class HandoverConventionTests
{
    private static readonly CultureInfo French = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static TheoryData<string> DisplayedKeys()
    {
        var keys = new TheoryData<string>();
        void Add<TEnum>(string prefix, params TEnum[] excluded)
            where TEnum : struct, Enum
        {
            foreach (var value in Enum.GetValues<TEnum>().Except(excluded))
            {
                keys.Add($"{prefix}.{value}");
            }
        }

        Add<DamageView>("DamageView");
        Add<DamageType>("DamageType");
        Add<DamageSeverity>("DamageSeverity");
        Add<AccessoryItem>("AccessoryItem");
        Add<ContractLanguage>("Contract.Language");
        Add<IdentityDocumentTypeInfo>("Contract.DocumentType");
        Add<BookingPartyRoleInfo>("Contract.Role");
        Add<PhotoSlot>("Photo.Slot", PhotoSlot.Damage);
        foreach (var slot in PhotoSlots.Guided)
        {
            keys.Add($"Photo.Hint.{slot}");
        }

        foreach (var kind in Enum.GetValues<ReturnChargeKindInfo>())
        {
            keys.Add(ReturnChargeInfo.LabelKeyOf(kind));
        }

        // The details of the proposed lines, whose label arguments the domain fills.
        foreach (var kind in new[] { ReturnChargeKind.ExtraMileage, ReturnChargeKind.Fuel, ReturnChargeKind.LateReturn, ReturnChargeKind.LateContribution, ReturnChargeKind.FranchiseCap })
        {
            keys.Add($"ReturnCharge.Detail.{kind}");
        }

        for (var index = 0; index < 3; index++)
        {
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"Damage.Row.{index}"));
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"Damage.Column.{index}"));
        }

        foreach (var part in new[] { "Subject", "Greeting", "Body", "Closing", "Signature" })
        {
            keys.Add($"Email.Contract.{part}");
        }

        return keys;
    }

    [Theory]
    [MemberData(nameof(DisplayedKeys))]
    public void Every_value_shown_by_the_hand_over_screens_the_contract_and_its_email_is_translated(string key)
    {
        var manager = new ResourceManager(typeof(HandoversResources).FullName!, Assemblies.Web);

        manager.GetString(key, French).ShouldNotBeNullOrWhiteSpace(key);
        manager.GetString(key, English).ShouldNotBeNullOrWhiteSpace(key);
    }

    [Fact]
    public void Every_hand_over_error_has_a_unique_code_and_a_user_facing_text()
    {
        var manager = new ResourceManager(ResourceCatalog.SharedResources, Assemblies.Web);
        List<Error> errors = [.. typeof(HandoverErrors).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.GetValue(null)).OfType<Error>()];

        errors.Count.ShouldBeGreaterThan(40);
        errors.Select(e => e.Code).Distinct(StringComparer.Ordinal).Count().ShouldBe(errors.Count);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("handover.");
            manager.GetString($"Error.{error.Code}", French).ShouldNotBeNullOrWhiteSpace(error.Code);
            manager.GetString($"Error.{error.Code}", English).ShouldNotBeNullOrWhiteSpace(error.Code);
        }
    }

    [Fact]
    public void The_contract_enums_mirror_the_domain_ones()
    {
        Mirror<ReturnChargeKind, ReturnChargeKindInfo>();
        Mirror<ContractLanguage, ContractLanguageInfo>();
    }

    [Fact]
    public void The_label_arguments_of_the_proposed_charges_fit_their_detail_texts()
    {
        var manager = new ResourceManager(typeof(HandoversResources).FullName!, Assemblies.Web);
        var arguments = new Dictionary<ReturnChargeKind, int>
        {
            [ReturnChargeKind.ExtraMileage] = 2,
            [ReturnChargeKind.Fuel] = 2,
            [ReturnChargeKind.LateReturn] = 1,
            [ReturnChargeKind.LateContribution] = 2,
            [ReturnChargeKind.FranchiseCap] = 1,
        };

        foreach (var (kind, count) in arguments)
        {
            foreach (var culture in new[] { French, English })
            {
                var detail = manager.GetString($"ReturnCharge.Detail.{kind}", culture).ShouldNotBeNull();
                Should.NotThrow(() => string.Format(culture, detail, [.. Enumerable.Repeat<object>("1", count)]), $"{kind} ({culture.Name})");
                detail.ShouldContain($"{{{count - 1}}}", customMessage: $"{kind} ({culture.Name}) uses all its arguments");
            }
        }
    }

    private static void Mirror<TDomain, TContract>()
        where TDomain : struct, Enum
        where TContract : struct, Enum
    {
        Enum.GetValues<TContract>()
            .Select(v => (Convert.ToInt32(v, CultureInfo.InvariantCulture), v.ToString()))
            .ShouldBe(Enum.GetValues<TDomain>().Select(v => (Convert.ToInt32(v, CultureInfo.InvariantCulture), v.ToString())), typeof(TContract).Name);
    }
}
