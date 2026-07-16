using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("MohistIntegration")]
public class ApiContractSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public ApiContractSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Theory]
    [InlineData("/api/projects/current")]
    [InlineData("/api/questions")]
    [InlineData("/api/questions/question-1")]
    [InlineData("/api/providers")]
    [InlineData("/api/providers/models")]
    [InlineData("/api/providers/runtime")]
    [InlineData("/api/issues/1/agent-session")]
    [InlineData("/api/agent/session-status")]
    [InlineData("/api/issues/1/coder-sessions/session-1?projectId=p1")]
    [InlineData("/api/issues/1/workflow/sessions/plan?projectId=p1")]
    public async Task RemovedLegacyApi_WhenRequested_ReturnsNotFound(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task OpencodeModels_ReturnsRunnerReportedModels()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"models-{Guid.NewGuid():N}", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

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
            var response = await _fixture.Client.GetDataAsync<OpencodeModelsDto>($"/api/projects/{projectId}/opencode/models");

            Assert.Contains("zai/glm-5", response.Models);
            Assert.Contains("openai/gpt-5.5", response.Models);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task AgentStatus_ReportsRegisteredRunnerWorkflowSlots()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"slots-{Guid.NewGuid():N}", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        // Capacity.Max is summed across all currently-registered global
        // runners, so we need a clean registry to assert against this
        // runner's contribution in isolation. Drain anything left over
        // from prior tests in this collection.
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);

        var runnerId = $"slot-runner-{Guid.NewGuid():N}";

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = Array.Empty<string>(),
                hostname = "test-host",
                projectId,
            });

            // Capacity is sourced from the persisted definition state and
            // only mutates through PATCH /api/runner/{runnerId}. This test
            // exercises the PATCH path: register defaults the runner to 1
            // slot, then PATCH bumps it to 4 and the agent status view
            // reflects it.
            await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 4 });

            var status = await _fixture.Client.GetDataAsync<AgentStatusDto>($"/api/projects/{projectId}/agent/status");

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task ProjectApi_ResolvesProjectByNameOrId_AndUseReturnsProject()
    {
        var projectName = UniqueProjectName("resolve");
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = projectName, repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var byName = await _fixture.Client.GetDataAsync<ProjectDto>($"/api/projects/{projectName}");
        var byId = await _fixture.Client.GetDataAsync<ProjectDto>($"/api/projects/{projectId}");
        var useByName = await _fixture.Client.PostDataAsync<ProjectDto>($"/api/projects/{projectName}/use", new { });
        var useById = await _fixture.Client.PostDataAsync<ProjectDto>($"/api/projects/{projectId}/use", new { });

        Assert.Equal(projectId, byName.Id);
        Assert.Equal(projectName, byId.Name);
        Assert.Equal(projectId, useByName.Id);
        Assert.Equal(projectName, useById.Name);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task ProjectApi_CreatesDnsProjectName_AndRejectsInvalidName()
    {
        var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = "Dns-Project", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadFromJsonAsync<JsonElement>();
        var responseId = responseJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{responseId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        Assert.Equal("dns-project", responseJson.GetProperty("data").GetProperty("name").GetString());

        using var invalid = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = "Bad Project", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidPayload = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_project_name", invalidPayload.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task ProjectScopedApis_AcceptProjectNameAsProjectRef()
    {
        var projectName = UniqueProjectName("scope");
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            projectName,
            repoName: "main",
            gitUrl: $"file://{Guid.NewGuid():N}");

        var repos = await _fixture.Client.GetDataAsync<RepositoryDto[]>($"/api/projects/{project.Name}/repositories");
        Assert.Single(repos);

        var issue = await _fixture.Client.PostDataAsync<IssueDto>($"/api/projects/{project.Name }/issues", new { title = "Project name scoped issue", projectId = project.Id });
        var issueByName = await _fixture.Client.GetDataAsync<IssueDto>($"/api/projects/{project.Name}/issues/{issue.Number}");

        Assert.Equal(issue.Number, issueByName.Number);
        Assert.Equal(project.Id, issueByName.ProjectId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task OpencodeModels_OnProjectRoute_ReturnsGlobalRunnerModels()
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
            var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", UniqueProjectName("global-models"));
            var response = await _fixture.Client.GetDataAsync<OpencodeModelsDto>($"/api/projects/{project.Id}/opencode/models");

            Assert.Contains("openai/gpt-5.5", response.Models);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task OpencodeModels_RunnerReportsVariants_ReturnsModelVariantsMap()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"variants-{Guid.NewGuid():N}", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var runnerId = $"variant-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "test-host",
            projectId,
            coderModels = new[] { "zai/glm-5", "openai/gpt-5.5" },
            coderModelVariants = new Dictionary<string, string[]>
            {
                ["zai/glm-5"] = new[] { "low", "medium", "high", "max" },
                ["openai/gpt-5.5"] = new[] { "minimal", "low", "medium", "high" },
            },
        });
        try
        {
            var response = await _fixture.Client.GetDataAsync<OpencodeModelsDto>($"/api/projects/{projectId}/opencode/models");

            Assert.Contains("zai/glm-5", response.Models);
            Assert.Contains("openai/gpt-5.5", response.Models);
            Assert.Equal(new[] { "low", "medium", "high", "max" }, response.ModelVariants["zai/glm-5"]);
            Assert.Equal(new[] { "minimal", "low", "medium", "high" }, response.ModelVariants["openai/gpt-5.5"]);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task OpencodeModels_ModelWithoutVariants_KeepsModelAndOmitsFromVariantsMap()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"no-variants-{Guid.NewGuid():N}", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var runnerId = $"no-variants-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "test-host",
            projectId,
            coderModels = new[] { "zai/glm-5", "openai/gpt-5.5" },
            coderModelVariants = new Dictionary<string, string[]>
            {
                ["zai/glm-5"] = new[] { "low", "high" },
            },
        });
        try
        {
            var response = await _fixture.Client.GetDataAsync<OpencodeModelsDto>($"/api/projects/{projectId}/opencode/models");

            Assert.Contains("openai/gpt-5.5", response.Models);
            Assert.Contains("zai/glm-5", response.Models);
            Assert.Equal(new[] { "low", "high" }, response.ModelVariants["zai/glm-5"]);
            Assert.False(response.ModelVariants.ContainsKey("openai/gpt-5.5"));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task OpencodeModels_DisjointRunnerModels_ProducesUnionWithVariantsPreserved()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"union-{Guid.NewGuid():N}", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var projectRunnerId = $"union-project-runner-{Guid.NewGuid():N}";
        var globalRunnerId = $"union-global-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{projectRunnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "project-host",
            projectId,
            coderModels = new[] { "zai/glm-5" },
            coderModelVariants = new Dictionary<string, string[]>
            {
                ["zai/glm-5"] = new[] { "low", "medium" },
            },
        });
        await _fixture.Client.PostOkAsync($"/api/runner/{globalRunnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "global-host",
            coderModels = new[] { "openai/gpt-5.5" },
            coderModelVariants = new Dictionary<string, string[]>
            {
                ["openai/gpt-5.5"] = new[] { "minimal", "high" },
            },
        });

        try
        {
            var response = await _fixture.Client.GetDataAsync<OpencodeModelsDto>($"/api/projects/{projectId}/opencode/models");

            Assert.Equal(new[] { "openai/gpt-5.5", "zai/glm-5" }, response.Models);
            Assert.Equal(new[] { "low", "medium" }, response.ModelVariants["zai/glm-5"]);
            Assert.Equal(new[] { "minimal", "high" }, response.ModelVariants["openai/gpt-5.5"]);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{projectRunnerId}/unregister", null);
            await _fixture.Client.PostAsync($"/api/runner/{globalRunnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task RunnerStatus_RunnerWithVariants_KeepsCoderModelsAsStringArray()
    {
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = $"status-{Guid.NewGuid():N}", repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString()!;
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });

        var runnerId = $"status-variant-runner-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = Array.Empty<string>(),
            hostname = "status-host",
            projectId,
            coderModels = new[] { "zai/glm-5" },
            coderModelVariants = new Dictionary<string, string[]>
            {
                ["zai/glm-5"] = new[] { "low", "high" },
            },
        });

        try
        {
            var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/runners");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var runners = payload.GetProperty("data").GetProperty("runners");
            var runner = runners.EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == runnerId);
            Assert.NotEqual(JsonValueKind.Undefined, runner.ValueKind);

            var coderModels = runner.GetProperty("coderModels");
            Assert.Equal(JsonValueKind.Array, coderModels.ValueKind);
            Assert.Single(coderModels.EnumerateArray());
            Assert.Equal("zai/glm-5", coderModels[0].GetString());

            Assert.False(runner.TryGetProperty("coderModelVariants", out _));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task IssueRebaseApi_QueuesWorkflowTask()
    {
        var projectName = UniqueProjectName("rebase");
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = projectName, repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" } });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString();
        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "trunk", setDefault = true });
        var issueResponse = await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/issues", new { title = "Needs rebase", isDraft = false });
        var issueJson = await issueResponse.Content.ReadFromJsonAsync<JsonElement>();
        var number = issueJson.GetProperty("data").GetProperty("number").GetInt32();
        var issueId = issueJson.GetProperty("data").GetProperty("id").GetString()!;

        await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{number}/start", new { });

        var runnerId = $"rebase-test-{Guid.NewGuid():N}";
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "mohist/rebase", "spec/task", "spec/check" },
            hostname = "test-host",
            projectId,
        });

        try
        {
            var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
            var issueStatus = await issueGrain.GetWorkflowStatusAsync();
            var wrId = issueStatus!.WorkflowRunId!;

            var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(wrId);
            await workflow.AssignWorkerAsync(runnerId);
            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            await runner.PollAsync(_fixture.Services);

            using var response = await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{number}/rebase", new { });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            Assert.Equal("queued", data.GetProperty("status").GetString());
            Assert.Equal("trunk", data.GetProperty("baseBranch").GetString());
            Assert.StartsWith("rebase-", data.GetProperty("taskId").GetString());

            using var duplicate = await _fixture.Client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{number}/rebase", new { });
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            var duplicatePayload = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("rebase_already_pending", duplicatePayload.GetProperty("code").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
            await _fixture.Client.PostAsync($"/api/projects/{projectId}/issues/{number}/stop", null);
        }
    }

    private sealed record OpencodeModelsDto(string[] Models, Dictionary<string, string[]> ModelVariants)
    {
        public OpencodeModelsDto() : this(Array.Empty<string>(), new Dictionary<string, string[]>()) { }
    }
    private sealed record ProjectDto(string Id, string Name);
    private sealed record RepositoryDto(string Name);
    private sealed record IssueDto(string Id, string ProjectId, int Number);
    private sealed record AgentStatusDto(AgentCapacityDto Capacity, RunnerDto[] Runners);
    private sealed record AgentCapacityDto(int Active, int Max);
    private sealed record RunnerDto(string Id, string? Kind = null, int Active = 0, int Max = 0);

    private static string UniqueProjectName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 32, 63)];
}
