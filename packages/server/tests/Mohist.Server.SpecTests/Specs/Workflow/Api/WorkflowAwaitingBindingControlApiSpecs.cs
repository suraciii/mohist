using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Api;

[Collection("IntegrationWorkflow")]
public sealed class WorkflowAwaitingBindingControlApiSpecs
{
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;

    public WorkflowAwaitingBindingControlApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Theory]
    [InlineData("retry")]
    [InlineData("rerun")]
    [InlineData("rerun-from-stage")]
    public async Task AwaitingBinding_RejectsRecoveryControlsAcrossRoutesAndDirectGrainCalls(string verb)
    {
        var (projectId, issueNumber, workflowRunId) = await SeedAwaitingBindingWorkflowAsync();
        var beforeState = JSON.Serialize(await LoadRunAsync(workflowRunId));
        var beforeEvents = await EventCountAsync(workflowRunId);

        var issueResponse = await PostControlAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/{verb}", verb);
        var workflowResponse = await PostControlAsync(
            $"/api/workflow-runs/{workflowRunId}/{verb}", verb);

        Assert.Equal(HttpStatusCode.Conflict, issueResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, workflowResponse.StatusCode);

        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RetryAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RerunAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RerunFromStageAsync("plan"));

        Assert.Equal(beforeState, JSON.Serialize(await LoadRunAsync(workflowRunId)));
        Assert.Equal(beforeEvents, await EventCountAsync(workflowRunId));

        var stopResponse = await _client.PostAsync($"/api/workflow-runs/{workflowRunId}/stop", content: null);

        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        Assert.Equal(WorkflowRunStatus.Stopped, (await LoadRunAsync(workflowRunId)).Status);
        Assert.Equal(beforeEvents + 1, await EventCountAsync(workflowRunId));
    }

    private async Task<(string ProjectId, int IssueNumber, string WorkflowRunId)> SeedAwaitingBindingWorkflowAsync()
    {
        var projectId = $"proj-awaiting-binding-{Guid.NewGuid():N}";
        var project = _grains.GetGrain<IProjectGrain>(projectId);
        await project.CreateAsync($"awaiting-binding-{Guid.NewGuid():N}");
        await project.AddRepositoryAsync("origin", "git@example.com:awaiting-binding.git", "main");

        var issueNumber = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue-awaiting-binding-{Guid.NewGuid():N}";
        await _grains.GetGrain<IIssueGrain>(issueId).CreateAsync(
            projectId, issueNumber, "Awaiting binding controls", null, null, null, null, issueId, isDraft: false);
        await SeedWorkflowTemplateAsync(projectId);

        var workflowRunId = await _grains.GetGrain<IIssueGrain>(issueId).StartWorkAsync();
        await ForceAwaitingBindingAsync(workflowRunId);
        return (projectId, issueNumber, workflowRunId);
    }

    private async Task<HttpResponseMessage> PostControlAsync(string path, string verb) =>
        verb == "rerun-from-stage"
            ? await _client.PostAsJsonAsync(path, new { stage = "plan" })
            : await _client.PostAsync(path, content: null);

    private async Task ForceAwaitingBindingAsync(string workflowRunId)
    {
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId).DeactivateForTestAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run '{workflowRunId}' was not stored");
        using var document = JsonDocument.Parse(row.State);
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText())
            ?? throw new InvalidOperationException($"Workflow run '{workflowRunId}' state was not stored");
        state["status"] = JsonSerializer.SerializeToElement("AwaitingBinding", JSON.Options);
        row.State = JsonSerializer.Serialize(state, JSON.Options);
        await db.SaveChangesAsync();

        await _grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);
    }

    private async Task SeedWorkflowTemplateAsync(string projectId)
    {
        var definition = new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
        ]);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        await using var db = new MohistDbContext(options);
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = definition.Id,
            Template = JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions),
        });
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = definition.Id,
        });
        await db.SaveChangesAsync();
    }

    private async Task<WorkflowRun> LoadRunAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        return await store.LoadAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run '{workflowRunId}' was not stored");
    }

    private async Task<int> EventCountAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        return (await store.ListAsync(workflowRunId)).Count;
    }
}
