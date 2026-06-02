using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class ApiContractSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public ApiContractSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("/api/projects/current")]
    [InlineData("/api/questions")]
    [InlineData("/api/questions/question-1")]
    [InlineData("/api/providers")]
    [InlineData("/api/providers/models")]
    [InlineData("/api/providers/runtime")]
    [InlineData("/api/issues/1/agent-session")]
    [InlineData("/api/agent/session-status")]
    public async Task RemovedLegacyApi_WhenRequested_ReturnsNotFound(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/questions/question-1/reply")]
    [InlineData("/api/questions/question-1/expire")]
    [InlineData("/api/providers/test")]
    [InlineData("/api/providers/custom-openai")]
    [InlineData("/api/settings/system/rebuild")]
    [InlineData("/api/issues/1/messages")]
    public async Task RemovedLegacyApiPost_WhenRequested_ReturnsNotFound(string path)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpencodeModels_ReturnsRunnerReportedModels()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"models-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var runnerId = $"model-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "test-host",
            projectId,
            coderModels = new[] { "zai/glm-5", "openai/gpt-5.5" },
        });
        try
        {
            var response = await _fixture.Client.GetDataAsync<OpencodeModelsDto>($"/api/opencode/models?projectId={projectId}");

            Assert.Contains("zai/glm-5", response.Models);
            Assert.Contains("openai/gpt-5.5", response.Models);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task AgentStatus_ReportsRegisteredRunnerWorkflowSlots()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"slots-{Guid.NewGuid():N}", path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        var runnerId = $"slot-runner-{Guid.NewGuid():N}";

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = Array.Empty<string>(),
                hostname = "test-host",
                projectId,
                maxWorkflowSlots = 4,
            });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/agent/status?projectId={projectId}");

            Assert.Equal(0, status.Capacity.Active);
            Assert.Equal(4, status.Capacity.Max);
            var runner = Assert.Single(status.Runners, r => r.Id == runnerId);
            Assert.Equal(0, runner.Active);
            Assert.Equal(4, runner.Max);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task ProjectApi_ResolvesProjectByNameOrId_AndUseReturnsProject()
    {
        var projectName = $"project-resolve-{Guid.NewGuid():N}";
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = projectName, path = "/tmp/project", baseBranch = "main" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;

        var byName = await _fixture.Client.GetDataAsync<ProjectDto>($"/api/projects/{projectName}");
        var byId = await _fixture.Client.GetDataAsync<ProjectDto>($"/api/projects/{projectId}");
        var useByName = await _fixture.Client.PostDataAsync<ProjectDto>($"/api/projects/{projectName}/use", new { });
        var useById = await _fixture.Client.PostDataAsync<ProjectDto>($"/api/projects/{projectId}/use", new { });

        Assert.Equal(projectId, byName.Id);
        Assert.Equal(projectName, byId.Name);
        Assert.Equal(projectId, useByName.Id);
        Assert.Equal(projectName, useById.Name);
    }

    [Fact]
    public async Task OpencodeModels_WhenProjectIdMissing_ReturnsGlobalRunnerModels()
    {
        var runnerId = $"global-model-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "test-host",
            coderModels = new[] { "openai/gpt-5.5" },
        });

        try
        {
            var response = await _fixture.Client.GetDataAsync<OpencodeModelsDto>("/api/opencode/models");

            Assert.Contains("openai/gpt-5.5", response.Models);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task IssueRebaseApi_QueuesWorkflowTask()
    {
        var projectName = $"proj-{Guid.NewGuid():N}";
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = projectName, path = "/tmp/project", baseBranch = "trunk" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString();
        var issueResponse = await _fixture.Client.PostAsJsonAsync("/api/issues", new { title = "Needs rebase", projectId });
        var issueJson = await issueResponse.Content.ReadFromJsonAsync<JsonElement>();
        var number = issueJson.GetProperty("data").GetProperty("number").GetInt32();

        await _fixture.Client.PostAsJsonAsync($"/api/issues/{number}/start?projectId={projectId}", new { });

        var runnerId = $"rebase-test-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "mohist/rebase", "spec/task", "spec/check" },
            hostname = "test-host",
            projectId,
        });

        try
        {
            var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(projectId!, number));
            var issueStatus = await issueGrain.GetWorkflowStatusAsync();
            var wrId = issueStatus!.WorkflowRunId!;

            var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(wrId);
            await workflow.AssignRunnerAsync(runnerId);
            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.PollAsync();

            using var response = await _fixture.Client.PostAsJsonAsync($"/api/issues/{number}/rebase?projectId={projectId}", new { });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            Assert.Equal("queued", data.GetProperty("status").GetString());
            Assert.Equal("trunk", data.GetProperty("baseBranch").GetString());
            Assert.StartsWith("rebase-", data.GetProperty("taskId").GetString());

            using var duplicate = await _fixture.Client.PostAsJsonAsync($"/api/issues/{number}/rebase?projectId={projectId}", new { });
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            var duplicatePayload = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("rebase_already_pending", duplicatePayload.GetProperty("code").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
            await _fixture.Client.PostAsync($"/api/issues/{number}/stop?projectId={projectId}", null);
        }
    }

    private sealed record OpencodeModelsDto(string[] Models);
    private sealed record ProjectDto(string Id, string Name);
    private sealed record AgentStatusDto(AgentCapacityDto Capacity, RunnerDto[] Runners);
    private sealed record AgentCapacityDto(int Active, int Max);
    private sealed record RunnerDto(string Id, string? Kind = null, int Active = 0, int Max = 0);
}
