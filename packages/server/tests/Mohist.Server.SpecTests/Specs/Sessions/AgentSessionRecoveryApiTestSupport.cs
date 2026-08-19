using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Sessions.Services;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Sessions;

public abstract class AgentSessionRecoveryApiTestSupport : IAsyncLifetime
{
    protected readonly MohistIntegrationFixture _fixture;
    protected readonly HttpClient _client;
    protected readonly string _runnerId = $"recovery-api-{Guid.NewGuid():N}";
    private readonly string _connectionId;
    protected readonly RecordingRunnerControlOwner RunnerControl;

    protected AgentSessionRecoveryApiTestSupport(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _connectionId = $"connection-{_runnerId}";

        var runnerControl = fixture.Services.GetRequiredService<RecordingRunnerControlTransport>();
        RunnerControl = runnerControl.CreateOwner(_runnerId);
        var tracker = fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var registered = false;
        try
        {
            RunnerControl.SetInvocationResponseFactory("session.command", arguments =>
            {
                var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
                return request.Command switch
                {
                    SessionCommandKind.Compact => new SessionCommandResult(Ok: true),
                    SessionCommandKind.Reset => new SessionCommandResult(
                        Ok: true,
                        RuntimeSessionId: $"{request.RuntimeSessionId ?? "new"}-replacement"),
                    _ => new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable),
                };
            });
            tracker.Register(_runnerId, _connectionId);
            registered = true;
        }
        catch
        {
            if (registered)
                tracker.Unregister(_runnerId, _connectionId);
            RunnerControl.Dispose();
            throw;
        }
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try
        {
            _fixture.Services.GetRequiredService<RunnerConnectionTracker>()
                .Unregister(_runnerId, _connectionId);
        }
        finally
        {
            RunnerControl.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    protected async Task SetPersistedRuntimeAsync(string sessionId, string? runtimeName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.AgentSessions.SingleAsync(r => r.Id == sessionId);
        var state = JsonNode.Parse(row.State)?.AsObject()
            ?? throw new InvalidOperationException($"Session {sessionId} state could not be parsed.");
        var runtime = state["runtime"]?.AsObject()
            ?? throw new InvalidOperationException($"Session {sessionId} state has no runtime binding.");
        if (runtimeName is null)
            runtime.Remove("runtime");
        else
            runtime["runtime"] = runtimeName;

        row.State = state.ToJsonString();
        await db.SaveChangesAsync();

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await TestLifecycle.Deactivate(grain);
        _ = await grain.GetAsync();
    }

    protected async Task<AgentSession> LoadSessionStateAsync(string sessionId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var state = await db.AgentSessions.AsNoTracking()
            .Where(row => row.Id == sessionId)
            .Select(row => row.State)
            .SingleAsync();
        return JsonSerializer.Deserialize<AgentSession>(state, JSON.Options)
            ?? throw new InvalidOperationException($"Session {sessionId} state could not be deserialized.");
    }

    protected static async Task<string[]> AssertRecoveryResponseAsync(
        HttpResponseMessage response,
        string expectedSessionId,
        string expectedOperation,
        bool expectedWasCompacted)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected recovery success, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(expectedSessionId, data.GetProperty("id").GetString());
        Assert.Equal(expectedOperation, data.GetProperty("operation").GetString());
        Assert.Equal(expectedWasCompacted, data.GetProperty("wasCompacted").GetBoolean());
        Assert.False(data.TryGetProperty("agentSessionId", out _));
        return data.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    protected SessionCommandRequest AssertSingleSessionCommandInvocation()
    {
        var invocation = Assert.Single(SessionCommandInvocations());
        return Assert.IsType<SessionCommandRequest>(Assert.Single(invocation.Arguments));
    }

    protected void AssertNoSessionCommandInvocations() =>
        Assert.Empty(SessionCommandInvocations());

    private RecordedRunnerControlRequest[] SessionCommandInvocations() =>
        RunnerControl.Invocations
            .Where(invocation => invocation.ConnectionId == _runnerId)
            .Where(invocation => invocation.Method == "session.command")
            .ToArray();

    protected async Task<AgentSessionInfo> CreateAgentLaunchSessionAsync(
        ProjectDto project,
        string name,
        bool attach)
    {
        var sessionId = $"agent-recovery-{Guid.NewGuid():N}";
        var workDir = $"/workspaces/{project.Id}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: "opencode",
            WorkDir: workDir,
            Model: null,
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                ProjectId: project.Id,
                AgentId: $"agent-{Guid.NewGuid():N}",
                AgentName: $"recovery-{name}"))));

        if (attach)
        {
            await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                AgentSessionId: sessionId,
                Model: null,
                WorkDir: workDir,
                ChangeDir: null,
                ProcessPid: 1234));
        }

        return await grain.GetAsync()
            ?? throw new InvalidOperationException($"Agent session {sessionId} was not created.");
    }

    protected async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, CreatedSession Session)> CreateAndStartSessionAsync(
        string name,
        string sessionName = "plan",
        bool attach = false)
    {
        var (project, issue) = await CreateProjectAndIssueAsync(name);
        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/opencode",
            WorkType: "task",
            Stage: "Build",
            Title: $"Session api {name}",
            Issue: new WorkIssueRef(project.Id, issue.Number));

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();
        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, sessionName, work, $"Session api {name}");

        if (attach)
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, runtime = "opencode", expectedRuntime = "opencode", expectedRuntimeSessionId = (string?)null, workDir = $"/workspaces/{project.Id}", processPid = 1234 });

        return (project, issue, work, currentSession);
    }

    protected async Task<(ProjectDto Project, IssueDto Issue)> CreateProjectAndIssueAsync(string name)
    {
        var projectName = $"recovery-api-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = $"Recovery api {name}", body = "track sessions", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        return (project, issue);
    }

    protected async Task<CreatedSession> OpenRunnerSessionAsync(string projectId, int issueNumber, string workflowRunId, string sessionName, WorkDispatch work, string title)
    {
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/open", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            title,
            issueNumber,
            runtime = "opencode"
        });

        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);
        var session = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
        return new CreatedSession(projectId, issueNumber, workflowRunId, sessionName, session ?? throw new InvalidOperationException($"Session {workflowRunId}/{sessionName} was not created."));
    }

    protected async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    protected string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    protected string RunnerAgentSessionRuntimeEventsPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    protected string RunnerSessionPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}";

    protected sealed record ProjectDto(string Id, string Name);
    protected sealed record IssueDto(string Id, int Number, string Title);
    protected sealed record CreatedSession(
        string ProjectId,
        int IssueNumber,
        string WorkflowRunId,
        string SessionName,
        AgentSessionInfo Info)
    {
        public string Id => Info.Id;
    }
}
