using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Mohist.Server.ArchTests;

/// <summary>
/// issue-511 T-004: structural contract that server production C#
/// comments do not introduce new references to issues / task
/// identifiers / design-doc paths / openspec/. The test scans
/// <c>SyntaxTrivia</c> of <see cref="SyntaxKind.SingleLineCommentTrivia"/>,
/// <see cref="SyntaxKind.MultiLineCommentTrivia"/>,
/// <see cref="SyntaxKind.SingleLineDocumentationCommentTrivia"/>, and
/// <see cref="SyntaxKind.MultiLineDocumentationCommentTrivia"/> only —
/// string literals and code are out of scope.
/// </summary>
public sealed class CommentReferenceRules
{
    private const string ServerSourcesPrefix = "ServerSources/";

    private static readonly Regex CommentReferencePattern = new(
        "issue-\\d+|T-\\d{3}|design/[^*\\s]+\\.md|openspec/",
        RegexOptions.ExplicitCapture);

    [Fact]
    public void ServerSourceComments_DoNotIntroduceNewIssueSpecDesignPathReferences_BeyondBaseline()
    {
        var sources = ReadServerSources();
        Assert.NotEmpty(sources);

        var baseline = ReadCommentReferenceBaseline();
        var currentCounts = sources.ToDictionary(
            source => source.Path,
            source => CountOffenders(source.Content),
            StringComparer.Ordinal);

        var violations = Ratchet(baseline, currentCounts);

        Assert.True(
            violations.Count == 0,
            "Server production comments must not introduce new issue/spec/design/openspec references "
            + "beyond the frozen baseline. Violations: "
            + string.Join("; ", violations));
    }

    [Fact]
    public void Ratchet_FailsWhenCurrentExceedsBaseline()
    {
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Workflow/Grains/WorkflowGrain.cs"] = 1,
        };
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Workflow/Grains/WorkflowGrain.cs"] = 2,
        };

        var violations = Ratchet(baseline, currentCounts);

        var message = Assert.Single(violations);
        Assert.Contains("grew from baseline 1 to 2", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ratchet_FailsWhenBaselineEntryIsStale()
    {
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Api/UnifiedSessionRoutes.cs"] = 2,
        };
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Api/UnifiedSessionRoutes.cs"] = 0,
        };

        var violations = Ratchet(baseline, currentCounts);

        var message = Assert.Single(violations);
        Assert.Contains("must be removed from the baseline", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ratchet_FailsWhenOffenderFileIsMissingFromBaseline()
    {
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal);
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Api/UnifiedSessionRoutes.cs"] = 1,
        };

        var violations = Ratchet(baseline, currentCounts);

        var message = Assert.Single(violations);
        Assert.Contains("no baseline entry", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ratchet_FailsWhenBaselineEntryPointsAtMissingFile()
    {
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Workflow/Grains/MissingGrain.cs"] = 2,
        };
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var violations = Ratchet(baseline, currentCounts);

        var message = Assert.Single(violations);
        Assert.Contains("source file is missing", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ratchet_PassesWhenCurrentCountEqualsBaseline()
    {
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Workflow/Grains/WorkflowGrain.cs"] = 4,
        };
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Workflow/Grains/WorkflowGrain.cs"] = 4,
        };

        var violations = Ratchet(baseline, currentCounts);

        Assert.Empty(violations);
    }

    [Fact]
    public void Ratchet_FailsWhenCurrentCountShrinksBelowBaselineWithoutBaselineUpdate()
    {
        var baseline = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Workflow/Grains/WorkflowGrain.cs"] = 4,
        };
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Workflow/Grains/WorkflowGrain.cs"] = 2,
        };

        var violations = Ratchet(baseline, currentCounts);

        var message = Assert.Single(violations);
        Assert.Contains("shrunk from baseline 4 to 2", message, StringComparison.Ordinal);
        Assert.Contains("must be updated", message, StringComparison.Ordinal);
    }

    internal static IReadOnlyList<string> Ratchet(
        IReadOnlyDictionary<string, int> baseline,
        IReadOnlyDictionary<string, int> currentCounts)
    {
        var violations = new List<string>();

        foreach (var (path, currentCount) in currentCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (currentCount == 0) continue;

            if (!baseline.TryGetValue(path, out var baselineCount))
            {
                violations.Add(
                    $"{path} has {currentCount} comment-reference offender(s) and no baseline entry");
                continue;
            }

            if (currentCount > baselineCount)
            {
                violations.Add(
                    $"{path} grew from baseline {baselineCount} to {currentCount} comment-reference offender(s)");
            }
        }

        foreach (var (path, baselineCount) in baseline.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!currentCounts.TryGetValue(path, out var currentCount))
            {
                violations.Add($"{path} is in the baseline but its source file is missing");
                continue;
            }

            if (currentCount == 0)
            {
                violations.Add(
                    $"{path} has 0 comment-reference offender(s) and must be removed from the baseline");
                continue;
            }

            if (currentCount < baselineCount)
            {
                violations.Add(
                    $"{path} shrunk from baseline {baselineCount} to {currentCount} comment-reference offender(s) "
                    + "and the baseline count must be updated");
            }
        }

        return violations;
    }

    private static int CountOffenders(string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var count = 0;

        foreach (var trivia in tree.GetRoot().DescendantTrivia())
        {
            if (!IsCommentTrivia(trivia.Kind())) continue;
            count += CommentReferencePattern.Matches(trivia.ToString()).Count;
        }

        return count;
    }

    private static bool IsCommentTrivia(SyntaxKind kind) =>
        kind == SyntaxKind.SingleLineCommentTrivia
        || kind == SyntaxKind.MultiLineCommentTrivia
        || kind == SyntaxKind.SingleLineDocumentationCommentTrivia
        || kind == SyntaxKind.MultiLineDocumentationCommentTrivia;

    private static IReadOnlyList<EmbeddedSource> ReadServerSources()
    {
        var assembly = typeof(CommentReferenceRules).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ServerSourcesPrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return new EmbeddedSource(name[ServerSourcesPrefix.Length..], reader.ReadToEnd());
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, int> ReadCommentReferenceBaseline()
    {
        const string resourceName = "CommentReferenceBaseline.json";
        var assembly = typeof(CommentReferenceRules).Assembly;
        Assert.Contains(resourceName, assembly.GetManifestResourceNames());

        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        var baseline = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(stream);
        Assert.NotNull(baseline);
        return baseline;
    }

    private sealed record EmbeddedSource(string Path, string Content);
}
