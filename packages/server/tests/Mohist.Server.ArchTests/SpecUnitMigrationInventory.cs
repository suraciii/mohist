using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mohist.Server.ArchTests;

internal sealed partial class SpecUnitMigrationInventory : IDisposable
{
    private static readonly HashSet<string> BlockingNames = new(StringComparer.Ordinal)
    {
        "WebApplicationFactory", "TestServer", "TestHost", "HostBuilder", "InProcessTestCluster", "TestCluster",
        "MohistIntegrationFixture", "MohistDbFixture", "WorkflowGrainFixture", "DbContext", "DbSet",
        "MigrationBuilder", "MigrationOperation", "DropTableOperation", "Migration", "SqliteConnection",
        "Sqlite", "SQLite", "HttpClient", "TcpClient", "Socket", "FileStream", "PhysicalFileProvider",
    };

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    internal static int ProofParallelism { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    private readonly IReadOnlyDictionary<string, SpecUnitMigrationType> _typesByFqn;
    private readonly IReadOnlyList<string> _parseDiagnostics;
    private SpecUnitMigrationCompiledDiscovery _compiledDiscovery;
    private readonly IReadOnlyDictionary<string, ArchitectureRules.EmbeddedSource> _sourcesByPath;
    private readonly IReadOnlyDictionary<string, SyntaxTree> _treesByPath;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _analysisScopes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SpecUnitMigrationType>> _typesByName;
    private readonly IReadOnlySet<string> _embeddedReferenceNames;
    private readonly bool _fullProjectScopes;
    private readonly SpecUnitMigrationReferenceSet _referenceSet;
    private readonly ConcurrentDictionary<string, Lazy<CSharpCompilation>> _scopeCompilations;
    private readonly ConcurrentDictionary<string, Lazy<SpecUnitMigrationTypeAnalysis>> _analyses;
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>> _semanticDiagnostics;
    private readonly ConcurrentDictionary<string, Lazy<SpecUnitMigrationCandidate>> _classifications;
    private bool _discoveryBound;
    private bool _disposed;

    private SpecUnitMigrationInventory(
        IReadOnlyList<SpecUnitMigrationType> types,
        IReadOnlyList<string> parseDiagnostics,
        SpecUnitMigrationSourceTreeSnapshot sourceTree,
        SpecUnitMigrationCompiledDiscovery compiledDiscovery,
        IReadOnlyDictionary<string, ArchitectureRules.EmbeddedSource> sourcesByPath,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> analysisScopes,
        bool fullProjectScopes,
        SpecUnitMigrationReferenceSet referenceSet,
        bool discoveryBound)
    {
        _typesByFqn = types.ToDictionary(type => type.Fqn, StringComparer.Ordinal);
        _parseDiagnostics = parseDiagnostics;
        _compiledDiscovery = compiledDiscovery;
        _sourcesByPath = sourcesByPath;
        _treesByPath = treesByPath;
        _analysisScopes = analysisScopes;
        _fullProjectScopes = fullProjectScopes;
        _referenceSet = referenceSet;
        _typesByName = types.GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SpecUnitMigrationType>)group.ToArray(), StringComparer.Ordinal);
        _embeddedReferenceNames = types.SelectMany(type => type.DeclaredReferenceNames).ToHashSet(StringComparer.Ordinal);
        _scopeCompilations = new ConcurrentDictionary<string, Lazy<CSharpCompilation>>(StringComparer.Ordinal);
        _analyses = new ConcurrentDictionary<string, Lazy<SpecUnitMigrationTypeAnalysis>>(StringComparer.Ordinal);
        _semanticDiagnostics = new ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>>(StringComparer.Ordinal);
        _classifications = new ConcurrentDictionary<string, Lazy<SpecUnitMigrationCandidate>>(StringComparer.Ordinal);
        _discoveryBound = discoveryBound;
        SourceTree = sourceTree;
    }

    internal SpecUnitMigrationSourceTreeSnapshot SourceTree { get; }
    internal string DiscoveryBindingIdentity => _compiledDiscovery.BindingIdentity;
    internal int AnalyzedTypeCount => _analyses.Values.Count(value => value.IsValueCreated);
    internal int DiscoveredTypeCount => _compiledDiscovery.Fqns.Count;
    internal int CacheEntryCount => _scopeCompilations.Count + _analyses.Count
        + _semanticDiagnostics.Count + _classifications.Count;
    internal int ReferenceMetadataCount => _referenceSet.OwnedMetadataCount;
    internal int ReferenceLeaseCount => _referenceSet.LeaseCount;
    internal bool ReferencesDisposed => _referenceSet.OwnerDisposed;

    internal IReadOnlyList<string> Diagnostics
    {
        get
        {
            PrimeProofs();
            return _parseDiagnostics.Concat(_semanticDiagnostics.Values.Where(value => value.IsValueCreated)
                    .SelectMany(value => value.Value))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
    }

    internal IReadOnlyList<string> CurrentSpecFqns
        => CurrentSpecTypes().Select(type => type.Fqn).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<SpecUnitMigrationCandidate> CurrentStaticLightSpecs
        => CurrentSpecClassifications.Where(candidate => candidate.Blockers.Count == 0)
            .OrderBy(candidate => candidate.Fqn, StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<SpecUnitMigrationCandidate> CurrentSpecClassifications
        => CurrentSpecTypes().Select(Classify).OrderBy(candidate => candidate.Fqn, StringComparer.Ordinal).ToArray();

    internal bool TryGetCurrentSpecClassification(string fqn, out SpecUnitMigrationCandidate candidate)
    {
        if (_typesByFqn.TryGetValue(fqn, out var type) && type.IsCurrentSpec && type.Name.EndsWith("Specs", StringComparison.Ordinal))
        {
            candidate = Classify(type);
            return true;
        }

        candidate = null!;
        return false;
    }

    internal void PrimeProductionProofs()
        => PrimeProofs();

    private void PrimeProofs()
    {
        ThrowIfDisposed();
        var roots = CurrentSpecTypes().Concat(_analysisScopes.Keys
                .Select(fqn => _typesByFqn.GetValueOrDefault(fqn)).Where(type => type is not null).Cast<SpecUnitMigrationType>())
            .DistinctBy(type => type.Fqn, StringComparer.Ordinal).ToArray();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = ProofParallelism };
        Parallel.ForEach(roots, parallelOptions, root => _ = Classify(root));
    }

    internal static SpecUnitMigrationInventory Create(
        IEnumerable<ArchitectureRules.EmbeddedSource> sources,
        SpecUnitMigrationCompiledDiscovery? compiledDiscovery = null)
    {
        var sourceList = sources.Where(IsTestSource).OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
        var trees = ParseTrees(sourceList);
        var treesByPath = trees.ToDictionary(tree => tree.FilePath, StringComparer.Ordinal);
        return CreateSnapshot(sourceList, treesByPath, compiledDiscovery ?? SpecUnitMigrationCompiledDiscovery.Empty,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), fullProjectScopes: true,
            referenceSet: new SpecUnitMigrationReferenceSet(), discoveryBound: compiledDiscovery is not null);
    }

    internal static SpecUnitMigrationInventory CreateScoped(
        IEnumerable<ArchitectureRules.EmbeddedSource> sources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> analysisScopes,
        SpecUnitMigrationCompiledDiscovery? compiledDiscovery = null)
    {
        var sourceList = sources.Where(IsTestSource).OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
        var trees = ParseTrees(sourceList);
        return CreateSnapshot(sourceList, trees.ToDictionary(tree => tree.FilePath, StringComparer.Ordinal),
            compiledDiscovery ?? SpecUnitMigrationCompiledDiscovery.Empty, analysisScopes, fullProjectScopes: false,
            referenceSet: new SpecUnitMigrationReferenceSet(), discoveryBound: compiledDiscovery is not null);
    }

    internal SpecUnitMigrationInventory BindCompiledDiscovery(SpecUnitMigrationCompiledDiscovery compiledDiscovery)
    {
        ThrowIfDisposed();
        if (_discoveryBound || !_classifications.IsEmpty)
            throw new InvalidOperationException("compiled discovery must be bound exactly once before classification");
        _compiledDiscovery = compiledDiscovery;
        _discoveryBound = true;
        return this;
    }

    internal SpecUnitMigrationInventory BindSourceDiscovery()
        => BindCompiledDiscovery(SpecUnitMigrationCompiledDiscovery.ForSource(
            _typesByFqn.Values.Where(type => type.IsCurrentSpec || type.HasTestMethods)
                .Select(type => (type.Fqn, type.DiscoverTests(CompilationFor(type)))), SourceTree.Digest));

    internal SpecUnitMigrationInventory WithSourceContent(string path, string content)
    {
        ThrowIfDisposed();
        if (!_sourcesByPath.TryGetValue(path, out _))
            throw new ArgumentException($"source path is not in the live inventory: {path}", nameof(path));
        if (!_treesByPath.ContainsKey(path))
            throw new InvalidOperationException($"source path is not backed by a live syntax tree: {path}");

        var mutatedSource = new ArchitectureRules.EmbeddedSource(path, content, Encoding.UTF8.GetByteCount(content));
        var sources = _sourcesByPath.Values.Select(source => source.Path == path ? mutatedSource : source)
            .OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
        var mutatedTree = CSharpSyntaxTree.ParseText(content, path: path, options: ParseOptions);
        var treesByPath = _treesByPath.ToDictionary(entry => entry.Key,
            entry => entry.Key == path ? (SyntaxTree)mutatedTree : entry.Value, StringComparer.Ordinal);
        var mutated = CreateSnapshot(sources, treesByPath, SpecUnitMigrationCompiledDiscovery.Empty,
            _analysisScopes, _fullProjectScopes, _referenceSet.Lease(), discoveryBound: false);
        try
        {
            return mutated.BindSourceDiscovery();
        }
        catch
        {
            mutated.Dispose();
            throw;
        }
    }

    private static SpecUnitMigrationInventory CreateSnapshot(
        IReadOnlyList<ArchitectureRules.EmbeddedSource> sources,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath,
        SpecUnitMigrationCompiledDiscovery compiledDiscovery,
        IReadOnlyDictionary<string, IReadOnlyList<string>> analysisScopes,
        bool fullProjectScopes,
        SpecUnitMigrationReferenceSet referenceSet,
        bool discoveryBound)
    {
        var diagnostics = treesByPath.Values.SelectMany(tree => tree.GetDiagnostics())
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(FormatParseDiagnostic).Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var types = BuildTypes(sources, treesByPath);
        var sourceTree = SpecUnitMigrationSourceTree.Capture(sources, SpecUnitMigrationLedgerValidator.ValidationHead,
            SpecUnitMigrationLedgerValidator.ValidationTree);
        return new SpecUnitMigrationInventory(types, diagnostics, sourceTree, compiledDiscovery,
            sources.ToDictionary(source => source.Path, StringComparer.Ordinal), treesByPath, analysisScopes,
            fullProjectScopes, referenceSet, discoveryBound);
    }

    private static IReadOnlyList<SpecUnitMigrationType> BuildTypes(
        IReadOnlyList<ArchitectureRules.EmbeddedSource> sources,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath)
    {
        var types = new Dictionary<string, SpecUnitMigrationType>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var tree = treesByPath[source.Path];
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var fqn = SyntaxFqn(declaration);
                if (string.IsNullOrWhiteSpace(fqn)) continue;
                if (!types.TryGetValue(fqn, out var type))
                {
                    type = new SpecUnitMigrationType(fqn, declaration.Identifier.ValueText, source.Path,
                        IsCurrentSpecPath(source.Path));
                    types.Add(fqn, type);
                }
                type.AddDeclaration(source.Path, declaration, source.Content);
            }
        }

        Parallel.ForEach(types.Values, new ParallelOptions { MaxDegreeOfParallelism = ProofParallelism },
            type => type.Complete());
        return types.Values.ToArray();
    }

    private static SyntaxTree[] ParseTrees(IReadOnlyList<ArchitectureRules.EmbeddedSource> sources)
        => sources.AsParallel().AsOrdered().WithDegreeOfParallelism(ProofParallelism)
            .Select(source => (SyntaxTree)CSharpSyntaxTree.ParseText(source.Content, path: source.Path, options: ParseOptions))
            .ToArray();

    private SpecUnitMigrationCandidate Classify(SpecUnitMigrationType root)
        => _classifications.GetOrAdd(root.Fqn, _ => new Lazy<SpecUnitMigrationCandidate>(
            () => ClassifyCore(root), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private SpecUnitMigrationCandidate ClassifyCore(SpecUnitMigrationType root)
    {
        var closure = new List<SpecUnitMigrationType>();
        var pending = new Stack<SpecUnitMigrationType>([root]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<string>();
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!visited.Add(type.Fqn)) continue;
            closure.Add(type);
            var analysis = Analyze(root, type);
            foreach (var referenceFqn in analysis.ReferenceFqns)
            {
                if (!_typesByFqn.TryGetValue(referenceFqn, out var reference)) continue;
                edges.Add($"{type.Fqn}->{reference.Fqn}");
                pending.Push(reference);
            }
        }

        var blockers = closure.SelectMany(type => Analyze(root, type).Blockers.Select(blocker => $"{type.Fqn}: {blocker}"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var type in closure)
            foreach (var diagnostic in SemanticDiagnostics(root, type))
                blockers.Add($"{type.Fqn}: source diagnostics: {diagnostic}");
        var closureNames = closure.Select(type => type.Fqn).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var distinctEdges = edges.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var facts = _compiledDiscovery.ForType(root.Fqn);
        if (facts.Missing) blockers.Add($"{root.Fqn}: compiled MTP discovery unavailable");
        var closureDigest = Digest(
        [
            $"path={root.PrimaryPath}", $"fqn={root.Fqn}", $"mtp-count={facts.CaseCount}",
            $"mtp-digest={facts.CaseIdentityDigest}",
            .. closure.Select(type => $"source-content={type.PrimaryPath}|{type.SourceContentDigest}"),
            .. closureNames.Select(value => $"symbol={value}"),
            .. distinctEdges.Select(value => $"edge={value}"),
        ]);
        var sourceContentDigest = Digest(closure.Select(type => $"{type.PrimaryPath}|{type.SourceContentDigest}"));
        return new SpecUnitMigrationCandidate(root.Fqn, root.PrimaryPath, facts.FactMethods, facts.TheoryMethods,
            facts.InlineDataRows, facts.CaseCount, facts.CaseIdentityDigest, closureDigest, sourceContentDigest,
            facts.CaseIdentities ?? [],
            new ReadOnlyCollection<string>(closureNames),
            new ReadOnlyCollection<string>(blockers.OrderBy(value => value, StringComparer.Ordinal).ToArray()),
            Digest(distinctEdges));
    }

    private SpecUnitMigrationTypeAnalysis Analyze(SpecUnitMigrationType root, SpecUnitMigrationType type)
        => _analyses.GetOrAdd(CacheKey(root, type), _ => new Lazy<SpecUnitMigrationTypeAnalysis>(
            () => type.RequiresSemanticAnalysis(this)
                ? type.Analyze(this, CompilationFor(root))
                : type.DirectAnalysis(),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private IReadOnlyList<string> SemanticDiagnostics(SpecUnitMigrationType root, SpecUnitMigrationType type)
        => _semanticDiagnostics.GetOrAdd(CacheKey(root, type), _ => new Lazy<IReadOnlyList<string>>(
            () => type.ReadSemanticDiagnostics(CompilationFor(root)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    internal bool RequiresSemanticBinding(string name)
        => _embeddedReferenceNames.Contains(name) || (name.Length > 0 && char.IsUpper(name[0]));

    private IEnumerable<SpecUnitMigrationType> CurrentSpecTypes()
        => _typesByFqn.Values.Where(type => type.IsCurrentSpec && type.Name.EndsWith("Specs", StringComparison.Ordinal));

    internal bool TryGetCandidate(string fqn, out SpecUnitMigrationCandidate candidate)
    {
        if (_typesByFqn.TryGetValue(fqn, out var type))
        {
            candidate = Classify(type);
            return true;
        }

        candidate = null!;
        return false;
    }

    internal bool TryGetExecutable(string fqn, string path, out SpecUnitMigrationExecutableFacts executable)
    {
        if (_typesByFqn.TryGetValue(fqn, out var type) && type.HasPath(path))
        {
            var candidate = Classify(type);
            executable = new SpecUnitMigrationExecutableFacts(fqn, path, candidate.ExecutableCaseCount,
                candidate.ExecutableCaseIdentityDigest, candidate.ClosureIdentityDigest, candidate.SourceContentDigest,
                candidate.EdgesDigest, candidate.ExecutableCaseIdentities);
            return true;
        }

        executable = null!;
        return false;
    }

    internal static string Digest(IEnumerable<string> values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values.OrderBy(value => value, StringComparer.Ordinal))))).ToLowerInvariant();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scopeCompilations.Clear();
        _analyses.Clear();
        _semanticDiagnostics.Clear();
        _classifications.Clear();
        _referenceSet.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
