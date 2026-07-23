using System.Net;
using System.Text.Json.Nodes;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public sealed class CliActivityListCommandSpecs
{
    [Fact]
    public async Task List_BuildsRouteAndForwardsLimitInQuery()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Array.Empty<object>(),
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--limit", "50"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/projects/proj_abc/activity?limit=50", request.RequestUri?.PathAndQuery);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task List_NoLimit_DefaultsTo100()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Array.Empty<object>(),
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_abc/activity?limit=100", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task List_ProjectFlagOverridesActiveProject()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Array.Empty<object>(),
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["activity", "list", "--project", "proj_other", "--limit", "10"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/projects/proj_other/activity?limit=10", request.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task List_NoActiveProject_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
        handler.SetResponder((_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list"], output, error, fs, executor);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains(MohistCliCommands.NoActiveProjectMessage, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_LimitBelowRange_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--limit", "0"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--limit must be between 1 and 200", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_LimitAboveRange_FailsLocallyWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--limit", "201"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("--limit must be between 1 and 200", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_BareJson_ListsSelectableFieldsWithoutHttp()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--json"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        Assert.Empty(handler.Requests);
        var fields = JsonNode.Parse(output.ToString()) as JsonArray;
        Assert.NotNull(fields);
        var names = fields!.Select(n => n!.GetValue<string>()).ToHashSet();
        Assert.Contains("id", names);
        Assert.Contains("provenance", names);
        Assert.Contains("scope", names);
        Assert.Contains("kind", names);
        Assert.Contains("time", names);
        Assert.Contains("title", names);
        Assert.Contains("description", names);
        Assert.Contains("eventType", names);
        Assert.Contains("issueNumber", names);
        Assert.Contains("workflowRunId", names);
        Assert.Contains("sessionId", names);
        Assert.Contains("runnerId", names);
        Assert.Contains("status", names);
    }

    [Fact]
    public async Task List_InvalidJsonField_FailsLocallyWithExitCodeTwo()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--json", "id,bogus"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.Contains("bogus", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task List_SelectedJson_ProjectsRequestedFieldsAsArray()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new
                    {
                        id = "rec:issue:1",
                        provenance = "recorded",
                        scope = "project",
                        kind = "issue",
                        time = "2026-07-17T12:00:00Z",
                        title = "Issue #1",
                        description = "com.mohist.issue.created",
                        eventType = "com.mohist.issue.created",
                        issueNumber = 1,
                        workflowRunId = (string?)null,
                        sessionId = (string?)null,
                        runnerId = (string?)null,
                        status = (string?)null,
                    },
                    new
                    {
                        id = "snapshot:runner:r1",
                        provenance = "snapshot",
                        scope = "global",
                        kind = "runner",
                        time = "2026-07-17T12:05:00Z",
                        title = "Runner r1",
                        description = "host runner on host-1",
                        eventType = (string?)null,
                        issueNumber = (int?)null,
                        workflowRunId = (string?)null,
                        sessionId = (string?)null,
                        runnerId = "r1",
                        status = "online",
                    },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http,
            ["activity", "list", "--json", "id,provenance,scope,kind,eventType,runnerId"],
            output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var data = JsonNode.Parse(output.ToString()) as JsonArray;
        Assert.NotNull(data);
        Assert.Equal(2, data!.Count);
        var recorded = data[0] as JsonObject;
        Assert.NotNull(recorded);
        Assert.Equal("rec:issue:1", recorded!["id"]!.GetValue<string>());
        Assert.Equal("recorded", recorded["provenance"]!.GetValue<string>());
        Assert.Equal("project", recorded["scope"]!.GetValue<string>());
        Assert.Equal("issue", recorded["kind"]!.GetValue<string>());
        Assert.Equal("com.mohist.issue.created", recorded["eventType"]!.GetValue<string>());
        Assert.False(recorded.ContainsKey("title"));
        Assert.False(recorded.ContainsKey("description"));
        var snapshot = data[1] as JsonObject;
        Assert.NotNull(snapshot);
        Assert.Equal("snapshot:runner:r1", snapshot!["id"]!.GetValue<string>());
        Assert.Equal("snapshot", snapshot["provenance"]!.GetValue<string>());
        Assert.Equal("global", snapshot["scope"]!.GetValue<string>());
        Assert.Equal("r1", snapshot["runnerId"]!.GetValue<string>());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task List_DistinguishesRecordedVersusSnapshotAndProjectVersusGlobal()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new object[]
                {
                    new
                    {
                        id = "rec:workflow-run:wf1",
                        provenance = "recorded",
                        scope = "project",
                        kind = "workflow-run",
                        time = "2026-07-17T12:00:00Z",
                        title = "Workflow run wf1",
                        description = "com.mohist.workflow.stage.started",
                        eventType = "com.mohist.workflow.stage.started",
                        workflowRunId = "wf1",
                    },
                    new
                    {
                        id = "snapshot:waiting:issue:42",
                        provenance = "snapshot",
                        scope = "project",
                        kind = "waiting",
                        time = "2026-07-17T12:01:00Z",
                        title = "Awaiting approval on issue 42",
                        description = "approval",
                        eventType = (string?)null,
                        issueNumber = 42,
                        status = "waiting",
                    },
                    new
                    {
                        id = "snapshot:runner:r1",
                        provenance = "snapshot",
                        scope = "global",
                        kind = "runner",
                        time = "2026-07-17T12:02:00Z",
                        title = "Runner r1",
                        description = "host runner on host-1",
                        runnerId = "r1",
                    },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("recorded", stdout, StringComparison.Ordinal);
        Assert.Contains("snapshot", stdout, StringComparison.Ordinal);
        Assert.Contains("project", stdout, StringComparison.Ordinal);
        Assert.Contains("global", stdout, StringComparison.Ordinal);
        Assert.Contains("wf1", stdout, StringComparison.Ordinal);
        Assert.Contains("Awaiting approval on issue 42", stdout, StringComparison.Ordinal);
        Assert.Contains("#42", stdout, StringComparison.Ordinal);
        Assert.Contains("r1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_DefaultTable_RendersActivityTableHeaders()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        id = "rec:issue:1",
                        provenance = "recorded",
                        scope = "project",
                        kind = "issue",
                        time = "2026-07-17T12:00:00Z",
                        title = "Issue #1",
                        description = "com.mohist.issue.created",
                        eventType = "com.mohist.issue.created",
                        issueNumber = 1,
                    },
                },
            }));
        });

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("provenance", stdout, StringComparison.Ordinal);
        Assert.Contains("scope", stdout, StringComparison.Ordinal);
        Assert.Contains("kind", stdout, StringComparison.Ordinal);
        Assert.Contains("time", stdout, StringComparison.Ordinal);
        Assert.Contains("title", stdout, StringComparison.Ordinal);
        Assert.Contains("recorded", stdout, StringComparison.Ordinal);
        Assert.Contains("project", stdout, StringComparison.Ordinal);
        Assert.Contains("issue", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_RepeatRead_IssuesTwoFiniteNormalGets()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        id = "rec:issue:1",
                        provenance = "recorded",
                        scope = "project",
                        kind = "issue",
                        time = "2026-07-17T12:00:00Z",
                        title = "Issue #1",
                        description = "com.mohist.issue.created",
                        eventType = "com.mohist.issue.created",
                        issueNumber = 1,
                    },
                },
            }));
        });

        var firstWriter = new StringWriter();
        var firstErrorWriter = new StringWriter();
        var firstExit = await MohistCliCommands.RunAsync(
            http, ["activity", "list"], firstWriter, firstErrorWriter, fs, executor);

        var secondWriter = new StringWriter();
        var secondErrorWriter = new StringWriter();
        var secondExit = await MohistCliCommands.RunAsync(
            http, ["activity", "list"], secondWriter, secondErrorWriter, fs, executor);

        Assert.Equal(0, firstExit);
        Assert.Equal(0, secondExit);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, req =>
            Assert.Equal(HttpMethod.Get, req.Method));
        Assert.All(handler.Requests, req =>
            Assert.EndsWith("/activity?limit=100", req.RequestUri?.PathAndQuery ?? ""));
        Assert.Equal(firstWriter.ToString(), secondWriter.ToString());
        Assert.Empty(firstErrorWriter.ToString());
        Assert.Empty(secondErrorWriter.ToString());
    }

    [Fact]
    public async Task List_LimitAtBoundary_IssuesRequest()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();
        handler.SetResponder((req, _) =>
        {
            if (req.RequestUri is null || !req.RequestUri.AbsolutePath.EndsWith("/activity", StringComparison.Ordinal))
                return Task.FromResult(RecordingHttpHandler.Json(new { success = true, data = new { } }));
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Array.Empty<object>(),
            }));
        });

        var exitMax = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--limit", "200"], output, error, fs, executor);
        var exitMin = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--limit", "1"], output, error, fs, executor);

        Assert.Equal(0, exitMax);
        Assert.Equal(0, exitMin);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/projects/proj_abc/activity?limit=200", handler.Requests[0].RequestUri?.PathAndQuery);
        Assert.Equal("/api/projects/proj_abc/activity?limit=1", handler.Requests[1].RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task List_Help_DescribesBoundedProvenanceAndScopeWithoutSharedFlags()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create();

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--help"], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var help = output.ToString();
        Assert.Contains("bounded", help, StringComparison.Ordinal);
        Assert.Contains("recorded", help, StringComparison.Ordinal);
        Assert.Contains("snapshot", help, StringComparison.Ordinal);
        Assert.Contains("provenance", help, StringComparison.Ordinal);
        Assert.Contains("scope", help, StringComparison.Ordinal);
        Assert.Contains("project", help, StringComparison.Ordinal);
        Assert.Contains("global", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--mode", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--source", help, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task List_Limit_ValidatedBeforeProjectResolution()
    {
        var (handler, http, output, error, fs, executor) = CliTestFactory.Create(activeProjectId: null);
        handler.SetResponder((_, _) => throw new InvalidOperationException("API must not be called"));

        var exitCode = await MohistCliCommands.RunAsync(
            http, ["activity", "list", "--limit", "0"], output, error, fs, executor);

        Assert.Equal(2, exitCode);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(MohistCliCommands.NoActiveProjectMessage, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--limit must be between 1 and 200", error.ToString(), StringComparison.Ordinal);
    }
}
