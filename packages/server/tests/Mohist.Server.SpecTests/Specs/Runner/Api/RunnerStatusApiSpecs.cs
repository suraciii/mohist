using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("PlatformIntegration")]
public class RunnerStatusApiSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    private readonly MohistIntegrationFixture _fixture;

    public RunnerStatusApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task AssignActiveWorkForTestAsync(
        string runnerId,
        string workflowId,
        string workId,
        string workType,
        string stage,
        string title,
        string projectId = "test-project")
    {
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        var definition = new WorkflowDefinition(
        [
            new StageDefinition(stage,
                [new TaskDefinition(workId.Contains('.', StringComparison.Ordinal) ? workId[..workId.LastIndexOf('.')] : workId, title, "spec/task")],
                [])
        ]);
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: FixedNow,
             ProjectId: projectId)));
        await workflow.AssignWorkerAsync(runnerId);

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.NotNull(await runner.PollAsync(_fixture.Services));
    }

    private async Task SeedWorkflowTemplateAsync(string workflowId, WorkflowDefinition definition, string projectId = "test-project")
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        const string templateId = "spec/workflow";
        var templateJson = WorkflowGrainTestHelpers.SerializeProfile(definition);
        var template = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        if (template is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = templateId,
                Template = templateJson,
            });
        }
        else
        {
            template.Template = templateJson;
            template.UpdatedAt = FixedNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync("test-project");
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "test-project",
                DefaultTemplateId = templateId,
            });
        }
        else
        {
            profile.DefaultTemplateId = templateId;
            profile.UpdatedAt = FixedNow;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetRunners_NoRunnersForProject_ReturnsEmptyList()
    {
        var projectId = await CreateProjectIdAsync($"proj-empty-{Guid.NewGuid():N}");

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var existingIds = await registry.ListRunnerIdsAsync();
        foreach (var id in existingIds)
            await registry.UnregisterAsync(id);

        var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        var runners = payload.GetProperty("data").GetProperty("runners");
        Assert.Empty(runners.EnumerateArray());
    }

    [Fact]
    public async Task GetRunners_OnLegacyRoute_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/api/runners");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRunners_RunnerFields_UseRunnerTerminology()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-terms-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "terms-host",
            projectId,
            coderModels = new[] { "openai/gpt-4" },
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);

            // Runners are global execution resources; the ProjectId field on
            // the registration request is preserved on the wire for
            // runner-line compatibility but does not bind the runner.
            Assert.Equal("global", runner.GetProperty("scope").GetProperty("type").GetString());
            Assert.Contains("connectionState", runner.ToString());
            Assert.Contains("lastHeartbeatAt", runner.ToString());
            Assert.Contains("capabilities", runner.ToString());
            Assert.Contains("coderModels", runner.ToString());
            Assert.Contains("activeWorks", runner.ToString());

            Assert.DoesNotContain(runner.ToString(), "agent");
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunner_BusyRunner_Returns200WithFullDetail()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-detail-{Guid.NewGuid():N}";
        var hash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "detail-host",
            projectId,
            coderModels = new[] { "openai/gpt-4" },
            buildGitHash = hash,
        });

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflowId = $"wf-detail-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "work-detail-1", "task", "build", "Detail Task", projectId);

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners/{runnerId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var detail = payload.GetProperty("data").GetProperty("runner");

            Assert.Equal(runnerId, detail.GetProperty("id").GetString());
            Assert.Equal("external", detail.GetProperty("kind").GetString());
            Assert.Equal("detail-host", detail.GetProperty("hostname").GetString());
            // Runners are global execution resources; the ProjectId field on
            // the registration request is preserved on the wire but does not
            // bind the runner to a project.
            Assert.Equal("global", detail.GetProperty("scope").GetProperty("type").GetString());
            Assert.Equal(hash, detail.GetProperty("buildGitHash").GetString());
            Assert.Equal("busy", detail.GetProperty("status").GetString());
            Assert.Equal("openai/gpt-4", detail.GetProperty("coderModels")[0].GetString());

            var activeWorks = detail.GetProperty("activeWorks");
            Assert.Equal(global::System.Text.Json.JsonValueKind.Array, activeWorks.ValueKind);
            var first = activeWorks.EnumerateArray().Single();
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("workId").GetString()));
            Assert.Equal("workflow", first.GetProperty("ownerKind").GetString());
            Assert.Equal(workflowId, first.GetProperty("ownerId").GetString());
            Assert.Equal("task", first.GetProperty("workType").GetString());
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("stage").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("title").GetString()));

        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunner_UnknownRunner_Returns404WithRunnerNotFoundReason()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var unknownRunnerId = $"runner-unknown-{Guid.NewGuid():N}";

        var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners/{unknownRunnerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("runner_not_found", payload.GetProperty("code").GetString());
        Assert.Contains(unknownRunnerId, payload.GetProperty("error").GetString()!);
    }

    private async Task<string> CreateProjectIdAsync(string name)
    {
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<global::System.Text.Json.JsonElement>(
            "/api/projects",
            name,
            gitUrl: $"file://{Guid.NewGuid():N}");
        return project.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Project response did not include an id");
    }
}
