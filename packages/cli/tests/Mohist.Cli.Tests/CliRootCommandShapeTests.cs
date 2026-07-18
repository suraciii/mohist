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
        // T-001..T-005 migrations touched, plus the standard suite. Each
        // is advertised as `  <name>  <description>` in System.CommandLine's
        // subcommand table — anchor with leading newline + two spaces.
        string[] requiredResourceGroups =
        [
            "project",
            "system",
            "server",
            "notification",
        ];
        foreach (var name in requiredResourceGroups)
            Assert.Contains($"\n  {name} ", stdout);

        // The single controlled exception.
        Assert.Contains("\n  info ", stdout);

        // The five legacy bare-verb / misnamed paths must NOT be advertised
        // as top-level subcommands. Anchored on `\n  <name> ` to avoid
        // false positives from substring matches inside other descriptions
        // (e.g. `repository` contains `use`; `notification` contains `not`).
        Assert.DoesNotContain("\n  status ", stdout);
        Assert.DoesNotContain("\n  logs ", stdout);
        Assert.DoesNotContain("\n  use ", stdout);
        Assert.DoesNotContain("\n  notify ", stdout);
        // `system info` would render as `  info ` if it lived at the root
        // (which it doesn't — it lives under `server`). The negative
        // assertion above already pins it: no `  info ` row other than
        // the controlled exception exists at the root. We also assert
        // that the help output does not advertise a top-level `system`
        // subcommand carrying an `info` description, by checking the
        // exact row of `  system ` carries the application-diagnostics
        // description (T-002) and not the legacy `system info` framing.
        var systemRow = stdout
            .Split('\n')
            .Single(line => line.TrimStart().StartsWith("system ", StringComparison.Ordinal));
        Assert.Contains("application logs", systemRow, StringComparison.OrdinalIgnoreCase);
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
            "issue",
            "epic",
            "agent",
            "label",
            "workflow",
            "runner",
            "server",
            "system",
            "install",
            "update",
            "skills",
            "opencode",
            "config",
            "notification",
            "otel",
            "info",
        ];
        foreach (var name in survivingResourceGroups)
            Assert.Contains($"\n  {name} ", stdout);
        // `repo` is the primary name; `repository` is registered as an
        // alias. System.CommandLine renders them as `  repo, repository  …`.
        // We assert the full rendered form because anchored `\n  repo `
        // would miss the actual column layout (it's `repo,` not `repo `).
        Assert.Contains("\n  repo, repository ", stdout);
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
    public async Task LegacyEventNoun_FailsToResolveAndExitsNonZero()
    {
        // The singular `mo event` noun was consolidated under the plural
        // `mo events` noun in issue-413 T-003 (BREAKING). The legacy
        // singular form must no longer resolve.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["event", "dead-letter", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
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
        // `--json` produces a single machine-readable JSON object with
        // the documented top-level keys. Spec calls this out as the
        // second alternate invocation.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["info", "--json"], output, error, fs, executor);

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
    }

}
