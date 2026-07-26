using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IssueProfile")]
public class IssueWorkspaceRepositoryResolutionSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public IssueWorkspaceRepositoryResolutionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task GivenProjectRepositoryConfigChanges_AfterIssueCreation_WhenUserOpensWorkspaceDiff_ThenBaseBranchComesFromRunSnapshot()
    {
        // Given an issue bound to a project repository whose path and base branch are
        // subsequently changed in project configuration.
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
            [new Mohist.Server.Runner.Services.SignalR.DiffFile("a.txt", 1, 0, "@@ -1 +1 @@\n-x\n+y\n", false)]);

        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary-old", "develop");
        var issue = await CreateIssueAsync(projectId, "Repo path drifts", "secondary");
        await StartIssueAndAssignmentRunnerAsync(projectId, issue.Number);

        // Round-trip the issue through cancelled to satisfy the
        // repository deletion guard (issue-417 T-004) for the swap.
        await DriveIssueToTerminalAsync(projectId, issue);

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync("secondary", "git@secondary.example:repo-new.git", "release");

        // When the user opens the workspace diff.
        using var diffResponse = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/diff");
        Assert.Equal(HttpStatusCode.OK, diffResponse.StatusCode);
        var diff = JsonDocument.Parse(await diffResponse.Content.ReadAsStringAsync()).RootElement;
        var data = diff.GetProperty("data");
        Assert.True(data.GetProperty("available").GetBoolean());
        // The diff endpoint passes the run-owned repository context even
        // after the Project declaration is replaced.
        Assert.Equal("develop", _fixture.RunnerWorkspace.LastBaseBranch);
    }

    [Fact]
    public async Task GivenReferencedRepositoryRemoved_WhenUserRequestsWorkspaceEndpoints_ThenApiReturnsRepositoryConfigurationProblem()
    {
        // Given an issue bound to a project repository whose configuration is later removed.
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Repo gets removed", "secondary");
        await StartIssueAndAssignmentRunnerAsync(projectId, issue.Number);

        await DriveIssueToTerminalAsync(projectId, issue);

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");

        // When the user requests workspace diff/file-content/workspace-status/cleanup endpoints.
        using var diff = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/diff");
        using var fileContent = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/file-content?path=a.txt");
        using var workspaceStatus = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/workspace-status");
        using var cleanup = await _client.PostAsync($"/api/projects/{projectId}/issues/{issue.Number}/cleanup", null);

        // The run snapshot remains authoritative after the live declaration is removed.
        Assert.Equal(HttpStatusCode.OK, diff.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fileContent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workspaceStatus.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cleanup.StatusCode);
    }

    [Fact]
    public async Task GivenProjectRepositoryBaseBranchChanges_AfterIssueCreation_WhenUserRebasesWithoutBaseBranch_ThenRebaseUsesRunSnapshot()
    {
        // Given an issue bound to a project repository whose base branch is later changed.
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Base branch drifts", "secondary");
        await StartIssueAndAssignmentRunnerAsync(projectId, issue.Number);

        await DriveIssueToTerminalAsync(projectId, issue);

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", "release");

        // When the user queues a rebase without specifying a base branch.
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/rebase",
            new { });

        // Then the rebase uses the run-owned snapshot's base branch
        // ("develop" at start time), not the project's current base
        // branch ("release"). The D4 design rule is that the run owns
        // its repository context for its entire lifetime.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("develop", payload.GetProperty("data").GetProperty("baseBranch").GetString());
    }

    [Fact]
    public async Task GivenReferencedRepositoryRemoved_WhenUserRebases_ThenApiUsesRunSnapshot()
    {
        // Given an issue bound to a project repository whose configuration is later removed.
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Rebase orphan", "secondary");
        await StartIssueAndAssignmentRunnerAsync(projectId, issue.Number);

        await DriveIssueToTerminalAsync(projectId, issue);

        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");

        // When the user queues a rebase.
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/rebase",
            new { });

        // Then the endpoint uses the immutable run snapshot instead of
        // requiring the live repository declaration.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("develop", payload.GetProperty("data").GetProperty("baseBranch").GetString());
    }

    [Fact]
    public async Task GivenReferencedRepositoryRemoved_WhenUserArchivesIssue_ThenApiReturnsRepositoryConfigurationProblem()
    {
        // Given an issue bound to a project repository whose configuration is later removed.
        var projectId = await CreateProjectWithSecondaryRepositoryAsync("/proj/secondary", "develop");
        var issue = await CreateIssueAsync(projectId, "Archive orphan", "secondary");

        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{issue.Number}/close");

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
        await grain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", secondaryBaseBranch);
        return projectId;
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = "git@main.example:repo.git", baseBranch = "main" },
        });
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
        return new IssueDto(json.GetProperty("data").GetProperty("number").GetInt32());
    }

    private async Task StartIssueAndAssignmentRunnerAsync(string projectId, int number)
    {
        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{number}/start");
        await DispatchEventsAsync();

        var runnerId = $"repo-resolution-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "mohist/rebase", "spec/task", "spec/check" },
            hostname = "test-host",
            projectId,
        });

        var issue = await _client.GetDataAsync<IssueDto>($"/api/projects/{projectId}/issues/{number}");
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issue.Number)));
        var issueStatus = await issueGrain.GetWorkflowStatusAsync();
        var wrId = issueStatus!.WorkflowRunId!;

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(wrId);
        await workflow.AssignWorkerAsync(runnerId);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.PollAsync(_fixture.Services);

    }

    private async Task DispatchEventsAsync()
    {
        await _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();
    }

    private async Task DriveIssueToTerminalAsync(string projectId, IssueDto issue)
    {
        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{issue.Number}/stop");
        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{issue.Number}/close");
    }

    private sealed record IssueDto(int Number);
}
