namespace CarRental.Architecture.Tests;

/// <summary>
/// User interface texts (ADR-0015): every resource file exists in French and English with the same non-empty keys, and
/// every business error code has its <c>Error.&lt;code&gt;</c> text in both languages (ADR-0011).
/// </summary>
public sealed class LocalizationConventionTests
{
    public static TheoryData<string> ResourceFiles() => [.. ResourceCatalog.FrenchBaseNames()];

    [Fact]
    public void Every_resource_file_exists_in_french_and_in_english()
    {
        var french = ResourceCatalog.FrenchBaseNames();

        french.ShouldContain(ResourceCatalog.SharedResources);
        ResourceCatalog.EnglishBaseNames().ShouldBe(french, ignoreOrder: true, "Each Resources/**/X.resx (French) needs its X.en.resx, and the reverse.");
    }

    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void French_and_english_resources_have_the_same_non_empty_keys(string baseName)
    {
        var french = ResourceCatalog.Entries(baseName, ResourceCatalog.French);
        var english = ResourceCatalog.Entries(baseName, ResourceCatalog.English);

        french.ShouldNotBeEmpty();
        french.Keys.Except(english.Keys, StringComparer.Ordinal).ShouldBeEmpty($"Keys of {baseName} missing in English");
        english.Keys.Except(french.Keys, StringComparer.Ordinal).ShouldBeEmpty($"Keys of {baseName} missing in French");
        french.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ShouldBeEmpty($"Empty French texts in {baseName}");
        english.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ShouldBeEmpty($"Empty English texts in {baseName}");
    }

    [Fact]
    public void Every_business_error_code_has_a_french_and_an_english_text()
    {
        var errors = ErrorCatalog.All();
        var french = ResourceCatalog.Entries(ResourceCatalog.SharedResources, ResourceCatalog.French);
        var english = ResourceCatalog.Entries(ResourceCatalog.SharedResources, ResourceCatalog.English);

        // Positive control: the discovery finds the errors of several modules and of the shared kernel.
        errors.ShouldContain(e => e.Code.StartsWith("identity.", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Code.StartsWith("customers.", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Code.StartsWith("money.", StringComparison.Ordinal));
        var missing = errors
            .Where(e => !french.ContainsKey($"Error.{e.Code}") || !english.ContainsKey($"Error.{e.Code}"))
            .Select(e => $"Error.{e.Code} ({e.DeclaredBy})")
            .ToList();

        missing.ShouldBeEmpty("Add these keys to Resources/SharedResources.resx (French) and SharedResources.en.resx (English)");
    }
}
