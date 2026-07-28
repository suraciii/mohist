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
