using Mohist.Server.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[CollectionDefinition("WorkflowEvents", DisableParallelization = true)]
public class WorkflowEventsCollection;

[Collection("WorkflowEvents")]
public class WorkflowEventSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly WorkflowGrainFixture _fixture;

    public WorkflowEventSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void EventBus_DirectEmit_Works()
    {
        var received = new List<object>();
        _fixture.EventBus.On("test", data => received.Add(data));
        _fixture.EventBus.Emit("test", new { x = 1 });
        Assert.Single(received);
    }

    [Fact]
    public async Task WorkflowStart_EmitsStageChanged()
    {
        var received = new List<object>();
        Action<object> handler = data => received.Add(data);
        _fixture.EventBus.On("stage_changed", handler);

        try
        {
            var wf = _fixture.Grains.GetGrain<IWorkflowGrain>($"wf-{Guid.NewGuid():N}");
            await wf.StartAsync(MohistWorkflow.Definition);

            Assert.Single(received);
            var json = System.Text.Json.JsonSerializer.Serialize(received[0]);
            Assert.Contains("\"action\":\"started\"", json);
            Assert.Contains("\"stage\":\"plan\"", json);
        }
        finally
        {
            _fixture.EventBus.Off("stage_changed", handler);
        }
    }

    [Fact]
    public async Task WorkflowPause_EmitsStageChanged()
    {
        var received = new List<object>();
        Action<object> handler = data => received.Add(data);
        _fixture.EventBus.On("stage_changed", handler);

        try
        {
            var wf = _fixture.Grains.GetGrain<IWorkflowGrain>($"wf-{Guid.NewGuid():N}");
            await wf.StartAsync(MohistWorkflow.Definition);
            received.Clear();

            await wf.PauseAsync("user-requested");

            Assert.Single(received);
            var json = System.Text.Json.JsonSerializer.Serialize(received[0]);
            Assert.Contains("\"action\":\"paused\"", json);
            Assert.Contains("\"reason\":\"user-requested\"", json);
        }
        finally
        {
            _fixture.EventBus.Off("stage_changed", handler);
        }
    }

    [Fact]
    public async Task WorkflowResume_EmitsStageChanged()
    {
        var received = new List<object>();
        Action<object> handler = data => received.Add(data);
        _fixture.EventBus.On("stage_changed", handler);

        try
        {
            var wf = _fixture.Grains.GetGrain<IWorkflowGrain>($"wf-{Guid.NewGuid():N}");
            await wf.StartAsync(MohistWorkflow.Definition);
            await wf.PauseAsync();
            received.Clear();

            await wf.ResumeAsync();

            Assert.Single(received);
            var json = System.Text.Json.JsonSerializer.Serialize(received[0]);
            Assert.Contains("\"action\":\"resumed\"", json);
        }
        finally
        {
            _fixture.EventBus.Off("stage_changed", handler);
        }
    }
}
