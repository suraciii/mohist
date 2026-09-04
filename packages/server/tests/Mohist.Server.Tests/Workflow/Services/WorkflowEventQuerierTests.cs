using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Services;

[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowEventQuerierTests
{
    private readonly MohistDbFixture _fixture;

    public WorkflowEventQuerierTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListWorkflowEvents_HidesInvalidatedTaskOutcomesAndRetainsRestart()
    {
        using var scope = _fixture.Services.CreateScope();
        var runId = await SeedRerunHistoryAsync(scope.ServiceProvider);

        var events = await scope.ServiceProvider
            .GetRequiredService<WorkflowEventQuerier>()
            .ListWorkflowEventsAsync(runId, limit: 200);

        Assert.DoesNotContain(events, item => item.Envelope.Id == "old-build-completed");
        Assert.DoesNotContain(events, item => item.Envelope.Id == "old-build-failed");
        Assert.Contains(events, item => item.Envelope.Id == "restarted-build");
        Assert.Single(events, item =>
            item.Envelope.Type == EventCatalog.ReverseDns.StageStarted
            && item.Envelope.Data!.Value.GetProperty("stage").GetString() == "build");
        Assert.Contains(events, item => item.Envelope.Id == "new-build-completed");
    }

    [Fact]
    public async Task ListWorkflowEvents_FiltersInvalidatedHistoryBeforeApplyingLimit()
    {
        using var scope = _fixture.Services.CreateScope();
        var runId = await SeedRerunHistoryAsync(scope.ServiceProvider);

        var events = await scope.ServiceProvider
            .GetRequiredService<WorkflowEventQuerier>()
            .ListWorkflowEventsAsync(runId, limit: 4);

        Assert.Equal(
            ["plan-completed", "restarted-build", "new-build-started", "new-build-completed"],
            events.Select(item => item.Envelope.Id));
    }

    private async Task<string> SeedRerunHistoryAsync(IServiceProvider services)
    {
        var runId = $"wr-event-rerun-{Guid.NewGuid():N}";
        var now = _fixture.TimeProvider.GetUtcNow();
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("plan", [], []),
            new StageDefinition("build", [], []),
        ]);
        var run = WorkflowRun.Create(runId, definition, now);
        await services.GetRequiredService<IWorkflowRunStore>().SaveAsync(run);

        var eventStore = services.GetRequiredService<IEventStore>();
        var dbFactory = services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        for (var index = 0; index < RerunHistory.Length; index++)
        {
            var specification = RerunHistory[index];
            await eventStore.AppendAsync(db, Event(runId, specification, now.AddSeconds(index)));
        }
        await db.SaveChangesAsync();

        return runId;
    }

    private static CloudEvent Event(
        string runId,
        EventSpecification specification,
        DateTimeOffset time) =>
        new(
            specification.Id,
            new Uri(WorkflowRunEventPersistence.WorkflowRunSource(runId), UriKind.Relative),
            WorkflowEventSerializer.BusType(specification.Event),
            time,
            WorkflowEventSerializer.ToData(specification.Event));

    private static readonly EventSpecification[] RerunHistory =
    [
        new("plan-started", new StageStarted("plan")),
        new("plan-completed", new TaskCompleted("plan", "draft.1")),
        new("old-build-started", new StageStarted("build")),
        new("old-build-completed", new TaskCompleted("build", "compile.1")),
        new("old-build-failed", new TaskFailed("build", "compile.1", "compile failed")),
        new("restarted-build", new StageStarted("build")),
        new("new-build-started", new TaskStarted("build", "compile.s2.1", "runner-1")),
        new("new-build-completed", new TaskCompleted("build", "compile.s2.1")),
    ];

    private sealed record EventSpecification(string Id, WorkflowEvent Event);
}
