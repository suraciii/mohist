using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("MohistIntegration2")]
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
        var definition = new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition(stage,
                [new TaskDefinition(workId.Contains('.', StringComparison.Ordinal) ? workId[..workId.LastIndexOf('.')] : workId, title, "spec/task")],
                [])
        ]);
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: FixedNow,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
            })));
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
        var templateJson = global::System.Text.Json.JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions);
        var template = await db.ProjectWorkflowTemplates.FindAsync(projectId, definition.Id);
        if (template is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = definition.Id,
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
                DefaultTemplateId = definition.Id,
            });
        }
        else
        {
            profile.DefaultTemplateId = definition.Id;
            profile.UpdatedAt = FixedNow;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetRunners_GlobalRunnerWithoutProjectId_IsReturned()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-global-scope-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "global-scope-host",
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(global::System.Text.Json.JsonValueKind.Undefined, runner.ValueKind);
            Assert.Equal("global", runner.GetProperty("scope").GetProperty("type").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_GlobalRunner_ReturnsRunner()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-api-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "test-host",
            coderModels = new[] { "openai/gpt-4" },
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(global::System.Text.Json.JsonValueKind.Undefined, runner.ValueKind);
            Assert.Equal(runnerId, runner.GetProperty("id").GetString());
            Assert.Equal("external", runner.GetProperty("kind").GetString());
            Assert.Equal("test-host", runner.GetProperty("hostname").GetString());
            Assert.Equal("global", runner.GetProperty("scope").GetProperty("type").GetString());
            Assert.Equal("openai/gpt-4", runner.GetProperty("coderModels")[0].GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_RunnerRegisteringWithProjectId_IsReturnedAsGlobal()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-proj-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "proj-host",
            projectId,
            coderModels = new[] { "anthropic/claude-3" },
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(global::System.Text.Json.JsonValueKind.Undefined, runner.ValueKind);
            Assert.Equal(runnerId, runner.GetProperty("id").GetString());
            // Runners are global execution resources; the ProjectId field on
            // the request is preserved on the wire for runner-line
            // compatibility but does not bind the runner to a project.
            Assert.Equal("global", runner.GetProperty("scope").GetProperty("type").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
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
    public async Task GetRunners_BusyRunner_IncludesActiveWork()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-busy-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "busy-host",
            projectId,
        });

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflowId = $"wf-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "task-1.1", "task", "build", "Task 1");

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runnerView = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(global::System.Text.Json.JsonValueKind.Undefined, runnerView.ValueKind);
            Assert.Equal("busy", runnerView.GetProperty("status").GetString());
            var activeWorks = runnerView.GetProperty("activeWorks");
            Assert.Equal(global::System.Text.Json.JsonValueKind.Array, activeWorks.ValueKind);
            var firstActive = activeWorks.EnumerateArray().First();
            Assert.Equal(workflowId, firstActive.GetProperty("ownerId").GetString());
            Assert.Equal("workflow", firstActive.GetProperty("ownerKind").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_DisconnectedBusyWorkspaceRunner_IsBusyAndStillShowsConnectionDiagnostic()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-disc-busy-api-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*", "workspace-query" },
            hostname = "disc-busy-host",
            projectId,
        });

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.HeartbeatAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "task-1.1", "task", "build", "Task 1");

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runnerView = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(global::System.Text.Json.JsonValueKind.Undefined, runnerView.ValueKind);
            Assert.Equal("disconnected", runnerView.GetProperty("connectionState").GetString());
            Assert.Equal("busy", runnerView.GetProperty("status").GetString());
            var activeWorks = runnerView.GetProperty("activeWorks");
            Assert.Equal(global::System.Text.Json.JsonValueKind.Array, activeWorks.ValueKind);
            var firstActive = activeWorks.EnumerateArray().First();
            Assert.Equal(workflowId, firstActive.GetProperty("ownerId").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
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
    public async Task RegisterRunner_WithBuildGitHash_ExposesHashInStatus()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-hash-{Guid.NewGuid():N}";
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "hash-host",
            projectId,
            buildGitHash = hash,
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);

            Assert.NotEqual(global::System.Text.Json.JsonValueKind.Undefined, runner.ValueKind);
            Assert.True(runner.TryGetProperty("buildGitHash", out var reportedHash));
            Assert.Equal(hash, reportedHash.GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_BusyMultiSlotRunner_ListsEveryActiveWorkIndependently()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-multi-api-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "multi-host",
            projectId,
        });

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UpdateAsync(2);
        await runner.HeartbeatAsync();

        var workflowA = $"wf-multi-a-{Guid.NewGuid():N}";
        var workflowB = $"wf-multi-b-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowA, "task-a.1", "task", "build", "Task A", projectId);
        await AssignActiveWorkForTestAsync(runnerId, workflowB, "task-b.1", "task", "build", "Task B", projectId);

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runnerView = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(global::System.Text.Json.JsonValueKind.Undefined, runnerView.ValueKind);

            var activeWorks = runnerView.GetProperty("activeWorks");
            Assert.Equal(global::System.Text.Json.JsonValueKind.Array, activeWorks.ValueKind);
            Assert.Equal(2, activeWorks.GetArrayLength());

            var ownerIds = activeWorks.EnumerateArray().Select(w => w.GetProperty("ownerId").GetString()).ToArray();
            Assert.Contains(workflowA, ownerIds);
            Assert.Contains(workflowB, ownerIds);

            foreach (var work in activeWorks.EnumerateArray())
            {
                Assert.Equal("workflow", work.GetProperty("ownerKind").GetString());
                Assert.False(string.IsNullOrWhiteSpace(work.GetProperty("workId").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(work.GetProperty("workType").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(work.GetProperty("stage").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(work.GetProperty("title").GetString()));
            }
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_IdleRunner_HasEmptyActiveWorksArray()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-idle-api-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "idle-host",
            projectId,
        });
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.HeartbeatAsync();

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var runnerView = payload.GetProperty("data").GetProperty("runners").EnumerateArray()
                .First(r => r.GetProperty("id").GetString() == runnerId);
            Assert.Equal("idle", runnerView.GetProperty("status").GetString());
            var activeWorks = runnerView.GetProperty("activeWorks");
            Assert.Equal(global::System.Text.Json.JsonValueKind.Array, activeWorks.ValueKind);
            Assert.Equal(0, activeWorks.GetArrayLength());
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
    public async Task GetRunner_IdleRunner_Returns200WithEmptyActiveWorks()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-idle-detail-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "idle-detail-host",
            projectId,
        });
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.HeartbeatAsync();

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners/{runnerId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            var detail = payload.GetProperty("data").GetProperty("runner");

            Assert.Equal(runnerId, detail.GetProperty("id").GetString());
            Assert.Equal("idle", detail.GetProperty("status").GetString());
            var activeWorks = detail.GetProperty("activeWorks");
            Assert.Equal(global::System.Text.Json.JsonValueKind.Array, activeWorks.ValueKind);
            Assert.Equal(0, activeWorks.GetArrayLength());
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

    [Fact]
    public async Task GetRunner_RunnerWithOtherProjectId_Returns200()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-foreign-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "foreign-host",
            projectId = "different-project",
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners/{runnerId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var runner = payload.GetProperty("data").GetProperty("runner");
            Assert.Equal(runnerId, runner.GetProperty("id").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunner_IsReadOnly_NoDispatchHeartbeatOrUnregisterSideEffect()
    {
        var projectId = await CreateProjectIdAsync($"proj-{Guid.NewGuid():N}");

        var runnerId = $"runner-readonly-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "readonly-host",
            projectId,
        });

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.HeartbeatAsync();

        var workflowId = $"wf-readonly-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "work-readonly-1", "task", "build", "Readonly Task", projectId);

        try
        {
            var beforeRuntime = await runner.GetRuntimeStateAsync();
            var beforeInfo = await runner.GetInfoAsync();
            var beforeHeartbeatAt = beforeRuntime.LastHeartbeatAt;
            var beforeRegisteredAt = beforeInfo!.RegisteredAt;

            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners/{runnerId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var afterRuntime = await runner.GetRuntimeStateAsync();
            var afterInfo = await runner.GetInfoAsync();

            Assert.Single(afterRuntime.ActiveWorks);
            Assert.Equal(workflowId, afterRuntime.ActiveWorks[0].OwnerId);
            Assert.False(string.IsNullOrWhiteSpace(afterRuntime.ActiveWorks[0].WorkId));

            Assert.Equal(beforeHeartbeatAt, afterRuntime.LastHeartbeatAt);
            Assert.NotEqual(RunnerStatus.Offline, afterRuntime.Status);
            Assert.NotNull(afterInfo);
            Assert.Equal(beforeRegisteredAt, afterInfo.RegisteredAt);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
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
