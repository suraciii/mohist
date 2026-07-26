using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

/// <summary>
/// Issue 482 T-001 canonical command-surface regression suite.
///
/// Locks the converged `mo` tree to the spec-mandated areas, verbs, ownership
/// paths, project selector, and JSON vocabulary. Every removed path must exit
/// with a usage failure and make no remote request; every canonical path must
/// preserve its prior request and response semantics.
/// </summary>
public class CliCanonicalCommandSurfaceSpecs
{
    private const string ActiveProjectId = "proj_test";

    private static (RecordingHttpHandler handler, HttpClient http, StringWriter output, StringWriter error, FakeFileSystem fs, FakeCommandExecutor executor)
        SetupEnv(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responder = null,
            string? activeProjectId = ActiveProjectId)
    {
        return CliTestFactory.Create(
            responder ?? ((_, _) => Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }))),
            activeProjectId);
    }

    // ---------- Root exposes the canonical areas ----------

    [Fact]
    public async Task Root_Help_ListsAllCanonicalAreas()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        string[] canonical =
        [
            "project", "repo", "issue", "epic", "label",
            "workflow", "run", "agent", "session", "activity",
            "routing", "runner", "server", "service", "event",
            "notification", "otel", "skill", "install", "update", "info",
        ];
        foreach (var name in canonical)
            Assert.Contains($"\n  {name} ", stdout);
    }

    [Fact]
    public async Task Root_Help_DoesNotListRemovedRootAreas()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.DoesNotContain("\n  skills ", stdout);
        Assert.DoesNotContain("\n  opencode ", stdout);
        Assert.DoesNotContain("\n  config ", stdout);
        Assert.DoesNotContain("repository", stdout, StringComparison.Ordinal);
    }

    // ---------- Removed plural and alias paths fail locally ----------

    [Fact]
    public async Task SkillsPlural_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["skills", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SkillGet_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["skill", "get", "mohist"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepositoryAlias_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["repository", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpencodeRoot_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["opencode", "models"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ConfigRoot_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["config", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ConfigGet_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["config", "get", "server.tls.cert"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ---------- Issue view / edit / restore replace show / update / unarchive ----------

    [Fact]
    public async Task IssueView_HitsCanonicalEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "view", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueShow_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "show", "42"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueEdit_HitsCanonicalPatchEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "edit", "42", "--title", "New"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueUpdate_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "update", "42", "--title", "New"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueRestore_HitsCanonicalEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "restore", "42"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/issues/42/restore", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task IssueUnarchive_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "unarchive", "42"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ---------- Project view, Agent view/edit, Epic view/edit, Session view ----------

    [Fact]
    public async Task ProjectView_HitsCanonicalEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "view", "mohist-local"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/api/projects/mohist-local", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task ProjectShow_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["project", "show", "mohist-local"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentView_HitsCanonicalEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv((req, _) =>
        {
            if (req.Method == HttpMethod.Get && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/agents", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_supervisor", name = "supervisor" } },
                }));
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "agent_supervisor", name = "supervisor" } }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "view", "supervisor"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Contains("/api/projects/" + ActiveProjectId + "/agents/agent_supervisor", handler.Requests.Last().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task AgentShow_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "show", "supervisor"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AgentEdit_HitsCanonicalPatchEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv((req, _) =>
        {
            if (req.Method == HttpMethod.Get && (req.RequestUri?.AbsolutePath ?? "").EndsWith("/agents", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new[] { new { id = "agent_supervisor", name = "supervisor" } },
                }));
            return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { id = "agent_supervisor", name = "supervisor" } }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "edit", "supervisor", "--description", "new"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Patch, handler.Requests.Last().Method);
    }

    [Fact]
    public async Task AgentUpdate_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["agent", "update", "supervisor", "--description", "new"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EpicView_HitsCanonicalEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "view", "10"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/{ActiveProjectId}/epics/10", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task EpicShow_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "show", "10"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EpicEdit_HitsCanonicalPatchEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "edit", "10", "--title", "New"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Patch, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task EpicUpdate_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["epic", "update", "10", "--title", "New"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SessionView_HitsCanonicalEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "view", "sess_abc"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal($"/api/projects/{ActiveProjectId}/sessions/sess_abc", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task SessionShow_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["session", "show", "sess_abc"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ---------- Routing rule view / edit replace show / update ----------

    [Fact]
    public async Task RoutingRuleView_HitsCanonicalEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["routing", "rule", "view", "supervisor-approval"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = handler.Requests.Single();
        Assert.Equal($"/api/projects/{ActiveProjectId}/routing/rules/supervisor-approval", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RoutingRuleShow_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["routing", "rule", "show", "supervisor-approval"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RoutingRuleEdit_HitsCanonicalPatchEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["routing", "rule", "edit", "supervisor-approval", "--name", "Renamed"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Patch, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task RoutingRuleUpdate_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["routing", "rule", "update", "supervisor-approval", "--name", "Renamed"],
            output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ---------- Repo edit replaces update ----------

    [Fact]
    public async Task RepoEdit_HitsCanonicalPatchEndpoint()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["repo", "edit", "origin", "--base-branch", "develop"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Equal(HttpMethod.Patch, handler.Requests.Single().Method);
        Assert.Equal($"/api/projects/{ActiveProjectId}/repositories/origin", handler.Requests.Single().RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task RepoUpdate_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["repo", "update", "origin", "--base-branch", "develop"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    // ---------- Project selector and JSON field discovery ----------

    [Fact]
    public async Task ProjectIdFlag_IsRejectedAsUsageFailure()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--project-id", "proj_test"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueList_BareJson_DiscoversFieldsWithoutRemoteRequest()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv(activeProjectId: null);

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var fields = JsonNode.Parse(output.ToString())!.AsArray()
            .Select(node => node!.GetValue<string>()).ToArray();
        Assert.Contains("number", fields);
        Assert.Contains("title", fields);
        Assert.Contains("status", fields);
        Assert.Contains("stage", fields);
        Assert.Contains("priority", fields);
        Assert.Contains("labels", fields);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task IssueList_SelectedJson_ProjectsOnlyChosenFields()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv((_, _) =>
            Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new { number = 1, title = "One", status = "open", stage = "backlog", priority = "p2", labels = new { } },
                },
            })));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--json", "number,title,status"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("\"number\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"title\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"status\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stage\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"priority\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"labels\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"success\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueList_InvalidJsonField_FailsAsUsageWithoutRemoteRequest()
    {
        var (handler, http, output, error, fs, executor) = SetupEnv();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["issue", "list", "--json", "number,nope"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
    }
}