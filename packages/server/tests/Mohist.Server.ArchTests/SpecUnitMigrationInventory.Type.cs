using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Sdk;

namespace Mohist.Server.ArchTests;

internal sealed partial class SpecUnitMigrationInventory
{
    internal sealed class SpecUnitMigrationType
    {
        private readonly List<(string Path, ClassDeclarationSyntax Declaration)> _declarations = [];
        private readonly Dictionary<string, string> _sourceContentDigests = new(StringComparer.Ordinal);

        internal SpecUnitMigrationType(string fqn, string name, string path, bool isCurrentSpec)
        {
            Fqn = fqn;
            Name = name;
            PrimaryPath = path;
            IsCurrentSpec = isCurrentSpec;
        }

        internal string Fqn { get; }
        internal string Name { get; }
        internal string PrimaryPath { get; private set; }
        internal bool IsCurrentSpec { get; }
        internal string SourceContentDigest => Digest(_sourceContentDigests.Select(entry => $"{entry.Key}|{entry.Value}"));
        internal IReadOnlyList<string> DirectBlockers { get; private set; } = [];
        internal IReadOnlyList<string> ReferencedTypeNames { get; private set; } = [];
        internal IReadOnlyList<string> DeclaredReferenceNames { get; private set; } = [];
        internal IReadOnlyList<string> Paths => _declarations.Select(value => value.Path).Distinct(StringComparer.Ordinal).ToArray();
        internal bool HasTestMethods => _declarations.SelectMany(value => value.Declaration.Members)
            .OfType<MethodDeclarationSyntax>().Any(method => method.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attribute => NameIs(attribute, "Fact") || NameIs(attribute, "Theory")));

        internal void AddDeclaration(string path, ClassDeclarationSyntax declaration, string content)
        {
            _declarations.Add((path, declaration));
            _sourceContentDigests[path] = Digest([content]);
            if (string.CompareOrdinal(path, PrimaryPath) < 0) PrimaryPath = path;
        }

        internal void Complete()
        {
            var blockers = new HashSet<string>(StringComparer.Ordinal);
            var referencedTypeNames = new HashSet<string>(StringComparer.Ordinal);
            var declaredReferenceNames = new HashSet<string>(StringComparer.Ordinal) { Name };
            foreach (var (path, declaration) in _declarations)
            {
                foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                    declaredReferenceNames.Add(method.Identifier.ValueText);
                foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
                    declaredReferenceNames.Add(property.Identifier.ValueText);
                foreach (var constructor in declaration.Members.OfType<ConstructorDeclarationSyntax>())
                    declaredReferenceNames.Add(constructor.Identifier.ValueText);
                foreach (var eventDeclaration in declaration.Members.OfType<EventDeclarationSyntax>())
                    declaredReferenceNames.Add(eventDeclaration.Identifier.ValueText);
                foreach (var field in declaration.Members.OfType<BaseFieldDeclarationSyntax>())
                    foreach (var variable in field.Declaration.Variables)
                        declaredReferenceNames.Add(variable.Identifier.ValueText);
                foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    referencedTypeNames.Add(identifier.Identifier.ValueText);
                    if (BlockingNames.Contains(identifier.Identifier.ValueText))
                        blockers.Add($"external boundary symbol {identifier.Identifier.ValueText}");
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
                if (path.StartsWith("Mohist.Server.SpecTests/Support/", StringComparison.Ordinal))
                    blockers.Add("SpecTests-only support");
            }
            DirectBlockers = blockers.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            ReferencedTypeNames = referencedTypeNames.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            DeclaredReferenceNames = declaredReferenceNames.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        internal SpecUnitMigrationTypeAnalysis Analyze(
            SpecUnitMigrationInventory inventory,
            CSharpCompilation compilation)
        {
            var references = new HashSet<string>(StringComparer.Ordinal);
            var blockers = new HashSet<string>(DirectBlockers, StringComparer.Ordinal);
            foreach (var (path, declaration) in _declarations)
            {
                var model = compilation.GetSemanticModel(declaration.SyntaxTree, ignoreAccessibility: true);
                foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    var name = identifier.Identifier.ValueText;
                    if (name == "var" || !inventory.RequiresSemanticBinding(name)) continue;
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

                    var referencedFqn = EmbeddedContainingTypeFqn(resolvedSymbol, inventory);
                    if (referencedFqn is not null && referencedFqn != Fqn) references.Add(referencedFqn);
                }
            }

            return new SpecUnitMigrationTypeAnalysis(
                references.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                blockers.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }

        internal bool RequiresSemanticAnalysis(SpecUnitMigrationInventory inventory)
            => _declarations.SelectMany(value => value.Declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
                .Any(identifier => identifier.Identifier.ValueText is var name
                    && name != "var" && inventory.RequiresSemanticBinding(name));

        internal SpecUnitMigrationTypeAnalysis DirectAnalysis() => new([], DirectBlockers);

        internal SpecUnitMigrationMtpFacts DiscoverTests(CSharpCompilation compilation)
        {
            var identities = new List<string>();
            var factMethods = 0;
            var theoryMethods = 0;
            var inlineDataRows = 0;
            var incomplete = false;
            foreach (var (_, declaration) in _declarations)
            {
                var model = compilation.GetSemanticModel(declaration.SyntaxTree, ignoreAccessibility: true);
                foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    var methodSymbol = model.GetDeclaredSymbol(method);
                    if (methodSymbol is null)
                    {
                        if (method.AttributeLists.Count > 0) incomplete = true;
                        continue;
                    }

                    var attributes = methodSymbol.GetAttributes();
                    if (attributes.Any(attribute => AttributeNameIs(attribute, "Fact")))
                    {
                        factMethods++;
                        identities.Add(string.Join("|", Fqn, methodSymbol.Name, $"{Fqn}.{methodSymbol.Name}"));
                    }

                    if (!attributes.Any(attribute => AttributeNameIs(attribute, "Theory"))) continue;
                    theoryMethods++;
                    var dataAttributes = attributes.Where(attribute => IsDataAttribute(attribute.AttributeClass)).ToArray();
                    if (dataAttributes.Length == 0 || dataAttributes.Any(attribute => !AttributeNameIs(attribute, "InlineData")))
                    {
                        incomplete = true;
                        continue;
                    }

                    foreach (var inlineData in dataAttributes)
                    {
                        if (!TrySourceArguments(inlineData, methodSymbol, out var arguments))
                        {
                            incomplete = true;
                            continue;
                        }
                        inlineDataRows++;
                        identities.Add(string.Join("|", Fqn, methodSymbol.Name,
                            $"{Fqn}.{methodSymbol.Name}({string.Join(", ", arguments)})"));
                    }
                }
            }

            var distinctIdentities = identities.OrderBy(value => value, StringComparer.Ordinal)
                .GroupBy(value => value, StringComparer.Ordinal)
                .SelectMany(group => group.Select((_, occurrence) => $"{group.Key}|occurrence={occurrence + 1}"))
                .ToArray();
            return new SpecUnitMigrationMtpFacts(factMethods, theoryMethods, inlineDataRows,
                distinctIdentities.Length, Digest(distinctIdentities),
                incomplete || factMethods + theoryMethods == 0, distinctIdentities);
        }

        internal IReadOnlyList<string> ReadSemanticDiagnostics(CSharpCompilation compilation)
        {
            var diagnostics = new List<string>();
            foreach (var (_, declaration) in _declarations)
            {
                var model = compilation.GetSemanticModel(declaration.SyntaxTree, ignoreAccessibility: true);
                foreach (var diagnostic in model.GetDiagnostics(declaration.FullSpan)
                             .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    var value = FormatDiagnostic(diagnostic);
                    if (diagnostic.Id == "CS0411")
                    {
                        var node = declaration.SyntaxTree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
                        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                        if (invocation?.Expression is MemberAccessExpressionSyntax memberAccess)
                        {
                            var receiver = model.GetTypeInfo(memberAccess.Expression).Type?.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat) ?? "<unbound>";
                            var candidates = model.GetSymbolInfo(invocation).CandidateSymbols
                                .Select(SymbolDisplay).OrderBy(candidate => candidate, StringComparer.Ordinal);
                            value += $" [receiver={receiver}; candidates={string.Join(", ", candidates)}]";
                        }
                    }
                    diagnostics.Add(value);
                }
            }

            return diagnostics.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        internal bool HasPath(string path) => _declarations.Any(declaration => declaration.Path == path);

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

        private static string? EmbeddedContainingTypeFqn(ISymbol symbol, SpecUnitMigrationInventory inventory)
        {
            var type = symbol switch
            {
                INamedTypeSymbol namedType => namedType,
                _ => symbol.ContainingType,
            };
            if (type is null) return null;
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
            return inventory._typesByFqn.ContainsKey(fqn) ? fqn : null;
        }

        private static string SymbolDisplay(ISymbol symbol)
            => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static bool TrySourceArguments(
            AttributeData attribute,
            IMethodSymbol method,
            out string[] arguments)
        {
            arguments = [];
            if (attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array
                || attribute.ConstructorArguments[0].Values.Length != method.Parameters.Length)
                return false;

            var formatted = new string[method.Parameters.Length];
            for (var index = 0; index < formatted.Length; index++)
            {
                if (!TrySourceValue(attribute.ConstructorArguments[0].Values[index], out var value)) return false;
                formatted[index] = $"{method.Parameters[index].Name}: {value}";
            }
            arguments = formatted;
            return true;
        }

        private static bool TrySourceValue(TypedConstant constant, out string value)
        {
            value = "";
            if (constant.Kind == TypedConstantKind.Error || constant.Type?.TypeKind == TypeKind.Enum) return false;
            if (constant.Kind == TypedConstantKind.Array)
            {
                var values = new string[constant.Values.Length];
                for (var index = 0; index < values.Length; index++)
                    if (!TrySourceValue(constant.Values[index], out values[index])) return false;
                value = $"[{string.Join(", ", values)}]";
                return true;
            }
            if (constant.Kind == TypedConstantKind.Type) return false;
            value = ArgumentFormatter.Format(constant.Value, 1);
            return true;
        }

        private static bool AttributeNameIs(AttributeData attribute, string expected)
            => attribute.AttributeClass?.Name is var name && (name == expected || name == expected + "Attribute");

        private static bool IsDataAttribute(INamedTypeSymbol? type)
        {
            for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
                if (candidate.Name == "DataAttribute"
                    && candidate.ContainingNamespace.ToDisplayString().StartsWith("Xunit", StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    internal sealed record SpecUnitMigrationTypeAnalysis(
        IReadOnlyList<string> ReferenceFqns,
        IReadOnlyList<string> Blockers);

}
