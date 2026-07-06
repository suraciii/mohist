using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for the relocated `mo system logs` command (T-002 of issue #387).
// The legacy root-level `mo logs` was a 1-line handler that issued
// `GET /api/logs/tail`. This file verifies that:
//
//   1. The `logs` subcommand now lives under `mo system` and is listed by
//      `mo system --help` (alongside the still-present `info`).
//   2. The `system` group description explicitly identifies its logs as
//      application logs (Mohist server's own log tail) and distinguishes
//      them from the operational logs surfaced by `mo server logs`
//      (systemd journal / scheduled-task output) — see design D3.
//   3. `mo system logs` reproduces the legacy behavior byte-identically:
//      issues `GET /api/logs/tail`, renders the response, exits 0;
//      server-unreachable emits the same `Server is not running. Start with:
//      mo server start` guidance the legacy command did.
//   4. The legacy `mo logs` root path is removed (D1: no alias) — it no
//      longer resolves and exits non-zero.
public class CliSystemLogsCommandSpecs
{
    [Fact]
    public async Task System_Help_ListsLogsAlongsideInfo()
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The new `logs` subcommand must be advertised.
        Assert.Contains("logs", stdout);
        // `info` is still a sibling — T-005 will relocate it later; until
        // then `system` exposes both, so the help must list both verbs.
        Assert.Contains("info", stdout);
    }

    [Fact]
    public async Task System_Help_DescriptionIdentifiesApplicationLogsAndDistinguishesFromServerLogs()
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The system group description is reframed (D3) to identify its
        // logs as application logs and distinguish them from the
        // operational logs surfaced by `mo server logs`. Both anchors must
        // appear in the help text.
        Assert.Contains("application logs", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo server logs", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_Help_DescriptionStillDisambiguatesFromMoInfo()
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The pre-existing `mo info` distinction is part of the system group
        // description too — T-002 must preserve it while reframing the
        // group around application diagnostics.
        Assert.Contains("mo info", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemLogs_HitsLogsEndpointAndRendersResponse()
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { lines = new[] { "2026-07-06T10:00:00Z INFO  server started", "2026-07-06T10:00:01Z INFO  listening on :8644" } },
            })),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "logs"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/logs/tail", request.RequestUri?.PathAndQuery);
        // Rendered through the same `PrintResponseAsync` path the legacy
        // command used: success envelope → `data` block to stdout.
        Assert.Contains("server started", output.ToString());
        Assert.Contains("listening on :8644", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task SystemLogs_CommandDescriptionDistinguishesFromServerLogs()
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "logs", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The command-level description mirrors the group-level distinction:
        // it identifies the logs as application logs and points readers to
        // `mo server logs` for the operational counterpart.
        Assert.Contains("application logs", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mo server logs", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemLogs_ServerUnreachable_EmitsLegacyGuidanceAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create(
            (_, _) => throw new HttpRequestException("connection refused"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["system", "logs"], output, error, fs, executor);

        // `PrintGetAsync` returns 1 when `SendAsync` swallows the
        // `HttpRequestException` and prints the unavailable message; the
        // legacy command surfaced the same exit code via the same path.
        Assert.NotEqual(0, exitCode);
        var stderr = error.ToString();
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, stderr);
        Assert.Contains("mo server start", stderr);
        // Exactly one outbound GET — to the legacy endpoint. The responder
        // threw, but `RecordingHttpHandler` records the request before
        // delegating to the responder.
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/logs/tail", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task LegacyRootLogs_NoLongerResolvesAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestHarness.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["logs"], output, error, fs, executor);

        // Per D1 (no aliases retained) the legacy `mo logs` root path is
        // removed outright — System.CommandLine surfaces a parse error and
        // the runner returns non-zero. No HTTP request must be issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}
