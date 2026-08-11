using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mohist.Server.ArchTests;

internal sealed partial class SpecUnitMigrationInventory
{
    private CSharpCompilation CompilationFor(SpecUnitMigrationType root)
    {
        var compilationKey = CompilationNameForPath(root.PrimaryPath);
        return _scopeCompilations.GetOrAdd(compilationKey, _ => new Lazy<CSharpCompilation>(() =>
        {
            var trees = _treesByPath.Values.Where(tree => InCompilation(root.PrimaryPath, tree.FilePath)).ToArray();
            return CreateCompilation(trees, CompilationNameForPath(root.PrimaryPath));
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static string CacheKey(SpecUnitMigrationType root, SpecUnitMigrationType type)
        => CompilationNameForPath(root.PrimaryPath) + "\n" + type.Fqn;

    private static bool InCompilation(string rootPath, string candidatePath)
        => rootPath.StartsWith("Mohist.Server.SpecTests/", StringComparison.Ordinal)
            ? candidatePath.StartsWith("Mohist.Server.SpecTests/", StringComparison.Ordinal)
              || candidatePath.StartsWith("Mohist.Server.TestSupport/", StringComparison.Ordinal)
              || candidatePath == "eng/TestTime.cs"
            : candidatePath.StartsWith("Mohist.Server.UnitTests/", StringComparison.Ordinal)
              || candidatePath.StartsWith("Mohist.Server.TestSupport/", StringComparison.Ordinal)
              || candidatePath == "eng/TestTime.cs";

    private CSharpCompilation CreateCompilation(
        IReadOnlyList<SyntaxTree> trees,
        string assemblyName)
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
            """, path: "GeneratedImplicitUsings.cs", options: ParseOptions);
        return CSharpCompilation.Create(assemblyName, [globalUsings, .. trees],
            _referenceSet.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
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

    private static bool IsCurrentSpecPath(string path)
        => path.StartsWith("Mohist.Server.SpecTests/Specs/", StringComparison.Ordinal);

    private static string SyntaxFqn(ClassDeclarationSyntax declaration)
    {
        var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(namespaceNode => namespaceNode.Name.ToString()).Where(value => !string.IsNullOrWhiteSpace(value));
        var containingTypes = declaration.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(type => type.Identifier.ValueText);
        return string.Join('.', namespaces.Concat(containingTypes).Append(declaration.Identifier.ValueText));
    }

    private static bool NameIs(AttributeSyntax attribute, string expected)
    {
        var name = attribute.Name.ToString();
        return name == expected || name == expected + "Attribute"
            || name.EndsWith("." + expected, StringComparison.Ordinal);
    }
}
