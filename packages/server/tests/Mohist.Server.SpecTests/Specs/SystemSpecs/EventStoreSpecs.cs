using Microsoft.Extensions.DependencyInjection;
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
            time: TestTime.UtcNow,
            data: null,
            extensions: new Dictionary<string, string>
            {
                [EventCatalog.Lineage.ProjectId] = "proj",
                [EventCatalog.Lineage.WorkflowRunId] = workflowRunId,
            }));

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
            ["stage"] = "test",
        };
        await store.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            type: "com.mohist.workflow.task.completed",
            time: TestTime.UtcNow,
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
}
