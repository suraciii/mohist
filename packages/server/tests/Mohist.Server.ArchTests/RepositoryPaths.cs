using System.Reflection;

namespace Mohist.Server.ArchTests;

internal static class RepositoryPaths
{
    private const string RepositoryMarkerKey = "MohistRepositoryMarker";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static string RequireDirectory(params string[] relativeSegments)
    {
        var path = Resolve(relativeSegments);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Required repository directory does not exist: {path}");
        return path;
    }

    public static string RequireFile(params string[] relativeSegments)
    {
        var path = Resolve(relativeSegments);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required repository file does not exist: {path}", path);
        return path;
    }

    private static string Resolve(IEnumerable<string> relativeSegments)
    {
        var path = RepositoryRoot;
        foreach (var segment in relativeSegments)
            path = Path.Combine(path, segment);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var marker = typeof(RepositoryPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == RepositoryMarkerKey)?.Value;

        if (string.IsNullOrWhiteSpace(marker))
            throw new InvalidOperationException($"Missing assembly metadata: {RepositoryMarkerKey}");

        var markerPath = Path.GetFullPath(marker);
        if (!File.Exists(markerPath) || Path.GetFileName(markerPath) != "Directory.Packages.props")
            throw new InvalidOperationException($"Invalid repository marker: {markerPath}");

        return Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException($"Repository marker has no parent directory: {markerPath}");
    }
}
