using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using static Mohist.Server.SpecTests.Support.PathContractAssertions;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class PathContractRegressionSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public PathContractRegressionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public static TheoryData<string, string> ForbiddenLocalRepositoryFields => new()
    {
        { "path", "/runner/worktree" },
        { "remote", "origin" },
        { "resolvedPath", "/runner/resolved-worktree" },
    };

    [Fact]
    public async Task LegacyWorktreeStatusRoute_ReturnsNotFound()
    {
        var name = $"legacy-status-{Guid.NewGuid():N}";
        var project = await (await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "primary", gitUrl = "git@example.com:primary.git", baseBranch = "main" },
        }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("data").GetProperty("id").GetString()!;
        var issue = await (await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Legacy status", projectId = projectId }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();

        using var response = await _client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/worktree-status");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // Item-5: the workspace-path slug must stay in sync with the runner's
    // slug() helper in packages/runner/src/runtime/workspace.ts. The
    // runner-equivalent test in workspace.spec.ts asserts the JS side; this
    // test pins the C# side with representative Unicode inputs.
    [Theory]
    [InlineData("my-project", "my-project")]
    [InlineData("My Project!", "my-project")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("foo_bar.baz", "foo-bar-baz")]
    [InlineData("Café", "caf")]
    [InlineData("测试-project", "project")]
    [InlineData("", "project")]
    [InlineData(null, "project")]
    public void Slug_MatchesRunnerAlgorithm(string? input, string expected)
    {
        Assert.Equal(expected, MohistWorkspaceLayout.Slug(input ?? string.Empty));
    }

    [Fact]
    public async Task ProjectsList_OmitsPathAndEffectivePath()
    {
        var name = $"contract-list-{Guid.NewGuid():N}";
        var createResponse = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" },
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, createResponse.StatusCode);

        using var listResponse = await _client.GetAsync("/api/projects");
        Assert.Equal(System.Net.HttpStatusCode.OK, listResponse.StatusCode);
        var json = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement;
        var data = json.GetProperty("data");

        foreach (var project in data.EnumerateArray())
        {
            AssertProjectHasNoLocalPathFields(project);
        }
    }

    [Fact]
    public async Task ProjectDetail_OmitsPathAndEffectivePath()
    {
        var name = $"contract-detail-{Guid.NewGuid():N}";
        var createResponse = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" },
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("data").GetProperty("id").GetString()!;

        using var detailResponse = await _client.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, detailResponse.StatusCode);
        var json = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync()).RootElement;
        var data = json.GetProperty("data");

        AssertProjectHasNoLocalPathFields(data);
    }

    [Fact]
    public async Task ProjectCreate_WithoutPath_ReturnsProjectWithoutLocalPathFields()
    {
        var name = $"contract-create-{Guid.NewGuid():N}";
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" },
        });

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var data = json.GetProperty("data");

        Assert.Equal(name, data.GetProperty("name").GetString());
        AssertProjectHasNoLocalPathFields(data);
    }

    [Theory]
    [MemberData(nameof(ForbiddenLocalRepositoryFields))]
    public async Task ProjectCreate_WithForbiddenLocalRepositoryField_ReturnsBadRequestAndDoesNotCreateProject(
        string field,
        string value)
    {
        var name = $"contract-local-create-{Guid.NewGuid():N}";
        var payload = JsonSerializer.Serialize(new
        {
            name,
            repository = new Dictionary<string, string>
            {
                ["name"] = "primary",
                ["gitUrl"] = "git@example.com:primary.git",
                ["baseBranch"] = "main",
                [field] = value,
            },
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/projects", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            await _client.GetDataAsync<List<ProjectInfo>>("/api/projects"),
            project => project.Name == name);
    }

    [Fact]
    public async Task RepositoryAdd_WithoutGitUrl_Returns400AndDoesNotMutateState()
    {
        var name = $"contract-repoadd-{Guid.NewGuid():N}";
        var project = await (await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "test-repo", gitUrl = "git@example.com:test-repo.git", baseBranch = "main" },
        }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("data").GetProperty("id").GetString()!;

        using var before = await _client.GetAsync($"/api/projects/{projectId}/repositories");
        var beforeRepos = (await before.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetArrayLength();

        using var addResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repositories",
            new { name = "legacy", path = "/tmp/legacy" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, addResponse.StatusCode);

        using var after = await _client.GetAsync($"/api/projects/{projectId}/repositories");
        var afterRepos = (await after.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetArrayLength();
        Assert.Equal(beforeRepos, afterRepos);
    }

    [Theory]
    [MemberData(nameof(ForbiddenLocalRepositoryFields))]
    public async Task RepositoryAdd_WithForbiddenLocalRepositoryField_ReturnsBadRequestAndDoesNotMutateState(
        string field,
        string value)
    {
        var project = await (await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"contract-local-add-{Guid.NewGuid():N}",
            repository = new { name = "primary", gitUrl = "git@example.com:primary.git", baseBranch = "main" },
        }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("data").GetProperty("id").GetString()!;

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = "secondary",
            ["gitUrl"] = "git@example.com:secondary.git",
            ["baseBranch"] = "develop",
            [field] = value,
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"/api/projects/{projectId}/repositories", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var after = await _client.GetAsync($"/api/projects/{projectId}/repositories");
        var repositories = (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray().ToList();
        var repository = Assert.Single(repositories);
        Assert.Equal("primary", repository.GetProperty("name").GetString());
        Assert.Equal("git@example.com:primary.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("main", repository.GetProperty("baseBranch").GetString());
    }

    [Fact]
    public async Task RepositoryAdd_WithGitUrl_ReturnsRepositoryWithoutLocalPathFields()
    {
        var name = $"contract-repogit-{Guid.NewGuid():N}";
        var project = await (await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "primary", gitUrl = "git@example.com:primary.git", baseBranch = "main" },
        }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("data").GetProperty("id").GetString()!;

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repositories",
            new { name = "web", gitUrl = "git@example.com:web.git", baseBranch = "develop" });

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        var repo = data.GetProperty("repositories").EnumerateArray()
            .Single(repository => repository.GetProperty("name").GetString() == "web");
        AssertRepositoryHasNoLocalPathFields(repo);
        Assert.Equal("git@example.com:web.git", repo.GetProperty("gitUrl").GetString());
        Assert.Equal("develop", repo.GetProperty("baseBranch").GetString());
    }

    [Theory]
    [MemberData(nameof(ForbiddenLocalRepositoryFields))]
    public async Task RepositoryUpdate_WithForbiddenLocalRepositoryField_ReturnsBadRequestAndDoesNotMutateState(
        string field,
        string value)
    {
        var project = await (await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"contract-local-update-{Guid.NewGuid():N}",
            repository = new { name = "primary", gitUrl = "git@example.com:primary.git", baseBranch = "main" },
        }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("data").GetProperty("id").GetString()!;

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["gitUrl"] = "git@example.com:changed.git",
            ["baseBranch"] = "develop",
            [field] = value,
        });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PatchAsync($"/api/projects/{projectId}/repositories/primary", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var after = await _client.GetAsync($"/api/projects/{projectId}/repositories");
        var repository = Assert.Single((await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray());
        Assert.Equal("git@example.com:primary.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("main", repository.GetProperty("baseBranch").GetString());
    }

    [Fact]
    public async Task IssueStart_DispatchVariables_ContainGitUrlButNoLocalPathFields()
    {
        var name = $"contract-dispatch-{Guid.NewGuid():N}";
        var project = await (await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "primary", gitUrl = "git@example.com:dispatch.git", baseBranch = "main" },
        }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("data").GetProperty("id").GetString()!;

        var issue = await (await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Dispatch contract", projectId = projectId, isDraft = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();

        using var startResponse = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/start",
            null);
        Assert.Equal(System.Net.HttpStatusCode.OK, startResponse.StatusCode);
        await DispatchEventsAsync();

        using var statusResponse = await _client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
        Assert.Equal(System.Net.HttpStatusCode.OK, statusResponse.StatusCode);
        var statusJson = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync()).RootElement;
        var workflowRunId = statusJson.GetProperty("data").GetProperty("workflowRunId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workflowRunId), "expected start to create a workflow run");

        using var varsResponse = await _client.GetAsync(
            $"/api/workflow-runs/{workflowRunId}/variables/effective");
        Assert.Equal(System.Net.HttpStatusCode.OK, varsResponse.StatusCode);
        var varsJson = JsonDocument.Parse(await varsResponse.Content.ReadAsStringAsync()).RootElement;
        var variables = varsJson.GetProperty("data");
        AssertDispatchVariablesHaveWorkspaceContract(variables);
    }

    [Fact]
    public async Task WorkflowRunProfileVariables_AreReadWrittenAsVariableBundle()
    {
        var workflowRunId = $"wr_api_{Guid.NewGuid():N}";

        using var putResponse = await _client.PutAsJsonAsync(
            $"/api/workflow-runs/{workflowRunId}/workflow-profile/variables",
            new { vars = new { github = new { pr = new { number = 10 } }, source = "put" } });
        Assert.Equal(System.Net.HttpStatusCode.OK, putResponse.StatusCode);

        using var patchResponse = await _client.PatchAsJsonAsync(
            $"/api/workflow-runs/{workflowRunId}/workflow-profile/variables",
            new { vars = new { github = new { pr = new { url = "https://example.test/pr/10" } } } });
        Assert.Equal(System.Net.HttpStatusCode.OK, patchResponse.StatusCode);

        using var variablesResponse = await _client.GetAsync(
            $"/api/workflow-runs/{workflowRunId}/workflow-profile/variables");
        Assert.Equal(System.Net.HttpStatusCode.OK, variablesResponse.StatusCode);
        var variablesJson = JsonDocument.Parse(await variablesResponse.Content.ReadAsStringAsync()).RootElement;
        var vars = variablesJson.GetProperty("data").GetProperty("vars");
        var pr = vars.GetProperty("github").GetProperty("pr");
        Assert.Equal(10, pr.GetProperty("number").GetInt32());
        Assert.Equal("https://example.test/pr/10", pr.GetProperty("url").GetString());

        using var profileResponse = await _client.GetAsync(
            $"/api/workflow-runs/{workflowRunId}/workflow-profile");
        Assert.Equal(System.Net.HttpStatusCode.OK, profileResponse.StatusCode);
        var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync()).RootElement;
        var profileData = profileJson.GetProperty("data");
        Assert.Equal(workflowRunId, profileData.GetProperty("workflowRunId").GetString());
        Assert.Equal("put", profileData.GetProperty("variables").GetProperty("vars").GetProperty("source").GetString());
    }

    [Fact]
    public async Task WorkflowEffectiveVariableKeyPath_ReturnsValueOrJsonNull()
    {
        var name = $"keypath-{Guid.NewGuid():N}";
        var project = await (await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "primary", gitUrl = "git@example.com:keypath.git", baseBranch = "main" },
        }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("data").GetProperty("id").GetString()!;
        var issue = await (await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Key path contract", projectId, isDraft = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var issueNumber = issue.GetProperty("data").GetProperty("number").GetInt32();

        using var startResponse = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/start",
            null);
        Assert.Equal(System.Net.HttpStatusCode.OK, startResponse.StatusCode);
        await DispatchEventsAsync();

        using var statusResponse = await _client.GetAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/workflow/status");
        Assert.Equal(System.Net.HttpStatusCode.OK, statusResponse.StatusCode);
        var statusJson = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync()).RootElement;
        var workflowRunId = statusJson.GetProperty("data").GetProperty("workflowRunId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workflowRunId), "expected start to create a workflow run");

        using var putResponse = await _client.PutAsJsonAsync(
            $"/api/workflow-runs/{workflowRunId}/workflow-profile/variables",
            new
            {
                vars = new
                {
                    github = new { pr = new { number = 10 } },
                    agent = new { model = "base-model" },
                },
                stages = new
                {
                    build = new
                    {
                        vars = new
                        {
                            agent = new { model = "build-model" },
                        },
                    },
                },
            });
        Assert.Equal(System.Net.HttpStatusCode.OK, putResponse.StatusCode);

        using var baseResponse = await _client.GetAsync(
            $"/api/workflow-runs/{workflowRunId}/variables/effective/agent.model");
        Assert.Equal(System.Net.HttpStatusCode.OK, baseResponse.StatusCode);
        var baseJson = JsonDocument.Parse(await baseResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("base-model", baseJson.GetProperty("data").GetString());

        using var stageResponse = await _client.GetAsync(
            $"/api/workflow-runs/{workflowRunId}/variables/effective/agent.model?stage=build");
        Assert.Equal(System.Net.HttpStatusCode.OK, stageResponse.StatusCode);
        var stageJson = JsonDocument.Parse(await stageResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("build-model", stageJson.GetProperty("data").GetString());

        using var missingResponse = await _client.GetAsync(
            $"/api/workflow-runs/{workflowRunId}/variables/effective/github.pr.url");
        Assert.Equal(System.Net.HttpStatusCode.OK, missingResponse.StatusCode);
        var missingJson = JsonDocument.Parse(await missingResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, missingJson.GetProperty("data").ValueKind);
    }

    private Task DispatchEventsAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
}
