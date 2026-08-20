using System.Reflection;
using System.Reflection.Metadata;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Mohist.Server.ArchTests;

internal static class SpecUnitMigrationReferenceSet
{
    private static readonly object MetadataGate = new();
    private static readonly List<ModuleMetadata> Metadata = [];
    private static readonly List<AssemblyMetadata> Assemblies = [];
    private static readonly Lazy<IReadOnlyList<MetadataReference>> CompilationReferences =
        new(CreateCompilationReferencesCore);
    private static readonly HashSet<string> ExcludedAssemblies = new(StringComparer.Ordinal)
    {
        "Mohist.Server.ArchTests",
        "Mohist.Server.SpecTests",
        "Mohist.Server.UnitTests",
        "Mohist.Server.TestSupport",
    };

    internal static IReadOnlyList<MetadataReference> CreateCompilationReferences()
        => CompilationReferences.Value;

    private static IReadOnlyList<MetadataReference> CreateCompilationReferencesCore()
    {
        var references = LoadReferencedAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .Select(assembly => (Assembly: assembly, Name: assembly.GetName().Name))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name) && !ExcludedAssemblies.Contains(value.Name!))
            .GroupBy(value => value.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(value => value.Assembly.GetName().Version).First().Assembly)
            .Select(CreateMetadataReference)
            .Where(reference => reference is not null)
            .Cast<MetadataReference>()
            .OrderBy(reference => reference.Display, StringComparer.Ordinal)
            .ToArray();

        if (references.Length == 0)
            throw new InvalidOperationException("No in-memory assembly metadata is available for semantic inventory.");

        return references;
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
            if (string.IsNullOrWhiteSpace(identity) || !discovered.TryAdd(identity, assembly))
                continue;

            foreach (var reference in assembly.GetReferencedAssemblies())
                pending.Enqueue(Assembly.Load(reference));
        }

        return discovered.Values.ToArray();
    }

    private static MetadataReference? CreateMetadataReference(Assembly assembly)
    {
        unsafe
        {
            if (!assembly.TryGetRawMetadata(out var blob, out var length) || length <= 0)
                return null;

            var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
            var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
            lock (MetadataGate)
            {
                Metadata.Add(moduleMetadata);
                Assemblies.Add(assemblyMetadata);
            }
            return assemblyMetadata.GetReference(DocumentationProvider.Default, ImmutableArray<string>.Empty,
                embedInteropTypes: false, filePath: null, display: assembly.GetName().Name);
        }
    }
}
