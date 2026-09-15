using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Xunit;

namespace Mohist.Server.Tests.Runner.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
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
             ProjectId: projectId),
            VerificationCommand: "true"));
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
    public async Task GetRunners_GlobalInventoryIncludesDurableDefinitions()
    {
        var projectId = await CreateProjectIdAsync($"proj-empty-{Guid.NewGuid():N}");

        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var existingIds = await registry.ListRunnerIdsAsync();
        foreach (var id in existingIds)
            await registry.UnregisterAsync(id);

        var response = await _fixture.Client.GetAsync("/api/runners");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("ready", data.GetProperty("inventory").GetProperty("state").GetString());
        Assert.Equal(global::System.Text.Json.JsonValueKind.Array, data.GetProperty("runners").ValueKind);
    }

    [Fact]
    public async Task GetRunners_ProjectScopedRoute_IsRemoved()
    {
        var response = await _fixture.Client.GetAsync("/api/projects/does-not-exist/runners");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRunners_RunnerFields_UseRunnerTerminology()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-terms-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "terms-host",
            projectId,
            CoderModels: new[] { "openai/gpt-4" },
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);

        try
        {
            var response = await _fixture.Client.GetAsync("/api/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().Single(r => r.GetProperty("identity").GetProperty("id").GetString() == runnerId);

            Assert.Equal("ready", payload.GetProperty("data").GetProperty("inventory").GetProperty("state").GetString());
            Assert.Equal("terms-host", runner.GetProperty("identity").GetProperty("hostname").GetString());
            Assert.Equal("online", runner.GetProperty("presence").GetProperty("state").GetString());
            Assert.Equal("disconnected", runner.GetProperty("control").GetProperty("state").GetString());
            Assert.Contains("capabilities", runner.ToString());
            Assert.Contains("runtimes", runner.ToString());
            Assert.Contains("activeWorks", runner.ToString());
            Assert.DoesNotContain("coderModels", runner.ToString());
            Assert.DoesNotContain("idle", runner.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task GetRunner_BusyRunner_Returns200WithFullDetail()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-detail-{Guid.NewGuid():N}";
        var hash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "detail-host",
            projectId,
            CoderModels: new[] { "openai/gpt-4" },
            BuildGitHash: hash,
            Component: "mohist-runner",
            SourceRevision: hash,
            ReleaseId: "release-detail",
            Generation: 7,
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration),
            TestRunnerGenerationExtensions.ProcessGeneration);

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflowId = $"wf-detail-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "work-detail-1", "task", "build", "Detail Task", projectId);

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/runners/{runnerId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var detail = payload.GetProperty("data").GetProperty("runner");

            Assert.Equal(runnerId, detail.GetProperty("identity").GetProperty("id").GetString());
            Assert.Equal("external", detail.GetProperty("identity").GetProperty("kind").GetString());
            Assert.Equal("detail-host", detail.GetProperty("identity").GetProperty("hostname").GetString());
            Assert.Equal("mohist-runner", detail.GetProperty("identity").GetProperty("component").GetString());
            Assert.Equal(hash, detail.GetProperty("identity").GetProperty("sourceRevision").GetString());
            Assert.Equal("release-detail", detail.GetProperty("identity").GetProperty("releaseId").GetString());
            Assert.Equal(7, detail.GetProperty("identity").GetProperty("generation").GetInt64());
            Assert.Equal("online", detail.GetProperty("presence").GetProperty("state").GetString());
            Assert.Equal(1, detail.GetProperty("capacity").GetProperty("used").GetInt32());
            Assert.Equal(1, detail.GetProperty("capacity").GetProperty("total").GetInt32());
            Assert.DoesNotContain("buildGitHash", detail.ToString());
            Assert.DoesNotContain("coderModels", detail.ToString());

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
            await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
        }
    }

    [Fact]
    public async Task GetRunner_UnknownRunner_Returns404WithRunnerNotFoundReason()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var unknownRunnerId = $"runner-unknown-{Guid.NewGuid():N}";

        var response = await _fixture.Client.GetAsync($"/api/runners/{unknownRunnerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("runner_not_found", payload.GetProperty("code").GetString());
        Assert.Contains(unknownRunnerId, payload.GetProperty("error").GetString()!);
    }

    private async Task<string> CreateProjectIdAsync(string name)
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            name,
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return projectId;
    }
}
