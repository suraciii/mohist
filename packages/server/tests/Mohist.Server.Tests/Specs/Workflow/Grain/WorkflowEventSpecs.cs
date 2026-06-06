using Mohist.Server.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

[Collection("WorkflowEvents")]
public class WorkflowEventSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly WorkflowGrainFixture _fixture;

    public WorkflowEventSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    private static WorkflowStartInput TestInput() =>
        new(Variables: """{"project":{"id":"test-project"}}""");

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void EventBus_DirectEmit_Works()
    {
        var received = new List<object>();
        _fixture.EventBus.On("test", data => received.Add(data));
        _fixture.EventBus.Emit("test", new { x = 1 });
        Assert.Single(received);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStart_EmitsStageChanged()
    {
        var received = new List<object>();
        Action<object> handler = data => received.Add(data);
        _fixture.EventBus.On("stage_changed", handler);

        try
        {
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var wf = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, MohistWorkflow.Definition);
            await wf.StartAsync(TestInput());

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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowPause_EmitsStageChanged()
    {
        var received = new List<object>();
        Action<object> handler = data => received.Add(data);
        _fixture.EventBus.On("stage_changed", handler);

        try
        {
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var wf = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, MohistWorkflow.Definition);
            await wf.StartAsync(TestInput());
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowResume_EmitsStageChanged()
    {
        var received = new List<object>();
        Action<object> handler = data => received.Add(data);
        _fixture.EventBus.On("stage_changed", handler);

        try
        {
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var wf = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, MohistWorkflow.Definition);
            await wf.StartAsync(TestInput());
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

    private async Task SeedWorkflowTemplateAsync(string workflowId, Mohist.Server.Workflow.Domain.Definition.WorkflowDefinition definition)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var templateJson = System.Text.Json.JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions);
        var template = await db.ProjectWorkflowTemplates.FindAsync("test-project", definition.Id);
        if (template is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = "test-project",
                TemplateId = definition.Id,
                Template = templateJson,
            });
        }
        else
        {
            template.Template = templateJson;
            template.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync("test-project");
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "test-project",
                DefaultTemplateId = definition.Id,
            });
        }
        else
        {
            profile.DefaultTemplateId = definition.Id;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }
}
