using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using DomainAgent = Mohist.Server.Agent.Domain.Agent;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

public class AgentJobReadRoutesSpecs
{
    private static readonly ProjectInfo Project = new()
    {
        Id = "proj_read",
        Name = "read-project",
    };

    [Fact]
    public async Task List_ReturnsJobsMostRecentFirst_WithIdAndStatusAndAgentName()
    {
        var agentId = "agent_read_jobs";
        using var db = AgentJobReadTestDb.WithAgent(Project.Id, agentId, "reviewer");
        await SeedJobAsync(db.Store, agentId, Project.Id, "job-old",
            AgentJobStatus.Completed, submitted: "2026-07-25T08:00:00Z");
        await SeedJobAsync(db.Store, agentId, Project.Id, "job-new",
            AgentJobStatus.Running, submitted: "2026-07-25T12:00:00Z");

        var result = await AgentJobReadRoutes.HandleListAsync(
            Project, agentId, status: null, limit: null, db.AgentQuerier, db.JobQuerier, CancellationToken.None);

        var payload = await AsPayloadAsync(result);
        Assert.True(payload.GetProperty("success").GetBoolean());
        var data = payload.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("jobId").GetString()!).ToArray();
        Assert.Equal(new[] { "job-new", "job-old" }, data);

        var first = payload.GetProperty("data").EnumerateArray().First();
        Assert.Equal("job-new", first.GetProperty("jobId").GetString());
        Assert.Equal("running", first.GetProperty("status").GetString());
        Assert.Equal(agentId, first.GetProperty("agentId").GetString());
        Assert.Equal("reviewer", first.GetProperty("agentName").GetString());
        Assert.Contains("2026-07-25T12:00:00", first.GetProperty("submittedAt").GetString());
    }

    [Fact]
    public async Task List_EmptyForAgentWithNoJobs()
    {
        var agentId = "agent_no_jobs";
        using var db = AgentJobReadTestDb.WithAgent(Project.Id, agentId, "lonely");

        var result = await AgentJobReadRoutes.HandleListAsync(
            Project, agentId, status: null, limit: null, db.AgentQuerier, db.JobQuerier, CancellationToken.None);

        var payload = await AsPayloadAsync(result);
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(0, payload.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task List_UnknownAgent_Returns404()
    {
        using var db = AgentJobReadTestDb.WithAgent(Project.Id, "agent_known", "known");

        var result = await AgentJobReadRoutes.HandleListAsync(
            Project, "agent_does_not_exist", status: null, limit: null, db.AgentQuerier, db.JobQuerier, CancellationToken.None);

        await AssertNotFoundAsync(result, "agent_does_not_exist");
    }

    [Fact]
    public async Task List_StatusFilterReturnsOnlyMatchingJobs()
    {
        var agentId = "agent_filter";
        using var db = AgentJobReadTestDb.WithAgent(Project.Id, agentId, "filter");
        await SeedJobAsync(db.Store, agentId, Project.Id, "job-completed", AgentJobStatus.Completed);
        await SeedJobAsync(db.Store, agentId, Project.Id, "job-failed", AgentJobStatus.Failed);
        await SeedJobAsync(db.Store, agentId, Project.Id, "job-running", AgentJobStatus.Running);

        var result = await AgentJobReadRoutes.HandleListAsync(
            Project, agentId, status: "completed,failed", limit: null, db.AgentQuerier, db.JobQuerier, CancellationToken.None);

        var payload = await AsPayloadAsync(result);
        var ids = payload.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("jobId").GetString()).ToHashSet();
        Assert.Contains("job-completed", ids);
        Assert.Contains("job-failed", ids);
        Assert.DoesNotContain("job-running", ids);
    }

    [Fact]
    public async Task View_TerminalCompleted_ReturnsStatusAndAllTerminalFields()
    {
        var terminal = new AgentJobTerminalResult(
            AgentJobStatus.Completed, "done", "{\"out\":1}", new[] { "art-1", "art-2" }, null, 0);
        var grain = new ReadAgentJobGrain(AgentJobStatus.Completed, Project.Id, terminal);

        var result = await AgentJobReadRoutes.HandleViewAsync(
            Project, "job-completed", FactoryFor(grain));

        var payload = await AsPayloadAsync(result);
        Assert.True(payload.GetProperty("success").GetBoolean());
        var data = payload.GetProperty("data");
        Assert.Equal("job-completed", data.GetProperty("jobId").GetString());
        Assert.Equal("completed", data.GetProperty("status").GetString());
        Assert.Equal("done", data.GetProperty("message").GetString());
        Assert.Equal("{\"out\":1}", data.GetProperty("output").GetString());
        Assert.Equal(new[] { "art-1", "art-2" },
            data.GetProperty("artifactUploadIds").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(0, data.GetProperty("exitCode").GetInt32());
        Assert.True(data.TryGetProperty("failureReason", out var fr) && fr.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task View_ReturnsPersistedExecutionDefinition()
    {
        var definition = new AgentExecutionDefinition(
            "Review the change.", "pi", "anthropic/claude", "high", ["mohist", "review"]);
        var grain = new ReadAgentJobGrain(
            AgentJobStatus.Running, Project.Id, terminalResult: null, executionDefinition: definition);

        var result = await AgentJobReadRoutes.HandleViewAsync(
            Project, "job-definition", FactoryFor(grain));

        var payload = await AsPayloadAsync(result);
        var value = payload.GetProperty("data").GetProperty("executionDefinition");
        Assert.Equal("Review the change.", value.GetProperty("instructions").GetString());
        Assert.Equal("pi", value.GetProperty("runtime").GetString());
        Assert.Equal("anthropic/claude", value.GetProperty("model").GetString());
        Assert.Equal("high", value.GetProperty("variant").GetString());
        Assert.Equal(["mohist", "review"], value.GetProperty("skills").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task View_TerminalFailed_ReturnsFailureReasonAndExitCode()
    {
        var terminal = new AgentJobTerminalResult(
            AgentJobStatus.Failed, null, null, null, "runner-unavailable", 1);
        var grain = new ReadAgentJobGrain(AgentJobStatus.Failed, Project.Id, terminal);

        var result = await AgentJobReadRoutes.HandleViewAsync(
            Project, "job-failed", FactoryFor(grain));

        var payload = await AsPayloadAsync(result);
        var data = payload.GetProperty("data");
        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.Equal("runner-unavailable", data.GetProperty("failureReason").GetString());
        Assert.Equal(1, data.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task View_NonTerminal_ShowsStatusWithoutAssertingTerminalResult()
    {
        var grain = new ReadAgentJobGrain(AgentJobStatus.Running, Project.Id, terminalResult: null);

        var result = await AgentJobReadRoutes.HandleViewAsync(
            Project, "job-running", FactoryFor(grain));

        var payload = await AsPayloadAsync(result);
        var data = payload.GetProperty("data");
        Assert.Equal("running", data.GetProperty("status").GetString());
        Assert.True(data.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Null);
        Assert.True(data.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Null);
        Assert.True(data.TryGetProperty("failureReason", out var fr) && fr.ValueKind == JsonValueKind.Null);
        Assert.True(data.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Null);
        Assert.Equal(0, grain.TerminalResultCalls);
    }

    [Fact]
    public async Task View_PreCutoverJob_LoadsRealStateFromGrainWithoutRow()
    {
        var terminal = new AgentJobTerminalResult(
            AgentJobStatus.Completed, "real-state", null, null, null, 0);
        var grain = new ReadAgentJobGrain(AgentJobStatus.Completed, Project.Id, terminal);

        var result = await AgentJobReadRoutes.HandleViewAsync(
            Project, "agent-job-pre-cutover", FactoryFor(grain));

        var payload = await AsPayloadAsync(result);
        var data = payload.GetProperty("data");
        Assert.Equal("completed", data.GetProperty("status").GetString());
        Assert.Equal("real-state", data.GetProperty("message").GetString());
    }

    [Fact]
    public async Task View_UnknownJobId_Returns404()
    {
        var grain = new ReadAgentJobGrain(AgentJobStatus.Pending, projectId: null, terminalResult: null);

        var result = await AgentJobReadRoutes.HandleViewAsync(
            Project, "never-existed", FactoryFor(grain));

        await AssertNotFoundAsync(result, "Job not found");
    }

    [Fact]
    public async Task View_CrossProjectJob_Returns404()
    {
        var otherProject = new ProjectInfo { Id = "proj_other", Name = "other" };
        var grain = new ReadAgentJobGrain(AgentJobStatus.Completed, otherProject.Id,
            new AgentJobTerminalResult(AgentJobStatus.Completed, "x", null, null, null, 0));

        var result = await AgentJobReadRoutes.HandleViewAsync(
            Project, "job-cross", FactoryFor(grain));

        await AssertNotFoundAsync(result, "Job not found");
    }

    private static async Task<JsonElement> AsPayloadAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                foreach (var converter in JSON.Options.Converters)
                    o.SerializerOptions.Converters.Add(converter);
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var element = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return element;
    }

    private static async Task AssertNotFoundAsync(IResult result, string expectedFragment)
    {
        var (body, status) = await ExecuteAsync(result);
        Assert.Equal(404, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Contains(expectedFragment, body.GetProperty("error").GetString()!);
    }

    private static async Task<(JsonElement body, int status)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JSON.Options.PropertyNamingPolicy;
                foreach (var converter in JSON.Options.Converters)
                    o.SerializerOptions.Converters.Add(converter);
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var element = (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return (element, context.Response.StatusCode);
    }

    private static IGrainFactory FactoryFor(IAgentJobGrain grain) => new SingleAgentJobGrainFactory(grain);

    private static async Task SeedJobAsync(
        AgentJobStore store, string agentId, string projectId, string key,
        AgentJobStatus status, string? submitted = null)
    {
        var state = new AgentJobState
        {
            Status = status,
            Input = new AgentJobInput(Prompt: "p", ProjectId: projectId, AgentId: agentId),
            SubmittedAt = submitted is null
            ? new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero)
            : DateTimeOffset.Parse(submitted),
        };
        await store.SaveAsync(key, JsonSerializer.Serialize(state, JSON.Options));
    }
}

internal sealed class AgentJobReadTestDb : IDisposable
{
    private readonly TestSqliteDatabase _database;

    public AgentJobReadTestDb(TestSqliteDatabase database, AgentJobStore store, AgentQuerier agentQuerier, AgentJobQuerier jobQuerier)
    {
        _database = database;
        Store = store;
        AgentQuerier = agentQuerier;
        JobQuerier = jobQuerier;
    }

    public AgentJobStore Store { get; }
    public AgentQuerier AgentQuerier { get; }
    public AgentJobQuerier JobQuerier { get; }

    public static AgentJobReadTestDb WithAgent(string projectId, string agentId, string name)
    {
        var database = TestSqliteDatabase.CreateMigrated();
        var factory = new TestDbContextFactory(database.Options);
        var domain = new DomainAgent
        {
            Id = agentId,
            ProjectId = projectId,
            Name = name,
            Status = AgentStatus.Active,
        };
        using (var db = new MohistDbContext(database.Options))
        {
            db.Agents.Add(new AgentRow
            {
                Id = agentId,
                ProjectId = projectId,
                Name = name,
                Status = AgentStatus.Active,
                State = AgentStore.Serialize(domain),
            });
            db.SaveChanges();
        }
        var store = new AgentJobStore(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentJobStore>.Instance);
        return new AgentJobReadTestDb(database, store, new AgentQuerier(factory), new AgentJobQuerier(factory));
    }

    public void Dispose() => _database.Dispose();
}

internal sealed class ReadAgentJobGrain : IAgentJobGrain
{
    private readonly AgentJobStatus _status;
    private readonly string? _projectId;
    private readonly AgentJobTerminalResult? _terminalResult;
    private readonly AgentExecutionDefinition? _executionDefinition;

    public ReadAgentJobGrain(
        AgentJobStatus status,
        string? projectId,
        AgentJobTerminalResult? terminalResult,
        AgentExecutionDefinition? executionDefinition = null)
    {
        _status = status;
        _projectId = projectId;
        _terminalResult = terminalResult;
        _executionDefinition = executionDefinition;
    }

    public int TerminalResultCalls { get; private set; }

    public Task<bool> IsWorkRunnableAsync(string runnerId, string workId) => Task.FromResult(false);
    public Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result) =>
        Task.FromResult(new AgentJobReportResult(false, "not-under-test"));
    public Task<AgentJobStatus> GetStatusAsync() => Task.FromResult(_status);
    public Task<string?> GetCurrentWorkIdAsync() => Task.FromResult<string?>(null);
    public Task AssignRunnerAsync(string runnerId, string workId) => Task.CompletedTask;
    public Task<bool> RecordRuntimeSessionBindingAsync(string runnerId, string workId, string sessionId, string runtimeSessionId) =>
        Task.FromResult(false);
    public Task SubmitAsync(AgentJobInput input) => Task.CompletedTask;
    public Task EnsureSubmittedAsync(AgentJobInput input) => Task.CompletedTask;
    public Task CheckTimeoutsAsync() => Task.CompletedTask;
    public Task<AgentJobTerminalResult> GetTerminalResultAsync()
    {
        TerminalResultCalls++;
        return Task.FromResult(_terminalResult ?? new AgentJobTerminalResult(_status, null, null, null, null, null));
    }
    public Task<AgentJobTerminalResult> WaitForTerminalAsync() => GetTerminalResultAsync();
    public Task<AgentJobRuntimeSnapshot> GetRuntimeSnapshotAsync() =>
        Task.FromResult(new AgentJobRuntimeSnapshot(_status, null, null, null, 0, false, false, _projectId, _executionDefinition));
    public Task<RoutedAgentLaunchPlan> EnsurePreparedAsync(RoutedAgentLaunchPlan plan) => Task.FromResult(plan);
    public Task AdvancePreparedLaunchAsync() => Task.CompletedTask;
    public Task MarkUnknownAsync(string reason) => Task.CompletedTask;
    public Task<AgentJobInput> PrepareManualLaunchAsync(PrepareManualLaunchCommand command) => Task.FromResult(new AgentJobInput(Prompt: command.Prompt, AgentId: command.AgentId, AgentSessionId: command.SessionId, InitialInputId: command.InputId, InitialTurnId: command.TurnId));
    public Task SubmitPreparedLaunchAsync() => Task.CompletedTask;
    public Task FailAsync(string reason, string? agentId = null) => Task.CompletedTask;
    public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;
}
