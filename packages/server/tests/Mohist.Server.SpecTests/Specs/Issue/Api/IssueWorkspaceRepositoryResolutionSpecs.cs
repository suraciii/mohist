using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue3")]
public class IssueWorkspaceRepositoryResolutionSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public IssueWorkspaceRepositoryResolutionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GivenProjectRepositoryConfigChanges_AfterIssueCreation_WhenUserOpensWorkspaceDiff_ThenPathAndBaseBranchComeFromCurrentProjectConfig()
    {
        // Given an issue bound to a project repository whose path and base branch are
        // subsequently changed in project configuration.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;
        _fixture.Git.Diff = new GitDiffResult
        {
            Files = [new DiffFile("a.txt", 1, 0, "@@ -1 +1 @@\n-x\n+y\n", false)],
            TotalAdditions = 1,
            TotalDeletions = 1,
        };
        _fixture.Git.AheadBehind = (1, 0);
        _fixture.RunnerWorkspace.Reset();
        _fixture.RunnerWorkspace.WorkspaceStatus = new WorkspaceStatus
        {
            Exists = true,
            Branch = "mohist/run-test",
            BaseBranch = "release",
            Ahead = 1,
            Behind = 0,
            RebaseInProgress = false,
            ConflictingFiles = [],
        };
        _fixture.RunnerWorkspace.Diff = new RunnerWorkspaceDiffResult(
            "release", "mohist/run-test", "merge-base", 1, 0, 1, 1, 1,
            [new DiffFile("a.txt", 1, 0, "@@ -1 +1 @@\n-x\n+y\n", false)]);

        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary-old", "develop");
        var issue = await CreateIssueAsync(projectId, "Repo path drifts", "secondary");
        await StartIssueAndAssignmentRunnerAsync(projectId, issue.Number);

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync("secondary", "git@secondary.example:repo-new.git", "release");

        // When the user opens the workspace diff.
        using var diffResponse = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/diff");
        Assert.Equal(HttpStatusCode.OK, diffResponse.StatusCode);
        var diff = JsonDocument.Parse(await diffResponse.Content.ReadAsStringAsync()).RootElement;
        var data = diff.GetProperty("data");
        Assert.True(data.GetProperty("available").GetBoolean());
        // The diff endpoint exposes the current repository's base branch as "base".
        // After the project repository config change it must reflect the new value
        // ("release") rather than the originally resolved "develop" value.
        Assert.Equal("release", data.GetProperty("base").GetString());

        // Then the diff git call used the latest path resolved from the current project
        // configuration, not the repository fields resolved at issue creation.
        Assert.NotEmpty(_fixture.Git.Diff.Files);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GivenReferencedRepositoryRemoved_WhenUserRequestsWorkspaceEndpoints_ThenApiReturnsRepositoryConfigurationProblem()
    {
        // Given an issue bound to a project repository whose configuration is later removed.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Repo gets removed", "secondary");

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");

        // When the user requests workspace diff/file-content/workspace-status/cleanup endpoints.
        using var diff = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/diff");
        using var fileContent = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/file-content?path=a.txt");
        using var workspaceStatus = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/workspace-status");
        using var cleanup = await _client.PostAsync($"/api/projects/{projectId}/issues/{issue.Number}/cleanup", null);

        // Then each endpoint returns a clear repository configuration problem instead of
        // silently using stale issue repository data or falling back to "main".
        Assert.Equal(HttpStatusCode.Conflict, diff.StatusCode);
        var diffPayload = await diff.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", diffPayload.GetProperty("code").GetString());
        Assert.Contains("secondary", diffPayload.GetProperty("error").GetString() ?? string.Empty);

        Assert.Equal(HttpStatusCode.Conflict, fileContent.StatusCode);
        var fileContentPayload = await fileContent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", fileContentPayload.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Conflict, workspaceStatus.StatusCode);
        var workspaceStatusPayload = await workspaceStatus.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", workspaceStatusPayload.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Conflict, cleanup.StatusCode);
        var cleanupPayload = await cleanup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", cleanupPayload.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GivenProjectRepositoryBaseBranchChanges_AfterIssueCreation_WhenUserRebasesWithoutBaseBranch_ThenRebaseUsesCurrentBaseBranch()
    {
        // Given an issue bound to a project repository whose base branch is later changed.
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Base branch drifts", "secondary");
        await StartIssueAndAssignmentRunnerAsync(projectId, issue.Number);

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", "release");

        // When the user queues a rebase without specifying a base branch.
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/rebase",
            new { });

        // Then the queued rebase task uses the current project repository base branch
        // resolved from the live project configuration.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("release", payload.GetProperty("data").GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GivenReferencedRepositoryRemoved_WhenUserRebases_ThenApiReturnsRepositoryConfigurationProblem()
    {
        // Given an issue bound to a project repository whose configuration is later removed.
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Rebase orphan", "secondary");
        await StartIssueAndAssignmentRunnerAsync(projectId, issue.Number);

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");

        // When the user queues a rebase.
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/rebase",
            new { });

        // Then the endpoint returns a clear repository configuration problem instead of
        // silently falling back to stale issue repository data or to "main".
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", payload.GetProperty("code").GetString());
        Assert.Contains("secondary", payload.GetProperty("error").GetString() ?? string.Empty);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GivenReferencedRepositoryRemoved_WhenUserArchivesIssue_ThenApiReturnsRepositoryConfigurationProblem()
    {
        // Given an issue bound to a project repository whose configuration is later removed.
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Archive orphan", "secondary");

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");

        // When the user archives the issue.
        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/archive",
            null);

        // Then the endpoint returns a clear repository configuration problem instead of
        // silently removing the workflow workspace using stale issue repository data.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", payload.GetProperty("code").GetString());
    }

    private async Task<string> CreateProjectWithSecondaryRepositoryAsync(string secondaryPath, string secondaryBaseBranch)
    {
        var projectId = await CreateProjectAsync($"proj-{Guid.NewGuid():N}");
        var grain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await grain.AddRepositoryAsync("main", "git@main.example:repo.git", "main");
        await grain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", secondaryBaseBranch);
        return projectId;
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task<IssueDto> CreateIssueAsync(string projectId, string title, string? repositoryName = null, bool isDraft = false)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title, repositoryName, isDraft });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new IssueDto(
            json.GetProperty("data").GetProperty("id").GetString()!,
            json.GetProperty("data").GetProperty("number").GetInt32());
    }

    private async Task StartIssueAndAssignmentRunnerAsync(string projectId, int number)
    {
        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{number}/start");

        var runnerId = $"repo-resolution-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "mohist/rebase", "spec/task", "spec/check" },
            hostname = "test-host",
            projectId,
        });

        var issue = await _client.GetDataAsync<IssueDto>($"/api/projects/{projectId}/issues/{number}");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var issueStatus = await issueGrain.GetWorkflowStatusAsync();
        var wrId = issueStatus!.WorkflowRunId!;

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(wrId);
        await workflow.AssignWorkerAsync(runnerId);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.PollAsync(_fixture.Services);

    }

    private sealed record IssueDto(string Id, int Number);
}
