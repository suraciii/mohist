using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for issue-480 T-004 — the `system` command group is retired now
// that `mo server logs` owns application logs (T-003) and the new `service`
// group owns local managed-process lifecycle (T-001). The group is removed
// entirely; the spec scenarios guarding this are:
//
//   * `mo system logs` MUST NOT resolve (exit non-zero, no HTTP request).
//   * `mo system --help` MUST NOT resolve (exit non-zero).
//   * `mo logs` (root) — the earlier #387 retirement — also stays gone.
//
// See:
//   - openspec/changes/issue-480/specs/server-commands/spec.md
//     "Application logs have a single entry point"
//   - openspec/changes/archive/2026-07-06-issue-387/ (the prior retirement
//     of the root-level `logs` command)
public class CliSystemGroupRetiredSpecs
{
    [Fact]
    public async Task SystemLogs_NoLongerResolvesAndIssuesNoHttpRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "logs"], output, error, fs, executor);

        // Per issue #480 / D4: the `system` group is removed. The relocated
        // application-log read lives at `mo server logs` (see
        // CliServerStatusCommandSpecs.ServerLogs_*). No HTTP request must
        // be issued here — System.CommandLine surfaces a parse error and
        // the runner returns non-zero.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task System_NoLongerResolves()
    {
        // The spec scenario names `mo system --help`. System.CommandLine
        // handles `--help` specially and returns 0 for unknown commands
        // because it falls back to nearest-command help. The structural
        // invariant — `system` is no longer a registered group — is
        // pinned by:
        //   1. `mo system logs` exits non-zero with no HTTP request
        //      (covered by SystemLogs_NoLongerResolvesAndIssuesNoHttpRequest)
        //   2. the root `--help` no longer lists `system` as a subcommand
        //      (covered by CliRootCommandShapeTests.Root_Help_ListsResourceGroupsAndInfoOnly_NoBareVerbs)
        // We keep this test as a no-HTTP regression guard for the bare
        // `mo system` form to confirm no installer / API call sneaks in
        // from any future parser twist.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LegacyRootLogs_NoLongerResolvesAndIssuesNoHttpRequest()
    {
        // Regression guard from #387: `mo logs` (root) was retired when
        // application logs moved under `mo system logs`. After #480 the
        // root path stays retired — application logs live at `mo server logs`.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["logs"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}