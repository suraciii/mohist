using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;

namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationReferenceSet : IDisposable
{
    private static readonly HashSet<string> ExcludedAssemblies = new(StringComparer.Ordinal)
    {
        "Mohist.Server.ArchTests",
        "Mohist.Server.SpecTests",
        "Mohist.Server.UnitTests",
        "Mohist.Server.TestSupport",
    };

    private readonly ReferenceSetOwner _owner;
    private bool _disposed;

    internal SpecUnitMigrationReferenceSet()
        => _owner = new ReferenceSetOwner();

    private SpecUnitMigrationReferenceSet(ReferenceSetOwner owner)
    {
        _owner = owner;
        _owner.AddLease();
    }

    internal SpecUnitMigrationReferenceSet Lease()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new SpecUnitMigrationReferenceSet(_owner);
    }

    internal IReadOnlyList<MetadataReference> References
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _owner.References;
        }
    }

    internal int OwnedMetadataCount => _owner.OwnedMetadataCount;
    internal int LeaseCount => _owner.LeaseCount;
    internal bool OwnerDisposed => _owner.IsDisposed;
    internal bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Release();
    }

    private static ReferenceData CreateReferences()
    {
        var metadata = new List<AssemblyMetadata>();
        var references = RequiredAssemblies().Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Where(assembly => !assembly.IsDynamic)
            .Select(assembly => (Assembly: assembly, Name: assembly.GetName().Name))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name) && !ExcludedAssemblies.Contains(value.Name!))
            .GroupBy(value => value.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(value => value.Assembly.GetName().Version).First().Assembly)
            .Select(assembly => CreateMetadataReference(assembly, metadata))
            .Where(reference => reference is not null)
            .Cast<MetadataReference>()
            .OrderBy(reference => reference.Display, StringComparer.Ordinal)
            .ToArray();

        if (references.Length == 0)
            throw new InvalidOperationException("No in-memory assembly metadata is available for semantic inventory.");

        return new ReferenceData(references, metadata);
    }

    private static IEnumerable<Assembly> RequiredAssemblies()
    {
        yield return typeof(object).Assembly;
        yield return typeof(Enumerable).Assembly;
        yield return typeof(System.Diagnostics.Process).Assembly;
        yield return typeof(System.Net.Http.HttpClient).Assembly;
        yield return typeof(System.Net.Http.Json.JsonContent).Assembly;
        yield return typeof(System.Security.Claims.ClaimsPrincipal).Assembly;
        yield return typeof(System.Text.Json.JsonSerializer).Assembly;
        yield return typeof(System.Threading.Channels.Channel).Assembly;
        yield return typeof(EnvironmentAbstractions.IEnvironmentVariableProvider).Assembly;
        yield return typeof(EnvironmentAbstractions.TestHelpers.MockEnvironmentVariableProvider).Assembly;
        yield return typeof(Microsoft.AspNetCore.Hosting.IWebHostBuilder).Assembly;
        yield return typeof(Microsoft.AspNetCore.Http.DefaultHttpContext).Assembly;
        yield return typeof(Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature).Assembly;
        yield return typeof(Microsoft.AspNetCore.Http.Features.IFeatureCollection).Assembly;
        yield return typeof(Microsoft.AspNetCore.Http.Json.JsonOptions).Assembly;
        yield return typeof(Microsoft.AspNetCore.SignalR.Hub).Assembly;
        yield return typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly;
        yield return typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly;
        yield return typeof(Microsoft.Extensions.DependencyInjection.ServiceCollection).Assembly;
        yield return typeof(Microsoft.Extensions.Hosting.IHost).Assembly;
        yield return typeof(Microsoft.Extensions.Time.Testing.FakeTimeProvider).Assembly;
        yield return typeof(Orleans.IGrain).Assembly;
        yield return typeof(Orleans.TestingHost.InProcessTestCluster).Assembly;
        yield return typeof(Xunit.FactAttribute).Assembly;
    }

    private static MetadataReference? CreateMetadataReference(
        Assembly assembly,
        ICollection<AssemblyMetadata> metadata)
    {
        unsafe
        {
            if (!assembly.TryGetRawMetadata(out var blob, out var length) || length <= 0) return null;
            var assemblyMetadata = AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((IntPtr)blob, length));
            metadata.Add(assemblyMetadata);
            return assemblyMetadata.GetReference(DocumentationProvider.Default, ImmutableArray<string>.Empty,
                embedInteropTypes: false, filePath: null, display: assembly.GetName().Name);
        }
    }

    private sealed record ReferenceData(
        IReadOnlyList<MetadataReference> References,
        IReadOnlyList<AssemblyMetadata> Metadata) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in Metadata) item.Dispose();
        }
    }

    private sealed class ReferenceSetOwner
    {
        private readonly Lazy<ReferenceData> _data = new(CreateReferences, LazyThreadSafetyMode.ExecutionAndPublication);
        private int _leases = 1;
        private bool _disposed;

        internal IReadOnlyList<MetadataReference> References
        {
            get
            {
                lock (this)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                }
                return _data.Value.References;
            }
        }

        internal int OwnedMetadataCount => _data.IsValueCreated ? _data.Value.Metadata.Count : 0;
        internal int LeaseCount { get { lock (this) return _leases; } }
        internal bool IsDisposed { get { lock (this) return _disposed; } }

        internal void AddLease()
        {
            lock (this)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _leases++;
            }
        }

        internal void Release()
        {
            ReferenceData? data = null;
            lock (this)
            {
                if (_disposed) return;
                if (--_leases > 0) return;
                _disposed = true;
                if (_data.IsValueCreated) data = _data.Value;
            }
            data?.Dispose();
        }
    }
}
