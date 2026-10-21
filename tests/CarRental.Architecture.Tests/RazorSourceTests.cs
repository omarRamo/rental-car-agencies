using System.Text.RegularExpressions;

namespace CarRental.Architecture.Tests;

/// <summary>Conventions checked on the Razor sources of the web project, where the compiled assembly cannot show them.</summary>
public sealed partial class RazorSourceTests
{
    private static readonly string Components = Path.Combine(RepositoryRoot(), "src", "CarRental.Web", "Components");

    [Fact]
    public void Back_office_tables_page_with_the_accessible_pager()
    {
        var files = Directory.EnumerateFiles(Components, "*.razor", SearchOption.AllDirectories).ToList();
        var offenders = files
            .Where(f => !string.Equals(Path.GetFileName(f), "AdminTablePager.razor", StringComparison.Ordinal))
            .SelectMany(f => MudTablePagerTag().Matches(File.ReadAllText(f))
                .Where(tag => !tag.Value.Contains("HideRowsPerPage=\"true\"", StringComparison.Ordinal))
                .Select(_ => Path.GetRelativePath(Components, f)))
            .ToList();

        files.ShouldNotBeEmpty();
        offenders.ShouldBeEmpty(
            "MudTablePager renders its page-size select without an accessible name: use <AdminTablePager /> (or HideRowsPerPage=\"true\" for a fixed page size)");
    }

    /// <summary>The folder holding CarRental.slnx, above the test binaries.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CarRental.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"CarRental.slnx not found above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex("<MudTablePager\\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex MudTablePagerTag();
}
