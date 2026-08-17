using System.Text.RegularExpressions;
using Xunit;

namespace Mohist.Server.ArchTests;

public class ProductionContractRules
{
    [Fact]
    public void ServerSource_ExcludesTestOnlyGrainControls()
    {
        var banned = new[]
        {
            "DeactivateForTestAsync",
            "FlushForTestAsync",
            "GrainKeyForTest",
        };

        AssertSourceExcludes(banned.Select(name => (name, new Regex($@"\b{name}\b"))));
    }

    [Fact]
    public void RegisteredCollaborators_AreRequiredDependencies()
    {
        var required = new[]
        {
            "IWorkflowProfileProvider",
            "IEventPushQueue",
            "IBackgroundTaskLauncher",
            "IAgentJobStore",
            "IAgentJobDispatchObserver",
        };

        AssertSourceExcludes(required.Select(name =>
            ($"{name}?", new Regex($@"\b{name}\s*\?"))));
    }

    // The direct external Agent API answers with the strict public
    // execution allowlist only. Internal read shapes - launch results,
    // session summaries, transcripts, grain states, and persistence
    // rows - must never be referenced from the direct API sources, so
    // no route can accidentally serialize product read shapes, prompt
    // text, or storage detail through the public boundary.
    [Fact]
    public void DirectApiSources_ReferenceNoInternalReadType()
    {
        var internalReadTypes = new[]
        {
            // Launch / routed launch product shapes.
            "AgentJobLaunchRead",
            "SessionOperationRead",
            "AgentLaunchResult",
            "RoutedAgentLaunchOutcome",
            // Internal grain and ledger state.
            "AgentJobState",
            "AgentSessionState",
            // Session and transcript product read shapes.
            "AgentSessionReadModels",
            "WorkflowSessionDto",
            "AgentSessionSummaryDto",
            "AgentSessionInfoDto",
            "AgentSessionListItemDto",
            "GenericAgentSessionSummaryDto",
            "AgentSessionTranscriptResponse",
            "AgentSessionTranscriptTurnDto",
            "AgentSessionTranscriptPartDto",
            "AgentSessionTranscriptUserDto",
            "AgentSessionTranscriptRawPartDto",
            "AgentSessionTranscriptToolDto",
            // Stop-operation internals.
            "TurnControlResult",
            // Persistence rows (the projection is read through the
            // dedicated public read service, never the rows).
            "AgentJobRow",
            "AgentSessionRow",
            "AgentJobEventRow",
            "AgentSessionEventRow",
        };

        var directApiSources = ServerSources()
            .Where(source => source.Path.StartsWith("Api/DirectApi/", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            directApiSources.Length > 0,
            "The direct API sources must exist under Api/DirectApi/ for this rule to be meaningful.");

        var violations = directApiSources
            .SelectMany(source => internalReadTypes
                .Where(name => Regex.Matches(source.Content, $@"\b{name}\b").Count > 0)
                .Select(name => $"{name} in {source.Path}"))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Api/DirectApi must not reference internal read types (the direct boundary serves the strict public allowlist only): "
            + string.Join("; ", violations));
    }

    private static void AssertSourceExcludes(IEnumerable<(string Name, Regex Pattern)> rules)
    {
        var sources = ServerSources();
        var violations = rules
            .SelectMany(rule => sources
                .Where(source => rule.Pattern.IsMatch(source.Content))
                .Select(source => $"{rule.Name} in {source.Path}"))
            .OrderBy(violation => violation, StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join("; ", violations));
    }

    private static IReadOnlyList<EmbeddedSource> ServerSources()
    {
        const string prefix = "ServerSources/";
        var assembly = typeof(ProductionContractRules).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return new EmbeddedSource(name[prefix.Length..], reader.ReadToEnd());
            })
            .ToArray();
    }

    private sealed record EmbeddedSource(string Path, string Content);
}
