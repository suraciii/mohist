using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

[Collection("MohistDb")]
public class EventStoreTests
{
    private readonly MohistDbFixture _fixture;

    public EventStoreTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

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
            time: DateTimeOffset.UnixEpoch,
            data: null));

        var events = await store.ListAsync(workflowRunId);
        var first = Assert.Single(events);
        Assert.Equal("com.mohist.workflow.run.started", first.Envelope.Type);
        Assert.Equal(source, first.Envelope.Source);
    }

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
            time: DateTimeOffset.UnixEpoch,
            data: null,
            subject: "42",
            extensions: extensions));

        var events = await store.ListAsync(workflowRunId);
        var e = Assert.Single(events);
        Assert.Equal("com.mohist.workflow.task.completed", e.Envelope.Type);
        Assert.Equal("42", e.Envelope.Subject);
        Assert.Equal("proj", e.Envelope.Extensions["projectid"]);
    }

    [Fact]
    public async Task ListAsync_EmptyForUnknownWorkflowRun()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var events = await store.ListAsync("nonexistent");
        Assert.Empty(events);
    }
}
