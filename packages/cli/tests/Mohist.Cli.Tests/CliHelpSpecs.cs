using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliHelpSpecs
{
    [Fact]
    public async Task RootHelp_IsCapabilityIndexWithoutLeafOptions()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Work", text);
        Assert.Contains("Automation", text);
        Assert.Contains("Operations", text);
        Assert.Contains("Tools", text);
        Assert.Contains("run", text);
        Assert.DoesNotContain("--issue", text);
        Assert.DoesNotContain("approve", text);
        Assert.Empty(handler.Requests);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("output", "field selection")]
    [InlineData("environment", "MO_PROJECTS_DIR")]
    [InlineData("exit-codes", "usage failure")]
    public async Task SharedTopic_IsLocal(string topic, string expected)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["help", topic], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(expected, output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task UnknownTopic_UsesHelpUsageOnStderr()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["help", "unknown"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage: mo help <output|environment|exit-codes>", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(new[] { "workflow", "--help" }, "Project-scoped Workflow Profiles", "mo run --help")]
    [InlineData(new[] { "run", "--help" }, "WorkflowRuns", "--issue <number>")]
    public async Task GroupHelp_StatesBoundary(string[] args, string boundary, string detail)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(boundary, output.ToString());
        Assert.Contains(detail, output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(new[] { "agent", "model", "--help" }, "mo agent model [<action>] [<resource>] [flags]")]
    [InlineData(new[] { "project", "workflow", "prompt", "--help" }, "mo project workflow prompt [<action>] [<resource>] [flags]")]
    public async Task NestedGroupHelp_UsesTheCompleteInvocationPath(string[] args, string usage)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains(usage, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/api/", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    public static IEnumerable<object[]> RetiredWorkflowReadCases()
    {
        yield return [new[] { "issue", "workflow" }];
        yield return [new[] { "issue", "workflow", "status", "42" }];
        yield return [new[] { "issue", "workflow", "timeline", "42" }];
        yield return [new[] { "run", "timeline", "wr_abc123" }];
    }

    [Theory]
    [MemberData(nameof(RetiredWorkflowReadCases))]
    public async Task RetiredWorkflowReads_AreUnknownWithoutHttp(string[] args)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Empty(output.ToString());
        Assert.Contains("Unrecognized command", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunHelp_ListsViewAndExcludesTimeline()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["run", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("view", text, StringComparison.Ordinal);
        Assert.DoesNotContain("timeline", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueHelp_ExcludesWorkflowAndRetainsProfileSelectionOnWrites()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var issueExit = await MohistCliCommands.RunAsync(http, ["issue", "--help"], output, error, fs, executor);
        Assert.Equal(0, issueExit);
        Assert.DoesNotContain("workflow", output.ToString(), StringComparison.OrdinalIgnoreCase);

        output.GetStringBuilder().Clear();
        var createExit = await MohistCliCommands.RunAsync(http, ["issue", "create", "--help"], output, error, fs, executor);
        Assert.Equal(0, createExit);
        Assert.Contains("--workflow-profile", output.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        var editExit = await MohistCliCommands.RunAsync(http, ["issue", "edit", "--help"], output, error, fs, executor);
        Assert.Equal(0, editExit);
        Assert.Contains("--inherit-workflow-profile", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LeafHelp_DoesNotMarkOptionalValueOptionsAsRequired()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["label", "edit", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("--description       New description of when to use this label (required)", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--project           Project name or id (required)", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NestedAgentJobHelp_ExcludesImplementationDetails()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["agent", "job", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("List AgentJobs for an Agent profile", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("GET", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/api/", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueViewHelp_ListsRuntimeJsonFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["issue", "view", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("mo issue view <number> [flags]", text, StringComparison.Ordinal);
        Assert.Contains("JSON FIELDS", text);
        Assert.Contains("number", text);
        Assert.Contains("workflowRunId", text);
        Assert.Contains("--json", text);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("edit")]
    public async Task IssueWriteHelp_ListsRuntimeJsonFields(string action)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["issue", action, "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("JSON FIELDS", text, StringComparison.Ordinal);
        Assert.Contains("number", text, StringComparison.Ordinal);
        Assert.Contains("workflowRunId", text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueArchiveHelp_ListsRuntimeJsonFieldsForBothInvocationForms()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["issue", "archive", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("target issue:", text, StringComparison.Ordinal);
        Assert.Contains("number", text, StringComparison.Ordinal);
        Assert.Contains("--all-completed:", text, StringComparison.Ordinal);
        Assert.Contains("archived", text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(new[] { "workflow", "list", "--help" }, "displayName")]
    [InlineData(new[] { "skill", "list", "--help" }, "description")]
    public async Task OutputSelectionHelp_ListsRuntimeJsonFields(string[] args, string expectedField)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("JSON FIELDS", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(expectedField, output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(new[] { "issue", "start", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "done", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "close", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "reopen", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "archive", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "prereq", "add", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "prereq", "remove", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "watch", "add", "--help" }, "workflowRunId")]
    [InlineData(new[] { "issue", "watch", "remove", "--help" }, "workflowRunId")]
    [InlineData(new[] { "run", "list", "--help" }, "issueNumber")]
    [InlineData(new[] { "run", "view", "--help" }, "currentStage")]
    [InlineData(new[] { "run", "approve", "--help" }, "status")]
    [InlineData(new[] { "run", "reject", "--help" }, "status")]
    [InlineData(new[] { "run", "retry", "--help" }, "status")]
    [InlineData(new[] { "run", "rerun", "--help" }, "status")]
    [InlineData(new[] { "run", "pause", "--help" }, "status")]
    [InlineData(new[] { "run", "resume", "--help" }, "status")]
    [InlineData(new[] { "run", "stop", "--help" }, "status")]
    [InlineData(new[] { "activity", "list", "--help" }, "provenance")]
    [InlineData(new[] { "event", "tail", "--help" }, "specversion")]
    [InlineData(new[] { "info", "--help" }, "platformNotice")]
    [InlineData(new[] { "project", "variable", "list", "--help" }, "stages")]
    [InlineData(new[] { "issue", "variable", "list", "--help" }, "stages")]
    [InlineData(new[] { "run", "variable", "list", "--help" }, "stages")]
    [InlineData(new[] { "epic", "close", "--help" }, "updatedAt")]
    public async Task DirectJsonSelectionHelp_ListsRuntimeJsonFields(string[] args, string expectedField)
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, args, output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("JSON FIELDS", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(expectedField, output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ActivityListHelp_ShowsDefaultLimit()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["activity", "list", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("--limit", text, StringComparison.Ordinal);
        Assert.Contains("(default: 100)", text, StringComparison.Ordinal);
        Assert.Contains("provenance", text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EpicEditHelp_ListsRuntimeJsonFields()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["epic", "edit", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("JSON FIELDS", text, StringComparison.Ordinal);
        Assert.Contains("number", text, StringComparison.Ordinal);
        Assert.Contains("updatedAt", text, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueEditHelp_SeparatesLongOptionNamesFromDescriptions()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["issue", "edit", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("--inherit-workflow-profile Clear the explicit Profile", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--stage-model-variants Per-stage model variant map", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidCommand_WritesScopedUsageAndDoesNotCallServer()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(http, ["run", "missing"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Contains("missing", error.ToString());
        Assert.Contains("Usage:", error.ToString());
        Assert.Contains("mo run", error.ToString());
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
    }
}
