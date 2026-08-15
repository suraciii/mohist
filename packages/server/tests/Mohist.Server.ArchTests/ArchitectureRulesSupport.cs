using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Mohist.Server.ArchTests;

internal static class ArchitectureRulesSupport
{
    internal static readonly Architecture Architecture = new ArchLoader()
        // The rule providers are scoped to Mohist.Server.*; loading the CLI
        // assembly adds startup cost without contributing architecture facts.
        .LoadNamespacesWithinAssembly(System.Reflection.Assembly.Load("Mohist.Server"), "Mohist.Server")
        .Build();

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

internal static class ArchitectureRulesWarmup
{
    [ModuleInitializer]
    internal static void Initialize() => _ = ArchitectureRulesSupport.Architecture;
}
