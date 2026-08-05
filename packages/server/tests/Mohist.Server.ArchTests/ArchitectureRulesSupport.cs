using System.Collections.Concurrent;

namespace Mohist.Server.ArchTests;

internal static class ArchitectureRulesSupport
{
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<ArchitectureRules.EmbeddedSource>>> EmbeddedSourceCache = new();

    internal static IReadOnlyList<ArchitectureRules.EmbeddedSource> EmbeddedSources(string prefix)
        => EmbeddedSourceCache.GetOrAdd(prefix, prefix => new Lazy<IReadOnlyList<ArchitectureRules.EmbeddedSource>>(
            () => ReadEmbeddedSources(prefix))).Value;

    private static IReadOnlyList<ArchitectureRules.EmbeddedSource> ReadEmbeddedSources(string prefix)
    {
        var assembly = typeof(ArchitectureRules).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                var byteLength = checked((int)stream.Length);
                using var reader = new StreamReader(stream);
                return new ArchitectureRules.EmbeddedSource(name[prefix.Length..], reader.ReadToEnd(), byteLength);
            })
            .ToArray()
            .AsReadOnly();
    }
}
