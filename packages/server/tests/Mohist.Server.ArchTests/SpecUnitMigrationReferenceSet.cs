using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace Mohist.Server.ArchTests;

internal static class SpecUnitMigrationReferenceSet
{
    private static readonly HashSet<string> ExcludedAssemblies = new(StringComparer.Ordinal)
    {
        "Mohist.Server.ArchTests",
        "Mohist.Server.SpecTests",
        "Mohist.Server.UnitTests",
        "Mohist.Server.TestSupport",
    };

    private static readonly Lazy<ReferenceSetOwner> Owner = new(CreateOwner, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static IReadOnlyList<MetadataReference> CreateCompilationReferences() => Owner.Value.References;

    internal static int OwnedMetadataCount => Owner.Value.Modules.Count + Owner.Value.Assemblies.Count;

    private static ReferenceSetOwner CreateOwner()
    {
        var modules = new List<ModuleMetadata>();
        var assemblies = new List<AssemblyMetadata>();
        var references = LoadReferencedAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .Select(assembly => (Assembly: assembly, Name: assembly.GetName().Name))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name) && !ExcludedAssemblies.Contains(value.Name!))
            .GroupBy(value => value.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(value => value.Assembly.GetName().Version).First().Assembly)
            .Select(assembly => CreateMetadataReference(assembly, modules, assemblies))
            .Where(reference => reference is not null)
            .Cast<MetadataReference>()
            .OrderBy(reference => reference.Display, StringComparer.Ordinal)
            .ToArray();

        if (references.Length == 0)
            throw new InvalidOperationException("No in-memory assembly metadata is available for semantic inventory.");

        return new ReferenceSetOwner(references, modules, assemblies);
    }

    private static IReadOnlyList<Assembly> LoadReferencedAssemblies()
    {
        var discovered = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>(AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic));
        pending.Enqueue(Assembly.Load("CloudNative.CloudEvents"));
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            var identity = assembly.GetName().FullName;
            if (string.IsNullOrWhiteSpace(identity) || !discovered.TryAdd(identity, assembly)) continue;
            foreach (var reference in assembly.GetReferencedAssemblies()) pending.Enqueue(Assembly.Load(reference));
        }
        return discovered.Values.ToArray();
    }

    private static MetadataReference? CreateMetadataReference(
        Assembly assembly,
        ICollection<ModuleMetadata> modules,
        ICollection<AssemblyMetadata> assemblies)
    {
        unsafe
        {
            if (!assembly.TryGetRawMetadata(out var blob, out var length) || length <= 0) return null;
            var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
            var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
            modules.Add(moduleMetadata);
            assemblies.Add(assemblyMetadata);
            return assemblyMetadata.GetReference(DocumentationProvider.Default, ImmutableArray<string>.Empty,
                embedInteropTypes: false, filePath: null, display: assembly.GetName().Name);
        }
    }

    // The metadata owner lives exactly as long as the apphost; the process reclaims the bounded set atomically.
    private sealed record ReferenceSetOwner(
        IReadOnlyList<MetadataReference> References,
        IReadOnlyList<ModuleMetadata> Modules,
        IReadOnlyList<AssemblyMetadata> Assemblies);
}
