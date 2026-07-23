using System.Net;
using System.Text.Json;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

// Specs for the single-entry `mo project use` command (T-003 of issue #387).
//
// Before this change there were two duplicate entries for setting the active
// project — the root-level `mo use <project>` (BuildUseCommand in
// MohistCliCommands.cs, sharing the same `UseProjectAsync` handler) and the
// `mo project use <project>` factory under ProjectCommands. This file verifies
// that:
//
//   1. `mo --help` no longer lists `use` as a top-level subcommand.
//   2. The legacy `mo use <project>` root path no longer resolves — it exits
//      non-zero with no HTTP request issued (D1: no alias retained).
//   3. `mo project use <identifier>` keeps the legacy behavior byte-identically:
//      resolves the identifier, POSTs `/api/projects/<identifier>/use` with an
//      empty body, persists `activeProjectId` to local project state, prints
//      `Active project: <name> (<id>)` to stdout, exits 0.
//   4. On server rejection or unreachable, `mo project use` emits the same
//      error/guidance text, exits non-zero, and does not modify local project
//      state.
public class CliProjectUseCommandSpecs
{
    private static string ProjectStatePath() =>
        Path.Combine(CliTestFactory.UserHome, ".mohist", "cli-state.json");

    private static string CurrentProjectStatePath() =>
        Path.Combine("/", ".mohist", "cli-state.json");

    [Fact]
    public async Task Root_Help_NoLongerListsUseSubcommand()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // `mo --help` lists the surviving resource groups and the controlled
        // `info` exception. The duplicate root-level `use` is gone.
        // Anchor on the explicit bare line `  use  ` (with surrounding spaces)
        // because `use` is a substring of many legitimate words (`because`,
        // `repository`) and an unanchored assertion would be brittle.
        Assert.DoesNotContain("\n  use ", stdout);
        // The surviving `project` resource group must still be listed.
        Assert.Contains("\n  project ", stdout);
    }

    [Fact]
    public async Task Project_Help_StillListsUseSubcommand()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        // The surviving `use` subcommand must still be advertised under
        // `mo project --help` — only the duplicate root-level entry went away.
        Assert.Contains("\n  use ", stdout);
    }

    [Fact]
    public async Task ProjectUse_PostsUseEndpointAndPersistsActiveProjectAndPrintsConfirmation()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "proj_active", name = "Active App" },
            })),
            activeProjectId: null);

        var statePath = ProjectStatePath();
        Assert.False(fs.Exists(statePath), "Pre-condition: no project state file");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "use", "active-app"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        // The surviving entry must hit the legacy endpoint exactly: POST
        // `/api/projects/<identifier>/use` with an empty body. The identifier
        // is URL-escaped on the wire path but the test uses a plain
        // alphanumeric slug here for clarity.
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/active-app/use", request.RequestUri?.PathAndQuery);
        // Body is the JSON-serialized `new { }` that `UseProjectAsync` passes
        // to `PostDataAsync` (byte-identical to the legacy behavior). Empty
        // string would mean a regression.
        Assert.Equal("{}", request.Body);
        // The same `Active project: <name> (<id>)` confirmation the legacy
        // command printed must be on stdout.
        var stdout = output.ToString();
        Assert.Contains("Active project: Active App (proj_active)", stdout);
        Assert.Empty(error.ToString());
        // The handler call must have persisted `activeProjectId` to local
        // project state on disk.
        Assert.True(fs.Exists(statePath));
        var saved = fs.ReadAllText(statePath);
        Assert.Contains("proj_active", saved);
        Assert.Contains("activeProjectId", saved);
        Assert.True(fs.Exists(CurrentProjectStatePath()));
        Assert.Equal(saved, fs.ReadAllText(CurrentProjectStatePath()));
    }

    [Fact]
    public async Task ProjectUse_IdentifierUrlEscapingProducesExpectedPath()
    {
        // The endpoint string is `$"/api/projects/{Uri.EscapeDataString(identifier)}/use"`.
        // Use an identifier that exercises escaping so a regression in the
        // escaping logic would fail this spec.
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new { id = "proj_space", name = "my app" },
            })),
            activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "use", "my app"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal("/api/projects/my%20app/use", request.RequestUri?.PathAndQuery);
        Assert.Contains("Active project: my app (proj_space)", output.ToString());
    }

    [Fact]
    public async Task ProjectUse_NoIdentifier_FailsToParseAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "use"], output, error, fs, executor);

        // No identifier is a parse error: System.CommandLine rejects it before
        // any handler runs. No HTTP request must be issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProjectUse_ServerRejects_LetsLocalStateUnmodifiedAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.JsonError("Project not found", code: "project_not_found", statusCode: HttpStatusCode.NotFound)),
            activeProjectId: null);

        var statePath = ProjectStatePath();
        Assert.False(fs.Exists(statePath), "Pre-condition: no project state file");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "use", "does-not-exist"], output, error, fs, executor);

        // Server rejected the request (`Project not found`) → the handler
        // throws ApiResponseException with 404 → FailureExitCode returns 4
        // (see MohistCliApi.FailureExitCode for NotFound → 4).
        Assert.Equal(1, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/does-not-exist/use", request.RequestUri?.PathAndQuery);
        // No `Active project: …` confirmation on stdout.
        Assert.DoesNotContain("Active project:", output.ToString());
        // The error must surface as a stderr message — `ex.Message` is the
        // server-supplied envelope error text, optionally with `(code)`
        // appended if a code is present. Either form is acceptable as long as
        // the human-readable error from the server is on stderr.
        var stderr = error.ToString();
        Assert.Contains("Project not found", stderr);
        // Crucially: no project state file is written.
        Assert.False(fs.Exists(statePath));
    }

    [Fact]
    public async Task ProjectUse_ServerRejection_WithExistingState_LeavesActiveProjectIdUnmodified()
    {
        // If a state file already exists for a different project, a failed
        // `project use` must NOT overwrite it.
        const string existingId = "proj_existing";
        var statePath = ProjectStatePath();
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.JsonError("Project not found", statusCode: HttpStatusCode.NotFound)),
            activeProjectId: existingId);

        // Sanity-check the setup wrote the state file.
        Assert.True(fs.Exists(statePath));
        var before = fs.ReadAllText(statePath);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "use", "does-not-exist"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        // The state file must still hold the original `proj_existing` —
        // a failed `use` must never clobber a previously persisted project.
        Assert.True(fs.Exists(statePath));
        var after = fs.ReadAllText(statePath);
        Assert.Equal(before, after);
        Assert.Contains(existingId, after);
    }

    [Fact]
    public async Task ProjectUse_ServerUnreachable_EmitsLegacyGuidanceAndDoesNotModifyState()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(
            (_, _) => throw new HttpRequestException("connection refused"),
            activeProjectId: null);

        var statePath = ProjectStatePath();
        Assert.False(fs.Exists(statePath), "Pre-condition: no project state file");

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "use", "anywhere"], output, error, fs, executor);

        // `UseProjectAsync` swallows HttpRequestException and writes
        // `ServerUnavailableMessage` to stderr, returning 1 — same shape the
        // legacy command emitted. Exactly one outbound POST is recorded
        // because RecordingHttpHandler captures the request before delegating
        // to the throwing responder.
        Assert.Equal(1, exitCode);
        Assert.Contains(MohistCliApi.ServerUnavailableMessage, error.ToString());
        Assert.Contains("mo service start server", error.ToString());
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/projects/anywhere/use", request.RequestUri?.PathAndQuery);
        // No confirmation on stdout; no state file written.
        Assert.DoesNotContain("Active project:", output.ToString());
        Assert.False(fs.Exists(statePath));
    }

    [Fact]
    public async Task LegacyRootUse_NoLongerResolvesAndExitsNonZero()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["use", "proj_anything"], output, error, fs, executor);

        // Per D1 the legacy `mo use <project>` root path is removed outright
        // (no alias retained). System.CommandLine surfaces a parse error and
        // the runner returns non-zero. No HTTP request must be issued.
        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}
