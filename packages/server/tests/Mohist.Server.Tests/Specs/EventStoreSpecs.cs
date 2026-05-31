using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
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
    public async Task AppendAsync_ListIssueEvents_ReturnsOrderedEventsWithPayload()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var projectId = $"project-{Guid.NewGuid():N}";

        await store.AppendAsync(new EventInput(projectId, 1, "issue", "issue_created", Status: "created", Payload: new { title = "A" }));
        await store.AppendAsync(new EventInput(projectId, 1, "workflow", "workflow_started", WorkflowRunId: "wr_test_1", Status: "started"));
        await store.AppendAsync(new EventInput(projectId, 2, "issue", "issue_created", Status: "created"));

        var events = await store.ListIssueEventsAsync(projectId, 1);

        Assert.Equal(["issue_created", "workflow_started"], events.Select(e => e.Type).ToArray());
        Assert.NotNull(events[0].Payload);
    }

    [Fact]
    public async Task ListRecentAsync_IsolatesProjects()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var projectA = $"project-{Guid.NewGuid():N}";
        var projectB = $"project-{Guid.NewGuid():N}";

        await store.AppendAsync(new EventInput(projectA, 1, "issue", "issue_created"));
        await store.AppendAsync(new EventInput(projectB, 1, "issue", "issue_created"));

        var events = await store.ListRecentAsync(projectA);

        Assert.All(events, e => Assert.Equal(projectA, e.ProjectId));
    }
}
