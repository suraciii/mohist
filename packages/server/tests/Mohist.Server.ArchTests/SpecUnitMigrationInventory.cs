using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Sdk;
using Xunit.v3;

namespace Mohist.Server.ArchTests;

internal sealed class SpecUnitMigrationInventory
{
    private static readonly HashSet<string> BlockingNames = new(StringComparer.Ordinal)
    {
        "WebApplicationFactory", "TestServer", "TestHost", "HostBuilder", "InProcessTestCluster", "TestCluster",
        "MohistIntegrationFixture", "MohistDbFixture", "WorkflowGrainFixture", "DbContext", "DbSet",
        "MigrationBuilder", "MigrationOperation", "DropTableOperation", "Migration", "SqliteConnection",
        "Sqlite", "SQLite", "HttpClient", "TcpClient", "Socket", "FileStream", "PhysicalFileProvider",
    };

    private readonly IReadOnlyDictionary<string, SpecUnitMigrationType> _typesByFqn;
    private readonly IReadOnlyList<string> _diagnostics;
    private readonly SpecUnitMigrationCompiledDiscovery _compiledDiscovery;
    private readonly IReadOnlyDictionary<string, SyntaxTree> _treesByPath;
    private readonly IReadOnlyDictionary<string, CSharpCompilation> _compilations;
    internal SpecUnitMigrationSourceTreeSnapshot SourceTree { get; }

    private SpecUnitMigrationInventory(IReadOnlyList<SpecUnitMigrationType> types, IReadOnlyList<string> diagnostics,
        SpecUnitMigrationSourceTreeSnapshot sourceTree, SpecUnitMigrationCompiledDiscovery compiledDiscovery,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath,
        IReadOnlyDictionary<string, CSharpCompilation> compilations)
    {
        _typesByFqn = types.ToDictionary(type => type.Fqn, StringComparer.Ordinal);
        _diagnostics = diagnostics;
        _compiledDiscovery = compiledDiscovery;
        _treesByPath = treesByPath;
        _compilations = compilations;
        SourceTree = sourceTree;
    }

    internal IReadOnlyList<string> Diagnostics => _diagnostics;

    internal IReadOnlyList<SpecUnitMigrationCandidate> CurrentStaticLightSpecs
        => CurrentSpecClassifications.Where(candidate => candidate.Blockers.Count == 0)
            .OrderBy(candidate => candidate.Fqn, StringComparer.Ordinal).ToArray();

    internal IReadOnlyList<SpecUnitMigrationCandidate> CurrentSpecClassifications
        => _typesByFqn.Values.Where(type => type.IsCurrentSpec && type.Name.EndsWith("Specs", StringComparison.Ordinal))
            .Select(Classify).OrderBy(candidate => candidate.Fqn, StringComparer.Ordinal).ToArray();

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

    internal static SpecUnitMigrationInventory Create(
        IEnumerable<ArchitectureRules.EmbeddedSource> sources,
        SpecUnitMigrationCompiledDiscovery? compiledDiscovery = null)
    {
        var sourceList = sources.Where(IsTestSource).ToArray();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = sourceList.Select(source => CSharpSyntaxTree.ParseText(source.Content, path: source.Path, options: parseOptions)).ToArray();
        var parseDiagnostics = trees.SelectMany(tree => tree.GetDiagnostics())
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => $"PARSE|{diagnostic.Location.SourceTree?.FilePath}: {diagnostic.GetMessage()}")
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var specTrees = trees.Where(tree => tree.FilePath.StartsWith("Mohist.Server.SpecTests/", StringComparison.Ordinal)
            || tree.FilePath.StartsWith("Mohist.Server.TestSupport/", StringComparison.Ordinal)
            || tree.FilePath == "eng/TestTime.cs").ToArray();
        var unitTrees = trees.Where(tree => tree.FilePath.StartsWith("Mohist.Server.UnitTests/", StringComparison.Ordinal)
            || tree.FilePath.StartsWith("Mohist.Server.TestSupport/", StringComparison.Ordinal)
            || tree.FilePath == "eng/TestTime.cs").ToArray();
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal)
        {
            ["Mohist.Server.SpecTests"] = CreateCompilation(specTrees, "Mohist.Server.SpecTests"),
            ["Mohist.Server.UnitTests"] = CreateCompilation(unitTrees, "Mohist.Server.UnitTests"),
        };
        var semanticDiagnostics = compilations.Values.SelectMany(compilation => compilation.GetDiagnostics())
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => $"SEMANTIC|{diagnostic.Location.SourceTree?.FilePath ?? "<compilation>"}: {diagnostic.GetMessage()}")
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var diagnostics = parseDiagnostics.Concat(semanticDiagnostics).Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var types = new Dictionary<string, SpecUnitMigrationType>(StringComparer.Ordinal);
        foreach (var source in sourceList)
        {
            var compilation = source.Path.StartsWith("Mohist.Server.SpecTests/", StringComparison.Ordinal)
                ? compilations["Mohist.Server.SpecTests"] : compilations["Mohist.Server.UnitTests"];
            var tree = trees.Single(candidate => candidate.FilePath == source.Path);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var symbol = compilation.GetSemanticModel(tree, ignoreAccessibility: true).GetDeclaredSymbol(declaration);
                var fqn = symbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal)
                    ?? SyntaxFqn(declaration);
                if (string.IsNullOrWhiteSpace(fqn)) continue;
                if (!types.TryGetValue(fqn, out var type))
                {
                    type = new SpecUnitMigrationType(fqn, declaration.Identifier.ValueText, source.Path,
                        IsCurrentSpecPath(source.Path), diagnostics);
                    types.Add(fqn, type);
                }
                type.AddDeclaration(source.Path, declaration, compilation, source.Content);
            }
        }

        var sourceTree = SpecUnitMigrationSourceTree.Capture(sourceList, SpecUnitMigrationLedgerValidator.ValidationHead,
            SpecUnitMigrationLedgerValidator.ValidationTree);
        var inventory = new SpecUnitMigrationInventory(types.Values.ToArray(), diagnostics, sourceTree,
            compiledDiscovery ?? SpecUnitMigrationCompiledDiscovery.Empty,
            trees.ToDictionary(tree => tree.FilePath, StringComparer.Ordinal), compilations);
        foreach (var type in types.Values) type.ResolveReferences(inventory);
        return inventory;
    }

    internal SpecUnitMigrationInventory WithSourceContent(string path, string content)
    {
        var originalType = _typesByFqn.Values.SingleOrDefault(type => type.HasPath(path));
        if (originalType is null)
            throw new ArgumentException($"source path is not in the live inventory: {path}", nameof(path));

        if (!_treesByPath.TryGetValue(path, out var originalTree))
            throw new InvalidOperationException($"source path is not backed by a live syntax tree: {path}");

        var compilationName = CompilationNameForPath(path);
        var originalCompilation = _compilations[compilationName];
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var mutatedTree = CSharpSyntaxTree.ParseText(content, path: path, options: parseOptions);
        var mutatedCompilation = originalCompilation.ReplaceSyntaxTree(originalTree, mutatedTree);
        var semanticModel = mutatedCompilation.GetSemanticModel(mutatedTree, ignoreAccessibility: true);
        var mutatedDeclarations = mutatedTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(declaration => semanticModel.GetDeclaredSymbol(declaration)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "", StringComparison.Ordinal) == originalType.Fqn)
            .ToArray();
        if (mutatedDeclarations.Length == 0)
            throw new InvalidOperationException($"source mutation removed the live type {originalType.Fqn} from {path}");

        var liveParseDiagnostics = mutatedTree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(FormatParseDiagnostic);
        var liveSemanticDiagnostics = semanticModel.GetDiagnostics(mutatedTree.GetRoot().FullSpan)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(FormatDiagnostic);
        var liveDiagnostics = liveParseDiagnostics.Concat(liveSemanticDiagnostics)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var diagnostics = _diagnostics.Where(diagnostic => DiagnosticPath(diagnostic) != path)
            .Concat(liveDiagnostics)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var mutatedType = originalType.CloneWithSourceContent(path, mutatedDeclarations, mutatedCompilation, content, diagnostics);
        var types = _typesByFqn.Values.Select(type => ReferenceEquals(type, originalType) ? mutatedType : type).ToArray();
        var inventory = new SpecUnitMigrationInventory(types, diagnostics, SourceTree, _compiledDiscovery,
            _treesByPath.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal), _compilations);
        mutatedType.ResolveReferences(inventory);
        return inventory;
    }

    private SpecUnitMigrationCandidate Classify(SpecUnitMigrationType root)
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
            foreach (var reference in type.ResolvedReferences)
            {
                edges.Add($"{type.Fqn}->{reference.Fqn}");
                pending.Push(reference);
            }
        }

        var blockers = closure.SelectMany(type => type.Blockers.Select(blocker => $"{type.Fqn}: {blocker}"))
            .ToHashSet(StringComparer.Ordinal);
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
            new ReadOnlyCollection<string>(closureNames), new ReadOnlyCollection<string>(blockers.OrderBy(value => value, StringComparer.Ordinal).ToArray()),
            Digest(distinctEdges));
    }

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

    private static CSharpCompilation CreateCompilation(IReadOnlyList<SyntaxTree> trees, string assemblyName)
    {
        var globalUsings = CSharpSyntaxTree.ParseText("""
            global using global::System;
            global using global::System.Collections.Generic;
            global using global::System.IO;
            global using global::System.Linq;
            global using global::System.Net.Http;
            global using global::System.Net.Http.Json;
            global using global::System.Threading;
            global using global::System.Threading.Tasks;
            global using global::Orleans;
            global using global::Orleans.Hosting;
            global using global::Orleans.Runtime;
            """, path: "GeneratedImplicitUsings.cs", options: new CSharpParseOptions(LanguageVersion.Preview));
        return CSharpCompilation.Create(assemblyName, [globalUsings, .. trees], SpecUnitMigrationReferenceSet.CreateCompilationReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static string CompilationNameForPath(string path)
        => path.StartsWith("Mohist.Server.SpecTests/", StringComparison.Ordinal)
            ? "Mohist.Server.SpecTests"
            : "Mohist.Server.UnitTests";

    private static string FormatDiagnostic(Diagnostic diagnostic)
        => $"SEMANTIC|{diagnostic.Location.SourceTree?.FilePath ?? "<compilation>"}: {diagnostic.GetMessage()}";

    private static string FormatParseDiagnostic(Diagnostic diagnostic)
        => $"PARSE|{diagnostic.Location.SourceTree?.FilePath ?? "<compilation>"}: {diagnostic.GetMessage()}";

    private static string? DiagnosticPath(string diagnostic)
    {
        var separator = diagnostic.IndexOf('|');
        var value = separator < 0 ? diagnostic : diagnostic[(separator + 1)..];
        var colon = value.IndexOf(':');
        return colon < 0 ? null : value[..colon];
    }

    private static bool IsTestSource(ArchitectureRules.EmbeddedSource source)
        => source.Path.StartsWith("Mohist.Server.SpecTests/", StringComparison.Ordinal)
        || source.Path.StartsWith("Mohist.Server.UnitTests/", StringComparison.Ordinal)
        || source.Path.StartsWith("Mohist.Server.TestSupport/", StringComparison.Ordinal)
        || source.Path == "eng/TestTime.cs";

    private static bool IsCurrentSpecPath(string path) => path.StartsWith("Mohist.Server.SpecTests/Specs/", StringComparison.Ordinal);

    private static string SyntaxFqn(ClassDeclarationSyntax declaration)
    {
        var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(namespaceNode => namespaceNode.Name.ToString()).Where(value => !string.IsNullOrWhiteSpace(value));
        var containingTypes = declaration.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(type => type.Identifier.ValueText);
        return string.Join('.', namespaces.Concat(containingTypes).Append(declaration.Identifier.ValueText));
    }

    internal sealed class SpecUnitMigrationType
    {
        private readonly List<(string Path, ClassDeclarationSyntax Declaration, CSharpCompilation Compilation)> _declarations = [];
        private readonly HashSet<string> _blockers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sourceContentDigests = new(StringComparer.Ordinal);

        internal SpecUnitMigrationType(string fqn, string name, string path, bool isCurrentSpec,
            IReadOnlyList<string> diagnostics)
        {
            Fqn = fqn;
            Name = name;
            PrimaryPath = path;
            IsCurrentSpec = isCurrentSpec;
            _blockers.UnionWith(diagnostics.Where(value => DiagnosticPath(value) == path)
                .Select(value => $"source diagnostics: {value}"));
        }

        internal string Fqn { get; }
        internal string Name { get; }
        internal string PrimaryPath { get; private set; }
        internal bool IsCurrentSpec { get; }
        internal string SourceContentDigest => Digest(_sourceContentDigests.Select(entry => $"{entry.Key}|{entry.Value}"));
        internal IReadOnlyList<SpecUnitMigrationType> ResolvedReferences { get; private set; } = [];
        internal IReadOnlyList<string> Blockers { get; private set; } = [];

        internal void AddDeclaration(string path, ClassDeclarationSyntax declaration, CSharpCompilation compilation, string content)
        {
            _declarations.Add((path, declaration, compilation));
            _sourceContentDigests[path] = Digest([content]);
            if (string.CompareOrdinal(path, PrimaryPath) < 0) PrimaryPath = path;
        }

        internal SpecUnitMigrationType CloneWithSourceContent(string path,
            IReadOnlyList<ClassDeclarationSyntax> declarations, CSharpCompilation compilation, string content,
            IReadOnlyList<string> diagnostics)
        {
            var clone = new SpecUnitMigrationType(Fqn, Name, PrimaryPath, IsCurrentSpec, diagnostics);
            foreach (var declaration in _declarations.Where(value => value.Path != path))
                clone.AddDeclarationDigest(declaration.Path, declaration.Declaration, declaration.Compilation,
                    _sourceContentDigests[declaration.Path]);
            foreach (var declaration in declarations)
                clone.AddDeclaration(path, declaration, compilation, content);
            return clone;
        }

        private void AddDeclarationDigest(string path, ClassDeclarationSyntax declaration,
            CSharpCompilation compilation, string digest)
        {
            _declarations.Add((path, declaration, compilation));
            _sourceContentDigests[path] = digest;
            if (string.CompareOrdinal(path, PrimaryPath) < 0) PrimaryPath = path;
        }

        internal void ResolveReferences(SpecUnitMigrationInventory inventory)
        {
            var references = new HashSet<SpecUnitMigrationType>();
            var blockers = new HashSet<string>(_blockers, StringComparer.Ordinal);
            foreach (var (path, declaration, compilation) in _declarations)
            {
                var model = compilation.GetSemanticModel(declaration.SyntaxTree, ignoreAccessibility: true);
                foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    var name = identifier.Identifier.ValueText;
                    if (name == "var") continue;
                    if (BlockingNames.Contains(name)) blockers.Add($"external boundary symbol {name}");
                    var info = GetSymbolInfo(model, identifier);
                    if (info.CandidateSymbols.Length > 1)
                    {
                        blockers.Add($"ambiguous symbol {name}: {string.Join(", ", info.CandidateSymbols.Select(SymbolDisplay))}");
                        continue;
                    }
                    var resolvedSymbol = info.Symbol ?? info.CandidateSymbols.SingleOrDefault();
                    if (resolvedSymbol is null)
                    {
                        blockers.Add($"unresolved symbol {name} at {path}:{identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                        continue;
                    }

                    var referencedType = EmbeddedContainingType(resolvedSymbol, inventory);
                    if (referencedType is not null && !ReferenceEquals(referencedType, this)) references.Add(referencedType);
                }

                if (declaration.AttributeLists.SelectMany(list => list.Attributes)
                    .Any(attribute => NameIs(attribute, "Collection") || NameIs(attribute, "CollectionDefinition")))
                    blockers.Add("collection fixture attribute");
                if (declaration.BaseList is not null)
                {
                    foreach (var baseType in declaration.BaseList.Types)
                    {
                        var baseName = baseType.Type.ToString();
                        if (baseName.Contains("IClassFixture", StringComparison.Ordinal) || baseName.Contains("ICollectionFixture", StringComparison.Ordinal))
                            blockers.Add("fixture interface");
                        if (baseName.EndsWith("Fixture", StringComparison.Ordinal) || baseName.EndsWith("Specs", StringComparison.Ordinal))
                            blockers.Add($"fixture/spec base {baseName}");
                    }
                }
                if (path.StartsWith("Mohist.Server.SpecTests/Support/", StringComparison.Ordinal)) blockers.Add("SpecTests-only support");
            }

            ResolvedReferences = references.OrderBy(reference => reference.Fqn, StringComparer.Ordinal).ToArray();
            Blockers = blockers.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static SymbolInfo GetSymbolInfo(SemanticModel model, IdentifierNameSyntax identifier)
        {
            if (identifier.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == identifier
                && memberAccess.Parent is InvocationExpressionSyntax invocation)
                return model.GetSymbolInfo(invocation);
            if (identifier.Parent is InvocationExpressionSyntax directInvocation)
                return model.GetSymbolInfo(directInvocation);
            return model.GetSymbolInfo(identifier);
        }

        internal bool HasPath(string path) => _declarations.Any(declaration => declaration.Path == path);

        private static string? DiagnosticPath(string diagnostic)
        {
            var separator = diagnostic.IndexOf('|');
            var value = separator < 0 ? diagnostic : diagnostic[(separator + 1)..];
            var colon = value.IndexOf(':');
            return colon < 0 ? null : value[..colon];
        }

        private static SpecUnitMigrationType? EmbeddedContainingType(ISymbol symbol, SpecUnitMigrationInventory inventory)
        {
            var type = symbol switch
            {
                INamedTypeSymbol namedType => namedType,
                _ => symbol.ContainingType,
            };
            if (type is null) return null;
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
            return inventory._typesByFqn.GetValueOrDefault(fqn);
        }

        private static string SymbolDisplay(ISymbol symbol)
            => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static bool NameIs(AttributeSyntax attribute, string expected)
        {
            var name = attribute.Name.ToString();
            return name == expected || name == expected + "Attribute" || name.EndsWith("." + expected, StringComparison.Ordinal);
        }
    }

    internal static string Digest(IEnumerable<string> values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values.OrderBy(value => value, StringComparer.Ordinal))))).ToLowerInvariant();
}
