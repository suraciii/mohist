using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Tests.Support;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistDb")]
public class EventStoreSpecs
{
    private readonly MohistDbFixture _fixture;

    public EventStoreSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AppendWorkflowEventAsync_StoresMinimalDomainEventRow()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var workflowRunId = $"wr_{Guid.NewGuid():N}";

        await store.AppendWorkflowEventAsync(workflowRunId, new WorkflowRunStarted());
        await store.AppendWorkflowEventAsync(workflowRunId, new StageStarted("plan"));

        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.Source == $"/workflow-runs/{workflowRunId}")
            .OrderBy(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.Source,
                e.Data,
                e.Time,
                Type = EF.Property<string>(e, "Type"),
                SpecVersion = EF.Property<string>(e, "SpecVersion"),
            })
            .ToListAsync();

        Assert.Collection(rows,
            first =>
            {
                Assert.Equal(1, first.Id);
                Assert.Equal(nameof(WorkflowRunStarted), first.Type);
                Assert.Equal("1.0", first.SpecVersion);
                Assert.Equal(JsonValueKind.Object, first.Data.ValueKind);
                Assert.Empty(first.Data.EnumerateObject());
            },
            second =>
            {
                Assert.Equal(2, second.Id);
                Assert.Equal($"/workflow-runs/{workflowRunId}", second.Source);
                Assert.Equal(nameof(StageStarted), second.Type);
                Assert.Equal("1.0", second.SpecVersion);
                Assert.True(second.Time > DateTime.UtcNow.AddMinutes(-1));

                Assert.Equal("plan", second.Data.GetProperty("stage").GetString());
            });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task ListWorkflowEventsAsync_ProjectsDomainEventsFromPayload()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var workflowRunId = $"wr_{Guid.NewGuid():N}";

        await store.AppendWorkflowEventAsync(
            workflowRunId, new TaskCompleted("build", "task.1"));

        var events = await store.ListWorkflowEventsAsync(workflowRunId);

        var e = Assert.Single(events);
        Assert.Equal(nameof(TaskCompleted), e.Type);
        Assert.Equal($"/workflow-runs/{workflowRunId}", e.Source);
        var payload = Assert.IsType<TaskCompleted>(e.Data.Value);
        Assert.Equal("build", payload.Stage);
        Assert.Equal("task.1", payload.TaskId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentSessionStore_StoresSessionStateAndDomainEventsInOneCommit()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var session = CreateAgentSession();

        var events = session.AttachAgent("runtime-session-1", "codex-high", "/work", null, null, DateTime.UtcNow);
        await store.SaveAsync(session.Id, session, events);

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.AgentSessions.AsNoTracking().SingleAsync(e => e.Id == session.Id);
        Assert.Equal("runtime-session-1", row.AgentSessionId);
        Assert.Equal("running", row.Status);

        var rows = await db.Events.AsNoTracking()
            .Where(e => e.Source == $"/agent-sessions/{session.Id}")
            .OrderBy(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.Source,
                e.Data,
                Type = EF.Property<string>(e, "Type"),
                SpecVersion = EF.Property<string>(e, "SpecVersion"),
            })
            .ToListAsync();

        Assert.Collection(rows,
            first =>
            {
                Assert.Equal(1, first.Id);
                Assert.Equal(nameof(AgentSessionStarted), first.Type);
                Assert.Equal("1.0", first.SpecVersion);
                Assert.Equal("runtime-session-1", first.Data.GetProperty("agentRuntimeSessionId").GetString());
            },
            second =>
            {
                Assert.Equal(2, second.Id);
                Assert.Equal($"/agent-sessions/{session.Id}", second.Source);
                Assert.Equal(nameof(AgentSessionModelChanged), second.Type);
                Assert.Equal("codex-high", second.Data.GetProperty("model").GetString());
            });
    }

    private static AgentSession CreateAgentSession()
    {
        var metadata = new AgentSessionMetadata()
            .WithLabel(AgentSessionMetadataKeys.ProjectId, "proj")
            .WithLabel(AgentSessionMetadataKeys.IssueNumber, "1")
            .WithLabel(AgentSessionMetadataKeys.SourceKind, AgentSessionKey.Workflow)
            .WithLabel(AgentSessionMetadataKeys.SourceId, "wf")
            .WithLabel(AgentSessionMetadataKeys.SessionName, "plan");

        return AgentSession.Create(
            $"proj/wf/plan-{Guid.NewGuid():N}",
            "runner-1",
            "opencode",
            "/work",
            metadata: metadata,
            now: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
    }

}
