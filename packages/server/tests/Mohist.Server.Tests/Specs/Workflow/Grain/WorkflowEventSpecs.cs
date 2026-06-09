using CloudNative.CloudEvents;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain.Definition;
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
    public async Task WorkflowStart_EmitsStageChanged()
    {
        var received = new List<CloudEvent>();
        _fixture.EventBus.Subscribe("stage_changed", evt => { received.Add(evt); return Task.CompletedTask; });

        var workflowId = $"wf-{Guid.NewGuid():N}";
        var wf = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, MohistWorkflow.Definition);

        await wf.StartAsync(TestInput());

        Assert.NotEmpty(received);
        Assert.Contains(received, e =>
        {
            if (e.Type != "stage_changed") return false;
            var json = (e.Data as System.Text.Json.JsonElement?)?.GetRawText() ?? "";
            return json.Contains("\"action\":\"started\"") && json.Contains("\"stage\":\"plan\"");
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowPause_EmitsStageChanged()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        var wf = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, MohistWorkflow.Definition);
        await wf.StartAsync(TestInput());

        var received = new List<CloudEvent>();
        _fixture.EventBus.Subscribe("stage_changed", evt => { received.Add(evt); return Task.CompletedTask; });

        await wf.PauseAsync("user-requested");

        Assert.NotEmpty(received);
        Assert.Contains(received, e =>
        {
            if (e.Type != "stage_changed") return false;
            var json = (e.Data as System.Text.Json.JsonElement?)?.GetRawText() ?? "";
            return json.Contains("\"action\":\"paused\"") && json.Contains("\"reason\":\"user-requested\"");
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowResume_EmitsStageChanged()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        var wf = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, MohistWorkflow.Definition);
        await wf.StartAsync(TestInput());
        await wf.PauseAsync();

        var received = new List<CloudEvent>();
        _fixture.EventBus.Subscribe("stage_changed", evt => { received.Add(evt); return Task.CompletedTask; });

        await wf.ResumeAsync();

        Assert.NotEmpty(received);
        Assert.Contains(received, e =>
        {
            if (e.Type != "stage_changed") return false;
            var json = (e.Data as System.Text.Json.JsonElement?)?.GetRawText() ?? "";
            return json.Contains("\"action\":\"resumed\"");
        });
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
