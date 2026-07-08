using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

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
    public async Task AppendAsync_StoresEnvelope()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var source = new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative);

        await store.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: source,
            type: "com.mohist.workflow.run.started",
            time: DateTimeOffset.UtcNow,
            data: null));

        var events = await store.ListAsync(workflowRunId);
        var first = Assert.Single(events);
        Assert.Equal("com.mohist.workflow.run.started", first.Envelope.Type);
        Assert.Equal(source, first.Envelope.Source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task ListAsync_RoundtripsEnvelopeWithExtensions()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var workflowRunId = $"wr_{Guid.NewGuid():N}";

        var extensions = new Dictionary<string, string>
        {
            ["projectid"] = "proj",
            ["workflowrunid"] = workflowRunId,
        };
        await store.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: "com.mohist.workflow.task.completed",
            time: DateTimeOffset.UtcNow,
            data: null,
            subject: "42",
            extensions: extensions));

        var events = await store.ListAsync(workflowRunId);
        var e = Assert.Single(events);
        Assert.Equal("com.mohist.workflow.task.completed", e.Envelope.Type);
        Assert.Equal("42", e.Envelope.Subject);
        Assert.Equal("proj", e.Envelope.Extensions["projectid"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task ListAsync_EmptyForUnknownWorkflowRun()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var events = await store.ListAsync("nonexistent");
        Assert.Empty(events);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AppendAsync_LeavesDispatchedAtNull_AfterMigrate()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var workflowRunId = $"wr_{Guid.NewGuid():N}";

        await store.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: "com.mohist.workflow.run.started",
            time: DateTimeOffset.Parse("2026-07-08T00:00:00Z"),
            data: null));

        await using var db = await dbFactory.CreateDbContextAsync();
        var row = Assert.Single(await db.WorkflowRunEvents
            .AsNoTracking()
            .Where(e => e.Source == $"/mohist/workflow-runs/{workflowRunId}")
            .ToListAsync());
        Assert.Null(row.DispatchedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task MarkDispatchedAsync_SetsOnlyMatchedRow_AfterMigrate()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var markAt = DateTimeOffset.Parse("2026-07-08T01:00:00Z");

        await AppendEventAsync(store, "/mohist/workflow-runs/wr_migrate_a", "com.mohist.workflow.run.started", "2026-07-08T00:00:00Z");
        await AppendEventAsync(store, "/mohist/workflow-runs/wr_migrate_a", "com.mohist.workflow.run.progressed", "2026-07-08T00:01:00Z");
        await AppendEventAsync(store, "/mohist/issues/issue_migrate_b", "com.mohist.issue.created", "2026-07-08T00:02:00Z");
        await AppendEventAsync(store, "/mohist/epics/epic_migrate_c", "com.mohist.epic.created", "2026-07-08T00:03:00Z");

        await store.MarkDispatchedAsync("/mohist/workflow-runs/wr_migrate_a", 1, markAt);

        await using var db = await dbFactory.CreateDbContextAsync();
        var wrRows = await db.WorkflowRunEvents.AsNoTracking()
            .Where(e => e.Source == "/mohist/workflow-runs/wr_migrate_a")
            .OrderBy(e => e.Id)
            .ToListAsync();
        var issueRow = await db.IssueEvents.AsNoTracking().SingleAsync(e => e.Source == "/mohist/issues/issue_migrate_b");
        var epicRow = await db.EpicEvents.AsNoTracking().SingleAsync(e => e.Source == "/mohist/epics/epic_migrate_c");

        Assert.Equal(markAt, wrRows[0].DispatchedAt);
        Assert.Null(wrRows[1].DispatchedAt);
        Assert.Null(issueRow.DispatchedAt);
        Assert.Null(epicRow.DispatchedAt);
    }

    private static Task AppendEventAsync(IEventStore store, string source, string type, string time) =>
        store.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.Parse(time),
            data: null));
}
