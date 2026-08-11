using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mohist.Server.ArchTests;

internal static class SpecUnitMigrationSyntheticProof
{
    internal sealed record Source(string Path, string Fqn, string Content);
    internal sealed record Edge(string From, string To);
    internal sealed record Discovery(
        string SourceDigest,
        int FactMethods,
        int TheoryMethods,
        int InlineDataRows,
        bool Complete,
        ImmutableArray<string> CaseIdentities);
    internal sealed record Bound(SpecUnitMigrationCandidate Candidate, SpecUnitMigrationExecutableFacts Executable);

    internal sealed record Snapshot(
        Source Root,
        ImmutableArray<Source> Sources,
        ImmutableArray<Edge> Edges,
        ImmutableArray<string> Diagnostics,
        string SourceDigest)
    {
        internal Discovery CompleteDiscovery(params string[] caseIdentities)
            => new(SourceDigest, 1, 0, 0, true, [.. caseIdentities]);

        internal Bound Bind(Discovery discovery, params string[] explicitBlockers)
        {
            var blockers = Diagnostics.Select(diagnostic => $"source diagnostics: {diagnostic}")
                .Concat(explicitBlockers).ToHashSet(StringComparer.Ordinal);
            if (!discovery.Complete)
                blockers.Add("compiled discovery incomplete");
            if (discovery.SourceDigest != SourceDigest)
                blockers.Add($"compiled discovery/source snapshot mismatch; discovery={discovery.SourceDigest}, source={SourceDigest}");

            var symbols = Sources.Select(source => source.Fqn).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
            var edgeIdentities = Edges.Select(edge => $"{edge.From}->{edge.To}")
                .OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
            var caseIdentities = discovery.CaseIdentities.OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
            var caseDigest = Digest(caseIdentities);
            var edgesDigest = Digest(edgeIdentities);
            var closureDigest = Digest([
                $"path={Root.Path}", $"fqn={Root.Fqn}", $"mtp-count={caseIdentities.Length}",
                $"mtp-digest={caseDigest}", $"source-digest={SourceDigest}",
                .. symbols.Select(value => $"symbol={value}"),
                .. edgeIdentities.Select(value => $"edge={value}"),
            ]);
            var candidate = new SpecUnitMigrationCandidate(
                Root.Fqn, Root.Path, discovery.FactMethods, discovery.TheoryMethods, discovery.InlineDataRows,
                caseIdentities.Length, caseDigest, closureDigest, SourceDigest, caseIdentities, symbols,
                blockers.OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray(), edgesDigest);
            var executable = new SpecUnitMigrationExecutableFacts(
                Root.Fqn, Root.Path, candidate.ExecutableCaseCount, candidate.ExecutableCaseIdentityDigest,
                candidate.ClosureIdentityDigest, candidate.SourceContentDigest, candidate.EdgesDigest,
                candidate.ExecutableCaseIdentities);
            return new Bound(candidate, executable);
        }
    }

    internal static Snapshot Compile(Source root, IEnumerable<Source>? dependencies = null, IEnumerable<Edge>? edges = null)
    {
        var sources = new[] { root }.Concat(dependencies ?? []).OrderBy(source => source.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        if (sources.Select(source => source.Path).Distinct(StringComparer.Ordinal).Count() != sources.Length
            || sources.Select(source => source.Fqn).Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new ArgumentException("Synthetic source paths and FQNs must be unique.");

        var closure = sources.Select(source => source.Fqn).ToHashSet(StringComparer.Ordinal);
        var immutableEdges = (edges ?? []).OrderBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal).ToImmutableArray();
        if (immutableEdges.Any(edge => !closure.Contains(edge.From) || !closure.Contains(edge.To)))
            throw new ArgumentException("Synthetic closure edges must stay inside the complete source set.");

        var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.Content,
            new CSharpParseOptions(LanguageVersion.Preview), source.Path)).ToImmutableArray<SyntaxTree>();
        var diagnostics = CompileDiagnostics(trees);
        var sourceDigest = Digest(sources.Select(source =>
            $"{source.Path}|{source.Fqn}|{Digest([source.Content])}"));
        return new Snapshot(root, sources, immutableEdges, diagnostics, sourceDigest);
    }

    private static unsafe ImmutableArray<string> CompileDiagnostics(ImmutableArray<SyntaxTree> trees)
    {
        var assembly = typeof(object).Assembly;
        if (!assembly.TryGetRawMetadata(out var blob, out var length) || length <= 0)
            throw new InvalidOperationException("Core runtime metadata is unavailable for the synthetic proof.");

        var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
        using var metadata = AssemblyMetadata.Create(module);
        var reference = metadata.GetReference(filePath: null, display: assembly.GetName().Name);
        var compilation = CSharpCompilation.Create("SpecUnitMigrationSyntheticProof", trees, [reference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        return compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic =>
            {
                var location = diagnostic.Location.GetLineSpan();
                return $"SEMANTIC|{diagnostic.Id}|{location.Path}:{location.StartLinePosition.Line + 1}: {diagnostic.GetMessage()}";
            })
            .OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
    }

    private static string Digest(IEnumerable<string> values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            values.OrderBy(value => value, StringComparer.Ordinal))))).ToLowerInvariant();
}
