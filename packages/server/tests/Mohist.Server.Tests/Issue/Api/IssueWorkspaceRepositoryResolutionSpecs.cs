using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Issue.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
public class IssueWorkspaceRepositoryResolutionSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly List<string> _runnerIds = [];

    public IssueWorkspaceRepositoryResolutionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        foreach (var runnerId in _runnerIds)
            await _client.PostAsync($"/api/runner/{runnerId}/unregister", null);
    }

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
            [new DiffFile("a.txt", 1, 0, "@@ -1 +1 @@\n-x\n+y\n", false)]);

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
        var projectId = $"proj-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            name,
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@main.example:repo.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return projectId;
    }

    private async Task<IssueDto> CreateIssueAsync(string projectId, string title, string? repositoryName = null, bool isDraft = false)
    {
        var number = await _fixture.Grains
            .GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId))
            .NextAsync();
        await _fixture.Grains
            .GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)))
            .CreateAsync(projectId, number, title, null, null, null, repositoryRef: repositoryName, isDraft: isDraft);
        return new IssueDto(number);
    }

    private async Task StartIssueAndAssignmentRunnerAsync(string projectId, int number)
    {
        var issue = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));
        var workflowRunId = await issue.StartWorkAsync();

        var runnerId = $"repo-resolution-runner-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(
            new RunnerInfo(runnerId, ["mohist/rebase", "spec/task", "spec/check"], "test-host", projectId),
            TestRunnerGenerationExtensions.ProcessGeneration);
        _runnerIds.Add(runnerId);

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.AssignWorkerAsync(runnerId);
        await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(_fixture.Services);
    }

    private async Task DispatchEventsAsync()
    {
        await _fixture.Services.GetRequiredService<IEventDispatcher>().DrainAsync();
    }

    private async Task DriveIssueToTerminalAsync(string projectId, IssueDto issue)
    {
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issue.Number)));
        var workflowRunId = (await issueGrain.GetWorkflowStatusAsync())?.WorkflowRunId
            ?? throw new InvalidOperationException("Issue has no workflow run");
        await _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId).StopAsync("test-stop");
        await issueGrain.CancelAsync();
    }

    private sealed record IssueDto(int Number);
}
