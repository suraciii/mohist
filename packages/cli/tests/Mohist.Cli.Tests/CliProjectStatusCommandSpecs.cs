using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for the relocated `mo project status` command (T-001 of issue #387).
// The legacy root-level `mo status` was a 1-line handler that issued
// `GET /api/status?all=true`. This file verifies that:
//
//   1. The `status` subcommand now lives under `mo project` as a peer of
//      `list`/`create`/`show`/`use`/`delete`/`workflow`.
//   2. `mo project status --help` advertises no positional `project` argument
//      and no `--project` / `--project-id` flags — the underlying endpoint
//      aggregates across all projects by design (`all=true`), so neither is
//      semantically appropriate.
//   3. `mo project status` reproduces the legacy behavior byte-identically:
//      issues `GET /api/status?all=true`, renders the response, exits 0;
//      server-unreachable emits the same `Server is not running. Start with:
//      mo server start` guidance the legacy command did.
//   4. The legacy `mo status` root path is removed (D1: no alias) — it no
//      longer resolves and exits non-zero.
public class CliProjectStatusCommandSpecs
{
    [Fact]
    public async Task Project_Help_ListsStatusAlongsideOtherVerbs()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // All six sibling project verbs must still be listed; status is the
        // new seventh, registering as a peer per spec requirement.
        Assert.Contains("list", stdout);
        Assert.Contains("create", stdout);
        Assert.Contains("show", stdout);
        Assert.Contains("use", stdout);
        Assert.Contains("delete", stdout);
        Assert.Contains("workflow", stdout);
        Assert.Contains("status", stdout);
    }

    [Fact]
    public async Task ProjectStatus_Help_AdvertisesNoProjectArgument()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "status", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // No positional `project` argument — the row must not contain
        // `status <project>` (which is the help format for a command that
        // does take a positional `project` argument, like `use`).
        Assert.DoesNotContain("status <project>", stdout);
        Assert.DoesNotContain("status <", stdout);
    }

    [Fact]
    public async Task ProjectStatus_Help_AdvertisesNoProjectRefFlags()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "status", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The endpoint aggregates across all projects (`all=true`); neither
        // a project argument nor `--project` / `--project-id` flags make
        // sense. Neither should be advertised in the help.
        Assert.DoesNotContain("--project", stdout);
        Assert.DoesNotContain("--project", stdout);
    }

    [Fact]
    public async Task ProjectStatus_HitsStatusEndpointAndRendersResponse()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { running = true, capacity = 42 },
            })),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "status"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/status?all=true", request.RequestUri?.PathAndQuery);
        // Rendered through the same `PrintResponseAsync` path the legacy
        // command used: success envelope → `data` block to stdout.
        Assert.Contains("\"running\": true", output.ToString());
        Assert.Contains("\"capacity\": 42", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ProjectStatus_ServerUnreachable_EmitsLegacyGuidanceAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new HttpRequestException("connection refused"),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "status"], output, error, fs, executor);

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
        Assert.Equal("/api/status?all=true", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task LegacyRootStatus_NoLongerResolvesAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["status"], output, error, fs, executor);

        // Per D1 (no aliases retained) the legacy `mo status` root path is
        // removed outright — System.CommandLine surfaces a parse error and
        // the runner returns non-zero. No HTTP request must be issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}
