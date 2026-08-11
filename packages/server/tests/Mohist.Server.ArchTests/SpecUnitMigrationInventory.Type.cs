using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mohist.Server.ArchTests;

internal sealed partial class SpecUnitMigrationInventory
{
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
}
