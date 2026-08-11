using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mohist.Server.ArchTests;

internal sealed partial class SpecUnitMigrationInventory
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
    private readonly SpecUnitMigrationCompiledDiscovery _compiledDiscovery;
    private readonly IReadOnlyDictionary<string, ArchitectureRules.EmbeddedSource> _sourcesByPath;
    private readonly IReadOnlyDictionary<string, SyntaxTree> _treesByPath;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _analysisScopes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SpecUnitMigrationType>> _typesByName;
    private readonly IReadOnlySet<string> _embeddedReferenceNames;
    private readonly bool _fullProjectScopes;
    private readonly bool _allowCompiledDependencyPrefilter;
    private readonly ConcurrentDictionary<string, Lazy<CSharpCompilation>> _scopeCompilations;
    private readonly ConcurrentDictionary<string, Lazy<SpecUnitMigrationTypeAnalysis>> _analyses;
    private readonly ConcurrentDictionary<string, Lazy<SpecUnitMigrationSourceProbe>> _sourceProbes;
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>> _semanticDiagnostics;
    private readonly ConcurrentDictionary<string, Lazy<SpecUnitMigrationCandidate>> _classifications;
    private readonly ConcurrentDictionary<string, Lazy<bool>> _sourceLightness;

    private SpecUnitMigrationInventory(
        IReadOnlyList<SpecUnitMigrationType> types,
        IReadOnlyList<string> parseDiagnostics,
        SpecUnitMigrationSourceTreeSnapshot sourceTree,
        SpecUnitMigrationCompiledDiscovery compiledDiscovery,
        IReadOnlyDictionary<string, ArchitectureRules.EmbeddedSource> sourcesByPath,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> analysisScopes,
        bool fullProjectScopes,
        bool allowCompiledDependencyPrefilter,
        ConcurrentDictionary<string, Lazy<CSharpCompilation>>? scopeCompilations = null,
        ConcurrentDictionary<string, Lazy<SpecUnitMigrationTypeAnalysis>>? analyses = null,
        ConcurrentDictionary<string, Lazy<SpecUnitMigrationSourceProbe>>? sourceProbes = null,
        ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>>? semanticDiagnostics = null)
    {
        _typesByFqn = types.ToDictionary(type => type.Fqn, StringComparer.Ordinal);
        _parseDiagnostics = parseDiagnostics;
        _compiledDiscovery = compiledDiscovery;
        _sourcesByPath = sourcesByPath;
        _treesByPath = treesByPath;
        _analysisScopes = analysisScopes;
        _fullProjectScopes = fullProjectScopes;
        _allowCompiledDependencyPrefilter = allowCompiledDependencyPrefilter;
        _typesByName = types.GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<SpecUnitMigrationType>)group.ToArray(), StringComparer.Ordinal);
        _embeddedReferenceNames = types.SelectMany(type => type.DeclaredReferenceNames).ToHashSet(StringComparer.Ordinal);
        _scopeCompilations = scopeCompilations ?? new ConcurrentDictionary<string, Lazy<CSharpCompilation>>(StringComparer.Ordinal);
        _analyses = analyses ?? new ConcurrentDictionary<string, Lazy<SpecUnitMigrationTypeAnalysis>>(StringComparer.Ordinal);
        _sourceProbes = sourceProbes ?? new ConcurrentDictionary<string, Lazy<SpecUnitMigrationSourceProbe>>(StringComparer.Ordinal);
        _semanticDiagnostics = semanticDiagnostics ?? new ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>>(StringComparer.Ordinal);
        _classifications = new ConcurrentDictionary<string, Lazy<SpecUnitMigrationCandidate>>(StringComparer.Ordinal);
        _sourceLightness = new ConcurrentDictionary<string, Lazy<bool>>(StringComparer.Ordinal);
        SourceTree = sourceTree;
        DiscoveryShapeDigest = ComputeDiscoveryShapeDigest(treesByPath.Values);
    }

    private SpecUnitMigrationInventory(
        SpecUnitMigrationInventory source,
        SpecUnitMigrationCompiledDiscovery compiledDiscovery)
    {
        _typesByFqn = source._typesByFqn;
        _parseDiagnostics = source._parseDiagnostics;
        _compiledDiscovery = compiledDiscovery;
        _sourcesByPath = source._sourcesByPath;
        _treesByPath = source._treesByPath;
        _analysisScopes = source._analysisScopes;
        _typesByName = source._typesByName;
        _embeddedReferenceNames = source._embeddedReferenceNames;
        _fullProjectScopes = source._fullProjectScopes;
        _allowCompiledDependencyPrefilter = compiledDiscovery.HasRuntimeReferences;
        _scopeCompilations = source._scopeCompilations;
        _analyses = source._analyses;
        _sourceProbes = source._sourceProbes;
        _semanticDiagnostics = source._semanticDiagnostics;
        _classifications = new ConcurrentDictionary<string, Lazy<SpecUnitMigrationCandidate>>(StringComparer.Ordinal);
        _sourceLightness = new ConcurrentDictionary<string, Lazy<bool>>(StringComparer.Ordinal);
        SourceTree = source.SourceTree;
        DiscoveryShapeDigest = source.DiscoveryShapeDigest;
    }

    internal SpecUnitMigrationSourceTreeSnapshot SourceTree { get; }
    internal string DiscoveryShapeDigest { get; }
    internal int AnalyzedTypeCount => _analyses.Values.Count(value => value.IsValueCreated);
    internal int ProbedTypeCount => _sourceProbes.Values.Count(value => value.IsValueCreated);
    internal int DiscoveredTypeCount => _compiledDiscovery.Fqns.Count;

    internal IReadOnlyList<string> Diagnostics
    {
        get
        {
            if (_allowCompiledDependencyPrefilter) return _parseDiagnostics;
            foreach (var fqn in CurrentSourceLightFqns) EnsureClosureDiagnostics(fqn);
            return _parseDiagnostics.Concat(_semanticDiagnostics.Values.Where(value => value.IsValueCreated)
                    .SelectMany(value => value.Value))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
    }

    internal IReadOnlyList<string> CurrentSourceLightFqns
        => CurrentSpecTypes().Where(IsSourceLight).Select(type => type.Fqn)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<string> CurrentSpecFqns
        => CurrentSpecTypes().Select(type => type.Fqn).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<SpecUnitMigrationCandidate> CurrentStaticLightSpecs
        => CurrentSpecTypes().Where(IsSourceLight).Select(Classify)
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
    {
        if (!_allowCompiledDependencyPrefilter)
            throw new InvalidOperationException("only a fresh compiled inventory can prime production proof work");

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = ProofParallelism };
        Parallel.ForEach(CurrentSpecTypes(), parallelOptions, root => _ = IsSourceLight(root));
        var scopedRoots = _analysisScopes.Keys.Select(fqn => _typesByFqn.GetValueOrDefault(fqn))
            .Where(type => type is not null).Cast<SpecUnitMigrationType>().ToArray();
        Parallel.ForEach(scopedRoots, parallelOptions, root => _ = Classify(root));
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
            allowCompiledDependencyPrefilter: false);
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
            allowCompiledDependencyPrefilter: compiledDiscovery?.HasRuntimeReferences == true);
    }

    internal SpecUnitMigrationInventory WithCompiledDiscovery(SpecUnitMigrationCompiledDiscovery compiledDiscovery)
        => new(this, compiledDiscovery);

    internal SpecUnitMigrationInventory WithSourceContent(string path, string content)
    {
        if (!_sourcesByPath.TryGetValue(path, out var originalSource))
            throw new ArgumentException($"source path is not in the live inventory: {path}", nameof(path));
        if (!_treesByPath.ContainsKey(path))
            throw new InvalidOperationException($"source path is not backed by a live syntax tree: {path}");

        var mutatedSource = new ArchitectureRules.EmbeddedSource(path, content, Encoding.UTF8.GetByteCount(content));
        var sources = _sourcesByPath.Values.Select(source => source.Path == path ? mutatedSource : source)
            .OrderBy(source => source.Path, StringComparer.Ordinal).ToArray();
        var mutatedTree = CSharpSyntaxTree.ParseText(content, path: path, options: ParseOptions);
        var treesByPath = _treesByPath.ToDictionary(entry => entry.Key,
            entry => entry.Key == path ? (SyntaxTree)mutatedTree : entry.Value, StringComparer.Ordinal);
        var mutated = CreateSnapshot(sources, treesByPath, _compiledDiscovery, _analysisScopes, _fullProjectScopes,
            allowCompiledDependencyPrefilter: false);
        if (mutated.DiscoveryShapeDigest != DiscoveryShapeDigest)
            throw new InvalidOperationException(
                $"source mutation changes compiled discovery shape for {originalSource.Path}; fresh compiled discovery is required");
        return mutated;
    }

    private static SpecUnitMigrationInventory CreateSnapshot(
        IReadOnlyList<ArchitectureRules.EmbeddedSource> sources,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath,
        SpecUnitMigrationCompiledDiscovery compiledDiscovery,
        IReadOnlyDictionary<string, IReadOnlyList<string>> analysisScopes,
        bool fullProjectScopes,
        bool allowCompiledDependencyPrefilter)
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
            fullProjectScopes, allowCompiledDependencyPrefilter);
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
            if (_semanticDiagnostics.TryGetValue(CacheKey(root, type), out var diagnostics) && diagnostics.IsValueCreated)
                foreach (var diagnostic in diagnostics.Value) blockers.Add($"{type.Fqn}: source diagnostics: {diagnostic}");
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

    private bool IsSourceLight(SpecUnitMigrationType root)
        => _sourceLightness.GetOrAdd(root.Fqn, _ => new Lazy<bool>(
            () => IsSourceLightCore(root), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private bool IsSourceLightCore(SpecUnitMigrationType root)
    {
        if (_allowCompiledDependencyPrefilter && HasCompiledBlockingDependency(root)) return false;
        var pending = new Stack<SpecUnitMigrationType>([root]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!visited.Add(type.Fqn)) continue;
            if (type.DirectBlockers.Count > 0) return false;
            var probe = Probe(root, type);
            if (probe.Blocked) return false;
            foreach (var referenceFqn in probe.ReferenceFqns)
                if (_typesByFqn.TryGetValue(referenceFqn, out var reference)) pending.Push(reference);
        }
        return true;
    }

    private SpecUnitMigrationTypeAnalysis Analyze(SpecUnitMigrationType root, SpecUnitMigrationType type)
        => _analyses.GetOrAdd(CacheKey(root, type), _ => new Lazy<SpecUnitMigrationTypeAnalysis>(
            () => type.RequiresSemanticAnalysis(this)
                ? type.Analyze(this, CompilationFor(root))
                : type.DirectAnalysis(),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private SpecUnitMigrationSourceProbe Probe(SpecUnitMigrationType root, SpecUnitMigrationType type)
        => _sourceProbes.GetOrAdd(CacheKey(root, type), _ => new Lazy<SpecUnitMigrationSourceProbe>(
            () =>
            {
                var analysis = Analyze(root, type);
                return new SpecUnitMigrationSourceProbe(analysis.Blockers.Count > 0, analysis.ReferenceFqns);
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private IReadOnlyList<string> SemanticDiagnostics(SpecUnitMigrationType root, SpecUnitMigrationType type)
        => _semanticDiagnostics.GetOrAdd(CacheKey(root, type), _ => new Lazy<IReadOnlyList<string>>(
            () => type.ReadSemanticDiagnostics(CompilationFor(root)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private bool HasCompiledBlockingDependency(SpecUnitMigrationType root)
    {
        var pending = new Stack<SpecUnitMigrationType>([root]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!visited.Add(type.Fqn)) continue;
            if (type.DirectBlockers.Count > 0) return true;
            foreach (var referenceFqn in _compiledDiscovery.RuntimeReferencesFor(type.Fqn))
                if (_typesByFqn.TryGetValue(referenceFqn, out var reference)) pending.Push(reference);
        }
        return false;
    }

    internal bool RequiresSemanticBinding(string name)
        => _embeddedReferenceNames.Contains(name) || (name.Length > 0 && char.IsUpper(name[0]));

    private void EnsureClosureDiagnostics(string fqn)
    {
        if (!_typesByFqn.TryGetValue(fqn, out var root)) return;
        var pending = new Stack<SpecUnitMigrationType>([root]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!visited.Add(type.Fqn)) continue;
            var analysis = Analyze(root, type);
            _ = SemanticDiagnostics(root, type);
            foreach (var referenceFqn in analysis.ReferenceFqns)
                if (_typesByFqn.TryGetValue(referenceFqn, out var reference)) pending.Push(reference);
        }
    }

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
}
