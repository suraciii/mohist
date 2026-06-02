using System.Net;
using System.Net.Http.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Projection;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class RunnerStatusApiSpecs
{
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
        string title)
    {
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition(stage,
                [new TaskDefinition("task-1", title, "spec/task")],
                [])
        ]), new WorkflowStartInput(Variables: """{"project":{"id":"test-project"}}"""));
        await workflow.AssignRunnerAsync(runnerId);

        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.NotNull(await runner.PollAsync());
    }

    [Fact]
    public async Task GetRunners_NoProjectScopedRunners_ReturnsOnlyGlobalRunners()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"proj-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var response = await _fixture.Client.GetAsync($"/api/runners?projectId={projectId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());
        var runners = payload.GetProperty("data").GetProperty("runners");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, runners.ValueKind);
        Assert.All(runners.EnumerateArray(), r => Assert.Equal("global", r.GetProperty("scope").GetProperty("type").GetString()));
    }

    [Fact]
    public async Task GetRunners_GlobalRunner_ReturnsRunner()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"proj-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var runnerId = $"runner-api-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "test-host",
            coderModels = new[] { "openai/gpt-4" },
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/runners?projectId={projectId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, runner.ValueKind);
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
    public async Task GetRunners_ProjectRunner_ReturnsRunner()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"proj-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

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
            var response = await _fixture.Client.GetAsync($"/api/runners?projectId={projectId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, runner.ValueKind);
            Assert.Equal(runnerId, runner.GetProperty("id").GetString());
            Assert.Equal("project", runner.GetProperty("scope").GetProperty("type").GetString());
            Assert.Equal(projectId, runner.GetProperty("scope").GetProperty("projectId").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_NoRunnersForProject_ReturnsEmptyList()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"proj-empty-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var runnerId = $"runner-unrelated-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "other-host",
            projectId = "different-project",
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/runners?projectId={projectId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            Assert.DoesNotContain(runners.EnumerateArray(), r => r.GetProperty("id").GetString() == runnerId);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_MissingProjectId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/api/runners");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetRunners_BusyRunner_IncludesActiveWork()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"proj-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

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
            var response = await _fixture.Client.GetAsync($"/api/runners?projectId={projectId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runnerView = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, runnerView.ValueKind);
            Assert.Equal("busy", runnerView.GetProperty("status").GetString());
            var activeWork = runnerView.GetProperty("activeWork");
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, activeWork.ValueKind);
            Assert.Equal(workflowId, activeWork.GetProperty("workflowRunId").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_DisconnectedBusyWorkspaceRunner_IsBusyAndStillShowsConnectionDiagnostic()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"proj-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

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
            var response = await _fixture.Client.GetAsync($"/api/runners?projectId={projectId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runnerView = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, runnerView.ValueKind);
            Assert.Equal("disconnected", runnerView.GetProperty("connectionState").GetString());
            Assert.Equal("busy", runnerView.GetProperty("status").GetString());
            var activeWork = runnerView.GetProperty("activeWork");
            Assert.NotEqual(System.Text.Json.JsonValueKind.Null, activeWork.ValueKind);
            Assert.Equal(workflowId, activeWork.GetProperty("workflowRunId").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task GetRunners_RunnerFields_UseRunnerTerminology()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"proj-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

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
            var response = await _fixture.Client.GetAsync($"/api/runners?projectId={projectId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);

            Assert.Equal("project", runner.GetProperty("scope").GetProperty("type").GetString());
            Assert.Contains("connectionState", runner.ToString());
            Assert.Contains("lastHeartbeatAt", runner.ToString());
            Assert.Contains("capabilities", runner.ToString());
            Assert.Contains("coderModels", runner.ToString());
            Assert.Contains("activeWork", runner.ToString());

            Assert.DoesNotContain(runner.ToString(), "agent");
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}
