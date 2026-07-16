using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Mohist.Server.ArchTests;

public class BannedApiAnalyzerTests
{
    [Fact]
    public async Task TestPolicy_RejectsPhysicalFilesystemApi()
    {
        const string source = """
            namespace System.IO
            {
                public static class File
                {
                    public static bool Exists(string path) => false;
                }
            }

            internal static class Candidate
            {
                public static bool Exists(string path) => System.IO.File.Exists(path);
            }
            """;
        var compilation = CSharpCompilation.Create(
            "Candidate",
            [CSharpSyntaxTree.ParseText(source)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzerOptions = new AnalyzerOptions([
            new InMemoryAdditionalText(
                "BannedSymbols.Tests.txt",
                "T:System.IO.File; Tests must use an in-memory filesystem or a domain storage fake.")
        ]);

        var diagnostics = await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new CSharpSymbolIsBannedAnalyzer()),
                analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();

        var diagnostic = Assert.Single(diagnostics, item => item.Id == "RS0030");
        Assert.Contains("File", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("in-memory filesystem", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(
            CancellationToken cancellationToken = default) =>
            Microsoft.CodeAnalysis.Text.SourceText.From(content);
    }
}
