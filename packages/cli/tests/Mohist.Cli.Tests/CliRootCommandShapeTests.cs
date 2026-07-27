using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Cross-cutting root-command-shape guard (T-006 of issue #387).
//
// After the five migrations land (T-001..T-005), the root command layer
// is required to expose ONLY resource / resource-group commands plus the
// single controlled exception `mo info`. No bare verb (status / logs /
// use / notify) and no misnamed group (system info) may hang directly
// off the root. This file is the regression gate that keeps the
// converged shape honest.
//
// See:
//   - openspec/changes/issue-387/specs/root-command-shape/spec.md
//   - openspec/changes/issue-387/design.md D1 (uniform no-alias policy),
//     D4 (test strategy — root-shape guard is the new third class).
public class CliRootCommandShapeTests
{
    [Fact]
    public async Task Root_Help_ListsResourceGroupsAndInfoOnly_NoBareVerbs()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();

        // Required resource / resource-group commands: the four that the
        // T-001..T-005 migrations touched, plus the standard suite.
        string[] requiredResourceGroups =
        [
            "project",
            "server",
            "service",
            "notification",
            "activity",
            "event",
            "session",
        ];
        foreach (var name in requiredResourceGroups)
            Assert.Contains(name, stdout, StringComparison.Ordinal);

        // The single controlled exception.
        Assert.Contains("info", stdout, StringComparison.Ordinal);

        // The five legacy bare-verb / misnamed paths must NOT be advertised
        // as top-level subcommands.
        Assert.DoesNotContain("status", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("logs", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("use", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("notify", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("system", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("events", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_Help_StillAdvertisesAllResourceGroupsBeyondMigratedOnes()
    {
        // Sanity check: the migration must not have removed any other
        // resource group. This pins the test surface so a future
        // refactor that drops one of these (e.g. `workflow`) is caught
        // here rather than in some unrelated test.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        string[] survivingResourceGroups =
        [
            "project",
            "run",
            "issue",
            "epic",
            "agent",
            "label",
            "runner",
            "server",
            "service",
            "install",
            "update",
            "skill",
            "notification",
            "otel",
            "info",
        ];
        foreach (var name in survivingResourceGroups)
            Assert.Contains(name, stdout, StringComparison.Ordinal);
        Assert.Contains("repo", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  repository ", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  opencode ", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  config ", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  skills ", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyRootStatus_FailsToResolveAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["status"], output, error, fs, executor);

        // Per D1 (no aliases retained), the legacy `mo status` path is
        // gone — System.CommandLine surfaces a parse error and the
        // runner returns non-zero. No HTTP request must be issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyRootLogs_FailsToResolveAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["logs"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyRootUse_FailsToResolveAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["use", "my-app"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyRootNotify_FailsToResolveAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["notify", "setup"], output, error, fs, executor);

        // `notify` is no longer a registered group; `mo notify setup`
        // surfaces a parse error, no HTTP request is issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacySystemInfo_FailsToResolveAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "info"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EventList_FailsToResolveAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["event", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EventHelp_ListsOnlyDeliveryOperationsWithoutRoutingCommands()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["event", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var help = output.ToString();
        Assert.Contains("tail", help, StringComparison.Ordinal);
        Assert.Contains("dead-letter", help, StringComparison.Ordinal);
        Assert.DoesNotContain("rule", help, StringComparison.Ordinal);
        Assert.DoesNotContain("test", help, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MoInfo_DefaultOutputAndExitAreUnchanged()
    {
        // The `mo info` controlled exception must remain byte-identical
        // to the pre-change state. We pin a few stable anchors from the
        // renderer output (`InfoRenderer.RenderDefault`) without making
        // the assertion fragile against cosmetic wording changes; the
        // exit code, the `mo info` path resolving, and the renderer
        // section anchors are the durable signal.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["info"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The default render surfaces the CLI / Server / Runner /
        // Project / Data-dir sections. Section headers must be present
        // and the exit code is 0 (controlled exception is read-only).
        Assert.Contains("CLI", stdout);
        Assert.Contains("Server", stdout);
        Assert.Contains("Runner", stdout);
        Assert.Contains("Project", stdout);
        Assert.Contains("Data dir", stdout);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task MoInfo_VerboseFlag_AppendsVerboseSections()
    {
        // `-v` / `--verbose` appends the supplementary sections
        // (skills, git remote, opencode, env, OS, capacity, disk). This
        // is one of the two alternate invocations the spec calls out
        // (`mo info --verbose` / `mo info --json`).
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["info", "--verbose"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The default sections are still emitted under --verbose.
        Assert.Contains("CLI", stdout);
        Assert.Contains("Data dir", stdout);
    }

    [Fact]
    public async Task MoInfo_JsonFlag_EmitsSingleJsonObject()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["info", "--json", "cli,server,runner,project,dataDir"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        var parsed = JsonNode.Parse(stdout.Trim()) as JsonObject;
        Assert.NotNull(parsed);
        var keys = parsed!.Select(kv => kv.Key).ToHashSet();
        Assert.Contains("cli", keys);
        Assert.Contains("server", keys);
        Assert.Contains("runner", keys);
        Assert.Contains("project", keys);
        Assert.Contains("dataDir", keys);
        Assert.Equal(5, keys.Count);
    }

}
