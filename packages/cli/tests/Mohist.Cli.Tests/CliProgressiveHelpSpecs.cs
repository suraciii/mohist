using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliProgressiveHelpSpecs
{
    [Fact]
    public async Task RootHelp_ListsEveryVisibleAreaExactlyOnceInItsCapability()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        AssertCapabilityContains(text, "Work", "workspace");
        AssertCapabilityContains(text, "Operations", "audit", "github", "slack");

        var expected = new Dictionary<string, string[]>
        {
            ["Work"] = ["epic", "issue", "label", "project", "repo", "workspace"],
            ["Automation"] = ["activity", "agent", "routing", "run", "session", "webhook", "workflow"],
            ["Operations"] = ["audit", "auth", "event", "github", "notification", "otel", "runner", "server", "service", "slack"],
            ["Tools"] = ["help", "info", "install", "skill", "update"],
        };

        foreach (var (capability, names) in expected)
        {
            var section = CapabilitySection(text, capability);
            foreach (var name in names)
                Assert.Equal(1, section.Count(line => line.StartsWith($"  {name}", StringComparison.Ordinal)));
        }

        Assert.DoesNotContain("workspace repo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("approve", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--project", text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task WorkspaceHelp_ListsOnlyItsDirectChildrenWithExplicitPresentations()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["workspace", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Manage named workspaces in the active Project", text, StringComparison.Ordinal);
        Assert.Contains("  list", text, StringComparison.Ordinal);
        Assert.Contains("  view", text, StringComparison.Ordinal);
        Assert.Contains("  create", text, StringComparison.Ordinal);
        Assert.Contains("  close", text, StringComparison.Ordinal);
        Assert.Contains("  repo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("add", text, StringComparison.Ordinal);
        Assert.DoesNotContain("remove", text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task NestedGroupHelp_UsesCompletePathAndPresentsRecentChildren()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var sessionExit = await MohistCliCommands.RunAsync(http, ["session", "schedule", "--help"], output, error, fs, executor);

        Assert.Equal(0, sessionExit);
        var sessionHelp = output.ToString();
        Assert.Contains("Manage scheduled inputs for an AgentSession", sessionHelp, StringComparison.Ordinal);
        Assert.Contains("mo session schedule [<action>] [<resource>] [flags]", sessionHelp, StringComparison.Ordinal);
        Assert.Contains("  create", sessionHelp, StringComparison.Ordinal);
        Assert.Contains("  list", sessionHelp, StringComparison.Ordinal);
        Assert.Contains("  cancel", sessionHelp, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);

        output.GetStringBuilder().Clear();
        var agentExit = await MohistCliCommands.RunAsync(http, ["agent", "--help"], output, error, fs, executor);

        Assert.Equal(0, agentExit);
        var agentHelp = output.ToString();
        Assert.Contains("  spawn", agentHelp, StringComparison.Ordinal);
        Assert.Contains("  subscription", agentHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("Spawn an allowed child AgentSession from a parent session", agentHelp, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task LeafHelp_UsesExactPathAndDescriptorBackedJsonFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var workspaceExit = await MohistCliCommands.RunAsync(
            http, ["workspace", "repo", "add", "--help"], output, error, fs, executor);

        Assert.Equal(0, workspaceExit);
        var workspaceHelp = output.ToString();
        Assert.Contains("mo workspace repo add <name> <repo> [flags]", workspaceHelp, StringComparison.Ordinal);
        Assert.Contains("Workspace name", workspaceHelp, StringComparison.Ordinal);
        Assert.Contains("Repository name", workspaceHelp, StringComparison.Ordinal);
        Assert.Contains("--project", workspaceHelp, StringComparison.Ordinal);
        Assert.Contains("JSON FIELDS", workspaceHelp, StringComparison.Ordinal);
        Assert.Contains("boundSessionCount", workspaceHelp, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace repo remove", workspaceHelp, StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        var otelExit = await MohistCliCommands.RunAsync(http, ["otel", "traces", "--help"], output, error, fs, executor);

        Assert.Equal(0, otelExit);
        var otelHelp = output.ToString();
        Assert.Contains("mo otel traces [flags]", otelHelp, StringComparison.Ordinal);
        Assert.Contains("--service", otelHelp, StringComparison.Ordinal);
        Assert.Contains("--limit", otelHelp, StringComparison.Ordinal);
        Assert.Contains("trace_id, service_name, start_time, end_time, span_count", otelHelp, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task HiddenOptionsRemainAbsentFromLeafHelp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["agent", "create", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("--runtime", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--agent-config", text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task HelpAtEveryScope_IsLocalAndSideEffectFreeWithoutProject()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new InvalidOperationException("help must not contact the Server"),
            activeProjectId: null);

        foreach (var args in new[]
        {
            new[] { "--help" },
            new[] { "session", "schedule", "--help" },
            new[] { "workspace", "repo", "add", "--help" },
        })
        {
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);
            Assert.Equal(0, exitCode);
            Assert.NotEmpty(output.ToString());
            Assert.Empty(error.ToString());
        }

        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public async Task UnknownNestedAction_RendersNearestUsageAndHasNoSideEffect()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new InvalidOperationException("usage must not contact the Server"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "schedule", "missing"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("missing", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo session schedule [flags]", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("mo session schedule --help", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
        Assert.Empty(executor.Invocations);
    }

    [Fact]
    public void Renderers_RequireExplicitPresentationAndDoNotUseRegistrationFallback()
    {
        var root = new System.CommandLine.RootCommand();
        root.Subcommands.Add(new System.CommandLine.Command("uncovered", "registration fallback"));
        Assert.Throws<InvalidOperationException>(() =>
            CommandHelpRenderer.RenderRoot(new StringWriter(), root));

        var group = new System.CommandLine.Command("group");
        group.Subcommands.Add(new System.CommandLine.Command("uncovered", "registration fallback"));
        CommandPresentationCatalog.Attach(group, new CommandPresentation(CommandCapability.Work, "A covered group"));
        Assert.Throws<InvalidOperationException>(() =>
            CommandHelpRenderer.RenderGroup(new StringWriter(), group, ["group"]));

        var leaf = new System.CommandLine.Command("leaf", "registration fallback");
        Assert.Throws<InvalidOperationException>(() =>
            CommandHelpRenderer.RenderLeaf(new StringWriter(), leaf, ["leaf"]));
    }

    [Fact]
    public void GroupHelp_OmitsHiddenChildren()
    {
        var group = new System.CommandLine.Command("group");
        var visible = new System.CommandLine.Command("visible");
        var hidden = new System.CommandLine.Command("hidden") { Hidden = true };
        group.Subcommands.Add(visible);
        group.Subcommands.Add(hidden);
        CommandPresentationCatalog.Attach(group, new CommandPresentation(CommandCapability.Work, "A covered group"));
        CommandPresentationCatalog.Attach(visible, new CommandPresentation(CommandCapability.Work, "A visible child"));

        var output = new StringWriter();
        CommandHelpRenderer.RenderGroup(output, group, ["group"]);

        Assert.Contains("visible", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepresentativeNonHelpCommandExecution_RemainsOperational()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(
            _ => RecordingHttpHandler.Json(new { success = true, data = Array.Empty<object>() }));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["workspace", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_abc/workspaces", request.RequestUri?.PathAndQuery);
        Assert.Empty(executor.Invocations);
    }

    private static void AssertCapabilityContains(string text, string capability, params string[] names)
    {
        var section = CapabilitySection(text, capability);
        foreach (var name in names)
            Assert.Contains(section, line => line.StartsWith($"  {name}", StringComparison.Ordinal));
    }

    private static string[] CapabilitySection(string text, string capability)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, line => string.Equals(line.Trim(), capability, StringComparison.Ordinal));
        Assert.True(start >= 0, $"Capability '{capability}' was not rendered.");
        var end = lines.Length;
        for (var index = start + 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                end = index;
                break;
            }
        }
        return lines[(start + 1)..end];
    }
}
