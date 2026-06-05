using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class EventStoreSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public EventStoreSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

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

}
