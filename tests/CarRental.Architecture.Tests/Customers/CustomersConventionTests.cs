using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Architecture.Tests.Customers;

/// <summary>Conventions of the Customers module (ADR-0011): every business error has its user-facing text in French and English.</summary>
public sealed class CustomersConventionTests
{
    [Fact]
    public void Every_error_code_of_the_customers_module_has_a_user_facing_text()
    {
        var manager = new ResourceManager("CarRental.Web.Resources.SharedResources", Assemblies.Web);
        var errors = typeof(CustomersErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<Error>()
            .ToList();

        errors.Count.ShouldBeGreaterThan(30);
        foreach (var error in errors)
        {
            error.Code.ShouldStartWith("customers.");
            manager.GetString($"Error.{error.Code}", CultureInfo.InvariantCulture).ShouldNotBeNull(error.Code);
            manager.GetString($"Error.{error.Code}", CultureInfo.GetCultureInfo("en")).ShouldNotBeNull(error.Code);
        }
    }

    [Fact]
    public void Every_label_built_from_a_value_exists_in_french_and_english()
    {
        var manager = new ResourceManager("CarRental.Web.Resources.Admin.Customers.CustomersAdminResources", Assemblies.Web);
        var keys = Enum.GetNames<IdentityDocumentTypeInfo>().Select(n => $"DocumentType.{n}")
            .Concat(Enum.GetNames<ConsentPurposeInfo>().Select(n => $"Consent.Purpose.{n}"))
            .Concat(Enum.GetNames<ConsentChannelInfo>().Select(n => $"Consent.Channel.{n}"))
            .Concat(Enum.GetNames<EligibilityIssueKindInfo>().Select(n => $"Eligibility.Issue.{n}"))
            .Concat(Enum.GetNames<DocumentImageSideInfo>().SelectMany(n => new[] { $"Side.{n}", $"Image.View.{n}" }))
            .Concat(DocumentAccessPurposes.All.Select(p => $"Viewer.Purpose.{p}"))
            .Concat(Enum.GetNames<PersonMatchKindInfo>().Where(n => n != nameof(PersonMatchKindInfo.Created)).Select(n => $"Person.Found.{n}"));

        foreach (var key in keys)
        {
            manager.GetString(key, CultureInfo.InvariantCulture).ShouldNotBeNullOrWhiteSpace(key);
            manager.GetString(key, CultureInfo.GetCultureInfo("en")).ShouldNotBeNullOrWhiteSpace(key);
        }
    }

    [Fact]
    public void Error_codes_of_the_customers_module_are_unique()
    {
        var codes = typeof(CustomersErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<Error>()
            .Select(e => e.Code)
            .ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
    }
}
