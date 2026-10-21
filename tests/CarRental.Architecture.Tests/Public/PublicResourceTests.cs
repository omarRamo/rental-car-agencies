using System.Globalization;
using System.Reflection;
using System.Resources;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Domain.Reporting;
using CarRental.Web.Components.Public.Search;

namespace CarRental.Architecture.Tests.Public;

/// <summary>Texts of the public site and of the errors it introduced exist in French and in English.</summary>
public sealed class PublicResourceTests
{
    private static readonly CultureInfo[] Cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en")];

    private static ResourceManager Manager(string marker) => new(marker, Assemblies.Web);

    public static TheoryData<string> ErrorCodes() =>
    [
        .. typeof(ReportingErrors).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<Error>()
            .Append(ReferentialErrors.AgencyCoordinatesInvalid)
            .Select(e => e.Code),
    ];

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public void Every_error_code_of_the_funnel_measurement_and_the_agency_has_a_text(string code)
    {
        var shared = Manager("CarRental.Web.Resources.SharedResources");

        foreach (var culture in Cultures)
        {
            shared.GetString($"Error.{code}", culture).ShouldNotBeNullOrWhiteSpace($"{code} ({culture.Name})");
        }
    }

    [Fact]
    public void Every_search_error_has_a_text()
    {
        var resources = Manager("CarRental.Web.Resources.Public.PublicResources");

        SearchValidator.ErrorCodes.ShouldNotBeEmpty();
        foreach (var code in SearchValidator.ErrorCodes)
        {
            foreach (var culture in Cultures)
            {
                resources.GetString($"Search.Error.{code}", culture).ShouldNotBeNullOrWhiteSpace($"{code} ({culture.Name})");
            }
        }
    }

    [Fact]
    public void Legal_placeholders_are_marked_for_the_lawyer()
    {
        var resx = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "CarRental.Web", "Resources", "Public", "PublicResources.resx"));

        resx.ShouldContain("<comment>TODO(spec): conditions générales à rédiger avec l'avocat de l'agence (SPEC §14).</comment>");
        resx.ShouldContain("<comment>TODO(spec): texte à valider avec l'avocat de l'agence et le dossier INPDP.</comment>");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CarRental.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
