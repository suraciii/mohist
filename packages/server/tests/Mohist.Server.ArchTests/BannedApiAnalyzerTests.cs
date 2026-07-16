using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.BannedApiAnalyzers;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Mohist.Server.ArchTests;

public class BannedApiAnalyzerTests
{
    public static TheoryData<string, string> RejectedTestApis => new()
    {
        {
            """
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
            """,
            "in-memory filesystem"
        },
        {
            """
            namespace System.Net.Http
            {
                public class HttpClientHandler
                {
                    public HttpClientHandler() { }
                }
            }

            internal static class Candidate
            {
                public static System.Net.Http.HttpClientHandler Create() =>
                    new System.Net.Http.HttpClientHandler();
            }
            """,
            "HttpMessageHandler"
        },
        {
            """
            namespace System
            {
                public abstract class TimeProvider
                {
                    public static TimeProvider System => null!;
                }
            }

            internal static class Candidate
            {
                public static object Clock => System.TimeProvider.System;
            }
            """,
            "FakeTimeProvider"
        },
        {
            """
            namespace System
            {
                public struct TimeSpan { }
            }

            namespace System.Threading
            {
                public class CancellationTokenSource
                {
                    public CancellationTokenSource(System.TimeSpan timeout) { }
                }
            }

            internal static class Candidate
            {
                public static object Create(System.TimeSpan timeout) =>
                    new System.Threading.CancellationTokenSource(timeout);
            }
            """,
            "FakeTimeProvider"
        },
    };

    [Theory]
    [MemberData(nameof(RejectedTestApis))]
    public async Task CheckedInTestPolicy_RejectsForbiddenApi(string source, string expectedMessage)
    {
        var diagnostics = await AnalyzeAsync(source);

        Assert.Contains(diagnostics, item =>
            item.Id == "RS0030"
            && item.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Candidate",
            [CSharpSyntaxTree.ParseText(source)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzerOptions = new AnalyzerOptions([
            EmbeddedPolicy("BannedSymbols.Product.txt"),
            EmbeddedPolicy("BannedSymbols.Tests.txt"),
        ]);

        return await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new CSharpSymbolIsBannedAnalyzer()),
                analyzerOptions)
            .GetAnalyzerDiagnosticsAsync();
    }

    private static AdditionalText EmbeddedPolicy(string resourceName)
    {
        var assembly = typeof(BannedApiAnalyzerTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded policy '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return new InMemoryAdditionalText(resourceName, reader.ReadToEnd());
    }

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;

        public override Microsoft.CodeAnalysis.Text.SourceText GetText(
            CancellationToken cancellationToken = default) =>
            Microsoft.CodeAnalysis.Text.SourceText.From(content);
    }
}
