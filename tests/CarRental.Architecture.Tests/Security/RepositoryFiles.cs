namespace CarRental.Architecture.Tests.Security;

/// <summary>Files of the repository read by the convention tests (infrastructure templates, settings).</summary>
internal static class RepositoryFiles
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>The path of a file below the folder that holds <c>CarRental.slnx</c>.</summary>
    public static string PathOf(params string[] segments) => Path.Combine([Root.Value, .. segments]);

    private static string FindRoot()
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
}
