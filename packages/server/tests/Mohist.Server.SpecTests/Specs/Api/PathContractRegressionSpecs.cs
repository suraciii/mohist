using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.SpecTests.Support;
using Xunit;

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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
    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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
        var data = project.GetProperty("data");

        var firstRepo = data.GetProperty("repositories").EnumerateArray().First();
        AssertRepositoryHasNoLocalPathFields(firstRepo);
        Assert.Equal("git@example.com:primary.git", firstRepo.GetProperty("gitUrl").GetString());
        Assert.Equal("main", firstRepo.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    private static void AssertProjectHasNoLocalPathFields(JsonElement project)
    {
        Assert.Equal(JsonValueKind.Object, project.ValueKind);
        Assert.False(project.TryGetProperty("path", out _), "project response unexpectedly contained 'path'");
        Assert.False(project.TryGetProperty("effectivePath", out _), "project response unexpectedly contained 'effectivePath'");
        Assert.False(project.TryGetProperty("checkoutPath", out _), "project response unexpectedly contained 'checkoutPath'");
        Assert.False(project.TryGetProperty("baseBranch", out _), "project response unexpectedly contained 'baseBranch'");

        if (project.TryGetProperty("repositories", out var repositories)
            && repositories.ValueKind == JsonValueKind.Array)
        {
            foreach (var repo in repositories.EnumerateArray())
            {
                AssertRepositoryHasNoLocalPathFields(repo);
            }
        }
    }

    private static void AssertRepositoryHasNoLocalPathFields(JsonElement repo)
    {
        Assert.Equal(JsonValueKind.Object, repo.ValueKind);
        Assert.False(repo.TryGetProperty("path", out _), "repository response unexpectedly contained 'path'");
        Assert.False(repo.TryGetProperty("remote", out _), "repository response unexpectedly contained 'remote'");
        Assert.False(repo.TryGetProperty("resolvedPath", out _), "repository response unexpectedly contained 'resolvedPath'");
        Assert.True(repo.TryGetProperty("gitUrl", out _), "repository response missing 'gitUrl'");
        Assert.True(repo.TryGetProperty("baseBranch", out _), "repository response missing 'baseBranch'");
    }

    private static void AssertDispatchVariablesHaveWorkspaceContract(JsonElement variables)
    {
        if (variables.ValueKind != JsonValueKind.Object)
        {
            Assert.Fail($"expected dispatch variables to be a JSON object, got {variables.ValueKind}");
            return;
        }

        if (variables.TryGetProperty("project", out var projectVar)
            && projectVar.ValueKind == JsonValueKind.Object)
        {
            Assert.False(projectVar.TryGetProperty("path", out _), "project dispatch variable unexpectedly contained 'path'");
            Assert.False(projectVar.TryGetProperty("effectivePath", out _), "project dispatch variable unexpectedly contained 'effectivePath'");
            // baseBranch is a repository property, not a project property.
            Assert.False(projectVar.TryGetProperty("baseBranch", out _), "project dispatch variable unexpectedly contained 'baseBranch'");
            Assert.False(projectVar.TryGetProperty("defaultBranch", out _), "project dispatch variable unexpectedly contained 'defaultBranch'");
        }

        if (variables.TryGetProperty("repository", out var repoVar)
            && repoVar.ValueKind == JsonValueKind.Object)
        {
            Assert.False(repoVar.TryGetProperty("path", out _), "repository dispatch variable unexpectedly contained 'path'");
            Assert.False(repoVar.TryGetProperty("remote", out _), "repository dispatch variable unexpectedly contained 'remote'");
            Assert.False(repoVar.TryGetProperty("resolvedPath", out _), "repository dispatch variable unexpectedly contained 'resolvedPath'");
            Assert.True(repoVar.TryGetProperty("gitUrl", out _), "repository dispatch variable missing 'gitUrl'");
            Assert.True(repoVar.TryGetProperty("baseBranch", out _), "repository dispatch variable missing 'baseBranch'");
        }

        Assert.True(variables.TryGetProperty("workspace", out var workspace),
            "dispatch variables missing 'workspace'");
        if (workspace.ValueKind == JsonValueKind.Object)
        {
            Assert.True(workspace.TryGetProperty("path", out _), "workspace dispatch variable missing 'path'");
            // Per-run head ref (mohist/run-${workflowRunId}); not a worktree branch.
            Assert.True(workspace.TryGetProperty("branch", out _), "workspace dispatch variable missing 'branch'");
        }
    }
}
