using Mohist.Cli.Tests.Compatibility;

namespace Mohist.Cli.Tests.Skills;

internal static class EmbeddedSkillData
{
    public const string VirtualRoot = "/mohist-tests/skill-data";

    public static string ReadText(string relativePath)
    {
        var resourceName = "SkillData/" + relativePath.Replace('\\', '/');
        var assembly = typeof(EmbeddedSkillData).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded skill asset '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> Paths()
    {
        const string prefix = "SkillData/";
        return typeof(EmbeddedSkillData).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static void Populate(FakeFileSystem files, string root = VirtualRoot)
    {
        files.AddDirectory(root);
        foreach (var relativePath in Paths())
        {
            var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                files.AddDirectory(directory);
            files.AddFile(target, ReadText(relativePath));
        }
    }
}
