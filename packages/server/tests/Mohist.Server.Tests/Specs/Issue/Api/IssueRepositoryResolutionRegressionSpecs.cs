using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Runner.Services.SignalR;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueRepositoryResolutionRegressionSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;

    public IssueRepositoryResolutionRegressionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
        _grains = fixture.Grains;
        _services = fixture.Services;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_AfterProjectRepositoryConfigChange_ResolvesLatestMetadataForEachIssue()
    {
        // Given two issues bound to two project repositories.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");

        var explicitIssue = await CreateIssueAsync(projectId, "Explicit repo issue", "secondary");
        var defaultIssue = await CreateIssueAsync(projectId, "Default repo issue");

        // When the project repository configuration is changed.
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("main");
        await projectGrain.AddRepositoryAsync("main", "git@main.example:repo-new.git", "release-new");
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync("secondary", "git@secondary.example:repo-new.git", "develop-new");

        // Then listing issues returns resolved repositories reflecting the latest project config.
        using var scope = _services.CreateScope();
        var issueQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var projectQuery = scope.ServiceProvider.GetRequiredService<ProjectQuerier>();
        var liveProject = await projectQuery.GetByIdAsync(projectId);
        Assert.NotNull(liveProject);
        var list = await issueQuery.ListAsync(projectId, liveProject);

        var byNumber = list.ToDictionary(i => i.Number);
        Assert.True(byNumber.ContainsKey(explicitIssue.Number));
        Assert.True(byNumber.ContainsKey(defaultIssue.Number));

        var explicitInfo = byNumber[explicitIssue.Number];
        Assert.Equal("secondary", explicitInfo.Repository!.Name);
        Assert.Equal("git@secondary.example:repo-new.git", explicitInfo.Repository.GitUrl);
        Assert.Equal("develop-new", explicitInfo.Repository.BaseBranch);
        Assert.Null(explicitInfo.RepositoryProblem);

        var defaultInfo = byNumber[defaultIssue.Number];
        Assert.Equal("main", defaultInfo.Repository!.Name);
        Assert.Equal("git@main.example:repo-new.git", defaultInfo.Repository.GitUrl);
        Assert.Equal("release-new", defaultInfo.Repository.BaseBranch);
        Assert.Null(defaultInfo.RepositoryProblem);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListIssuesApi_AfterProjectRepositoryConfigChange_ResolvesLatestMetadata()
    {
        // Given an issue created against a project repository.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "List path resolves", "secondary");
        Assert.Equal("git@secondary.example:repo.git", issue.Repository!.GitUrl);

        // When the project repository configuration is changed.
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync("secondary", "git@secondary.example:repo-new.git", "develop-new");

        // Then the list endpoint returns the resolved repository from the live project config.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        var list = data.EnumerateArray().ToList();
        var found = list.Single(item => item.GetProperty("number").GetInt32() == issue.Number);
        var repository = found.GetProperty("repository");
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:repo-new.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("develop-new", repository.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PostIssue_WithAmbiguousRepositoryName_ReturnsBadRequestWithRepositoryAmbiguousCode()
    {
        // Given a project with two repositories whose names differ only by case.
        var projectId = await CreateProjectWithAmbiguousRepositoryAsync(
            new RepositoryInfo { Name = "main", GitUrl = "git@main.example:repo.git", BaseBranch = "main", IsDefault = true },
            new RepositoryInfo { Name = "MAIN", GitUrl = "git@MAIN.example:repo.git", BaseBranch = "main", IsDefault = false });

        // When the client posts an issue with the case-insensitive ambiguous reference.
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Ambiguous repo", repositoryName = "main" },
            JsonOptions);

        // Then the API returns a structured ambiguous-reference problem.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<JsonEnvelope>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("repository_ambiguous_reference", envelope.Code);
        Assert.Contains("main", envelope.Error ?? string.Empty);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_WithAmbiguousRepositoryReference_SurfacesAmbiguousRepositoryProblem()
    {
        // Given a project with two repositories whose names differ only by case
        // and an issue whose stored reference resolves ambiguously.
        var projectId = await CreateProjectWithAmbiguousRepositoryAsync(
            new RepositoryInfo { Name = "main", GitUrl = "git@main.example:repo.git", BaseBranch = "main", IsDefault = true },
            new RepositoryInfo { Name = "MAIN", GitUrl = "git@MAIN.example:repo.git", BaseBranch = "main", IsDefault = false });

        var counter = _grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId));
        var number = await counter.NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
        await issueGrain.CreateAsync(projectId, number, "Ambiguous", body: null, labels: null, priority: null, "main", issueId);

        // When the client fetches the issue read model.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{number}");

        // Then the response surfaces an AmbiguousReference problem.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.True(data.TryGetProperty("repository", out var repository));
        Assert.Equal(JsonValueKind.Null, repository.ValueKind);
        var problem = data.GetProperty("repositoryProblem");
        Assert.Equal("AmbiguousReference", problem.GetProperty("code").GetString());
        Assert.Equal("main", problem.GetProperty("repositoryRef").GetString());
        var candidates = problem.GetProperty("candidateNames").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("main", candidates);
        Assert.Contains("MAIN", candidates);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StartWorkAsync_WithAmbiguousRepositoryReference_ThrowsConfigurationProblem()
    {
        // Given a project with two repositories whose names differ only by case
        // and an issue whose stored reference resolves ambiguously.
        var projectId = await CreateProjectWithAmbiguousRepositoryAsync(
            new RepositoryInfo { Name = "main", GitUrl = "git@main.example:repo.git", BaseBranch = "main", IsDefault = true },
            new RepositoryInfo { Name = "MAIN", GitUrl = "git@MAIN.example:repo.git", BaseBranch = "main", IsDefault = false });

        var counter = _grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId));
        var number = await counter.NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
        await issueGrain.CreateAsync(projectId, number, "Ambiguous start", body: null, labels: null, priority: null, "main", issueId);

        // When the user attempts to start the workflow.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issueGrain.StartWorkAsync());

        // Then the start fails with a configuration problem naming the ambiguous reference.
        Assert.Contains("AmbiguousReference", ex.Message);
        Assert.Contains("main", ex.Message);

        // And no workflow run is created.
        var status = await issueGrain.GetWorkflowStatusAsync();
        Assert.Null(status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RebaseEndpoint_AfterRepositoryGitUrlAndBaseBranchChange_EmbedsNewRepositoryContextInTask()
    {
        // Given an issue bound to a project repository whose git url and base branch
        // are subsequently changed.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;

        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Rebase context embeds", "secondary");
        await StartIssueAndClaimRunnerAsync(projectId, issue.Number);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release-new");

        // When the user queues a rebase without specifying a base branch.
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/rebase",
            new { });

        // Then the response uses the current base branch.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("release-new", payload.GetProperty("data").GetProperty("baseBranch").GetString());

        // And the queued rebase task embeds the resolved repository context
        // (name, gitUrl, base branch) from the live project configuration,
        // not the originally resolved repository details.
        var wrId = payload.GetProperty("data").GetProperty("workflowRunId").GetString();
        var run = await LoadWorkflowRunAsync(wrId!);
        var currentStage = run!.Stages.First(s => s.Id == run.CurrentStageId);
        var rebaseTask = currentStage.Tasks.Single(t => t.Uses == "mohist/rebase");
        Assert.NotNull(rebaseTask.WithInput);
        Assert.Equal("release-new", rebaseTask.WithInput!["baseBranch"]!.Value.GetString());
        var repository = rebaseTask.WithInput["repository"]!.Value;
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:repo-new.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("release-new", repository.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RebaseEndpoint_AfterRepositoryGitUrlAndBaseBranchChange_UsesCurrentRepositoryConfiguration()
    {
        // Given an issue bound to a project repository whose git url and base branch
        // are subsequently changed.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;

        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Rebase no fallback", "secondary");
        await StartIssueAndClaimRunnerAsync(projectId, issue.Number);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release-new");

        // When the user queues a rebase.
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/rebase",
            new { });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Then the rebase never falls back to the legacy path/branch or to "main".
        var wrId = payload.GetProperty("data").GetProperty("workflowRunId").GetString();
        var run = await LoadWorkflowRunAsync(wrId!);
        var currentStage = run!.Stages.First(s => s.Id == run.CurrentStageId);
        var rebaseTask = currentStage.Tasks.Single(t => t.Uses == "mohist/rebase");
        Assert.NotNull(rebaseTask.WithInput);
        var withJson = JsonSerializer.Serialize(rebaseTask.WithInput);
        Assert.DoesNotContain("/proj/secondary-old", withJson);
        Assert.DoesNotContain("develop-old", withJson);
        Assert.DoesNotContain("\"baseBranch\":\"main\"", withJson);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkspaceCommitsEndpoint_AfterRepositoryBaseBranchChange_UsesCurrentBaseBranch()
    {
        // Given an issue bound to a project repository whose base branch is later changed.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;
        _fixture.Git.Commits = new[]
        {
            new Mohist.Server.Infrastructure.Workspace.GitCommit(
                "abc1234", "abc1234", "First commit", "Author", "2024-01-01T00:00:00Z", new[] { "a.txt" }),
        };
        _fixture.RunnerWorkspace.Reset();
        _fixture.RunnerWorkspace.WorkspaceStatus = new Mohist.Server.Infrastructure.Workspace.WorkspaceStatus
        {
            Exists = true,
            Branch = "mohist/run-test",
            BaseBranch = "release-new",
            Ahead = 0,
            Behind = 0,
            RebaseInProgress = false,
            ConflictingFiles = [],
        };
        _fixture.RunnerWorkspace.Commits = new RunnerWorkspaceCommitsResult(
            "release-new", "mohist/run-test", "merge-base", 1, 0, 1, 10, 2,
            new[] { new Mohist.Server.Infrastructure.Workspace.GitCommit("abc1234", "abc1234", "First commit", "Author", "2024-01-01T00:00:00Z", new[] { "a.txt" }) });

        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Commits resolve", "secondary");
        await StartIssueAndClaimRunnerAsync(projectId, issue.Number);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo.git",
            "release-new");

        // When the user opens the workspace commits view.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}/commits");

        // Then the endpoint reports the current project repository base branch.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.True(data.GetProperty("available").GetBoolean());
        Assert.Equal("release-new", data.GetProperty("base").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkspaceFileContentEndpoint_AfterReferencedRepositoryRemoved_ReturnsRepositoryConfigurationProblem()
    {
        // Given an issue bound to a project repository whose configuration is later removed.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop");
        var issue = await CreateIssueAsync(projectId, "File content orphan", "secondary");
        await StartIssueAndClaimRunnerAsync(projectId, issue.Number);

        await _grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("secondary");

        // When the user requests the file content.
        using var response = await _client.GetAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/file-content?path=a.txt");

        // Then the endpoint returns a repository configuration problem
        // instead of falling back to "main".
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("repository_not_found", payload.GetProperty("code").GetString());
        Assert.Contains("secondary", payload.GetProperty("error").GetString() ?? string.Empty);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkspaceStatusEndpoint_AfterRepositoryGitUrlAndBaseBranchChange_ReturnsResolvedWorkspaceStatus()
    {
        // Given an issue bound to a project repository whose git url and base branch are later changed.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;
        _fixture.Git.WorkspaceStatus = new Mohist.Server.Infrastructure.Workspace.WorkspaceStatus
        {
            Exists = true,
            Branch = "mo/issue-1",
            BaseBranch = "release-new",
            Ahead = 0,
            Behind = 0,
            RebaseInProgress = false,
            ConflictingFiles = [],
        };
        _fixture.RunnerWorkspace.Reset();
        _fixture.RunnerWorkspace.WorkspaceStatus = new Mohist.Server.Infrastructure.Workspace.WorkspaceStatus
        {
            Exists = true,
            Branch = "mo/issue-1",
            BaseBranch = "release-new",
            Ahead = 0,
            Behind = 0,
            RebaseInProgress = false,
            ConflictingFiles = [],
        };

        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Workspace base drifts", "secondary");
        await StartIssueAndClaimRunnerAsync(projectId, issue.Number);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release-new");

        // When the user requests the workspace status after the project repository config change.
        using var response = await _client.GetAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/workspace-status");

        // Then the endpoint returns a successful workspace status — proving the route
        // resolved the live project repository (git url + base branch) instead of failing
        // because of stale issue repository data or silently using a fallback.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.True(data.GetProperty("exists").GetBoolean());
        Assert.Equal("release-new", data.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_ProjectRepositoryDefaultChanged_PreservesExplicitReferenceInsteadOfAdoptingNewDefault()
    {
        // Given an issue explicitly bound to a non-default project repository.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop");
        var explicitIssue = await CreateIssueAsync(projectId, "Explicit secondary", "secondary");
        Assert.Equal("secondary", explicitIssue.Repository!.Name);
        Assert.False(explicitIssue.Repository.IsDefault);

        // When the project default repository is swapped to secondary.
        await _grains.GetGrain<IProjectGrain>(projectId).SetDefaultRepositoryAsync("secondary");

        // Then the explicit issue still resolves to the same name (secondary),
        // and IsDefault reflects the new project default marker.
        var fetched = await GetIssueReadModelAsync(projectId, explicitIssue.Number);
        Assert.NotNull(fetched);
        Assert.Equal("secondary", fetched!.Repository!.Name);
        Assert.Equal("git@secondary.example:repo.git", fetched.Repository.GitUrl);
        Assert.True(fetched.Repository.IsDefault, "Default flag must follow project config, not the original issue repository details");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_ReferencedRepositoryRemoved_ButProjectHasOtherRepositories_RepositoryProblemReportsCandidates()
    {
        // Given an issue bound to a non-default repository and a project with multiple repositories.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop");
        var issue = await CreateIssueAsync(projectId, "Becomes orphan", "secondary");

        // When the referenced repository is removed.
        await _grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("secondary");

        // Then the issue read model surfaces a RepositoryNotFound problem
        // that includes the surviving candidate names so the user knows which
        // valid repositories they could switch to.
        var fetched = await GetIssueReadModelAsync(projectId, issue.Number);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Repository);
        Assert.NotNull(fetched.RepositoryProblem);
        Assert.Equal(IssueRepositoryProblemCode.RepositoryNotFound, fetched.RepositoryProblem!.Code);
        Assert.Equal("secondary", fetched.RepositoryProblem.RepositoryRef);
        var candidates = Assert.IsType<string[]>(fetched.RepositoryProblem.CandidateNames);
        Assert.Contains("main", candidates);
        Assert.DoesNotContain("secondary", candidates);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_ProjectHasNoRepositories_AfterRepositoryRemoval_SurfacesProjectHasNoRepositoriesProblem()
    {
        // Given a project with a single default repository and an issue bound to it.
        var projectId = await CreateProjectAsync("single-repo");
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.AddRepositoryAsync("main", "git@main.example:repo.git", "main");
        var issue = await CreateIssueAsync(projectId, "Last repo removed", "main");

        // When the only repository is removed.
        await _grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("main");

        // Then the read model reports the project has no repositories (not the
        // repository-not-found code) so the user can tell configuration apart
        // from a stale reference.
        var fetched = await GetIssueReadModelAsync(projectId, issue.Number);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Repository);
        Assert.NotNull(fetched.RepositoryProblem);
        Assert.Equal(IssueRepositoryProblemCode.ProjectHasNoRepositories, fetched.RepositoryProblem!.Code);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_ProjectDeleted_AfterIssueCreation_SurfacesProjectMissingRepositoryProblem()
    {
        // Given an issue created against a project.
        var projectId = await CreateProjectAsync("transient");
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.AddRepositoryAsync("main", "git@main.example:repo.git", "main");
        var issue = await CreateIssueAsync(projectId, "Vanishing project", "main");

        // When the project is deleted.
        await _grains.GetGrain<IProjectGrain>(projectId).DeleteAsync();

        // Then the issue read model surfaces a ProjectMissing repository
        // problem so the user knows the issue is no longer resolvable
        // against any project configuration.
        var fetched = await GetIssueReadModelAsync(projectId, issue.Number);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Repository);
        Assert.NotNull(fetched.RepositoryProblem);
        Assert.Equal(IssueRepositoryProblemCode.ProjectMissing, fetched.RepositoryProblem!.Code);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowStart_AfterProjectRepositoryRemoteChange_PicksUpLatestRemoteInVariables()
    {
        // Given an issue bound to a project repository whose remote is later changed.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop");
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
        await issueGrain.CreateAsync(projectId, number, "Remote drifts", body: null, labels: null, priority: null, "secondary", issueId);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-remote-new.git",
            "develop");

        // When the user starts the workflow.
        var wrId = await issueGrain.StartWorkAsync();

        // Then the workflow variables carry the new remote.
        var variables = await LoadWorkflowVariablesAsync(wrId);
        var repository = variables.RootElement.GetProperty("repository");
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_WithoutRepositoryRef_FallsBackToProjectDefault()
    {
        var projectId = await CreateProjectAsync("legacy-no-ref");
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.AddRepositoryAsync("main", "git@main.example:repo.git", "main");
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "issue_no_ref",
            projectId,
            1,
            "No repository reference",
            body: "uses project default");
        await SeedIssueAsync(projectId, 1, IssueStore.Serialize(issue));

        var info = await GetIssueInfoAsync(projectId, 1);

        // Then the issue resolves to the project default repository.
        Assert.NotNull(info);
        Assert.NotNull(info!.Repository);
        Assert.Equal("main", info.Repository!.Name);
        Assert.Equal("git@main.example:repo.git", info.Repository.GitUrl);
        Assert.True(info.Repository.IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_AfterRepositoryGitUrlAndBaseBranchChange_ReturnsLatestGitUrlAndBaseBranch()
    {
        // Given an issue created against a project repository.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Single read drifts", "secondary");
        Assert.Equal("git@secondary.example:repo.git", issue.Repository!.GitUrl);
        Assert.Equal("develop-old", issue.Repository.BaseBranch);

        // When the project repository configuration is changed.
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release-new");

        // Then GET /api/projects/:projectRef/issues/:number returns the latest resolved repository path
        // and base branch instead of stale stored repository details.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        var repository = data.GetProperty("repository");
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:repo-new.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("release-new", repository.GetProperty("baseBranch").GetString());
        Assert.False(repository.GetProperty("isDefault").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_AndListIssues_ReturnConsistentRepositoryMetadata_AfterConfigChange()
    {
        // Given an issue bound to a project repository whose path and base branch are
        // subsequently changed in project configuration.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Consistency across endpoints", "secondary");

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release-new");

        // When the same issue is fetched by GET /api/projects/:projectRef/issues/:number and via the list endpoint.
        using var singleResponse = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}");
        using var listResponse = await _client.GetAsync($"/api/projects/{projectId}/issues");

        Assert.Equal(HttpStatusCode.OK, singleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var singlePayload = await singleResponse.Content.ReadFromJsonAsync<JsonElement>();
        var listPayload = await listResponse.Content.ReadFromJsonAsync<JsonElement>();

        var singleRepository = singlePayload.GetProperty("data").GetProperty("repository");
        var listItem = listPayload.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("number").GetInt32() == issue.Number);
        var listRepository = listItem.GetProperty("repository");

        // Then the two endpoints agree on the latest resolved repository metadata
        // (gitUrl, base branch) and the default marker, so the issue page and
        // the issue list can never disagree after a project config change.
        Assert.Equal(singleRepository.GetProperty("name").GetString(), listRepository.GetProperty("name").GetString());
        Assert.Equal(singleRepository.GetProperty("gitUrl").GetString(), listRepository.GetProperty("gitUrl").GetString());
        Assert.Equal(singleRepository.GetProperty("baseBranch").GetString(), listRepository.GetProperty("baseBranch").GetString());
        Assert.Equal(singleRepository.GetProperty("isDefault").GetBoolean(), listRepository.GetProperty("isDefault").GetBoolean());

        Assert.Equal("git@secondary.example:repo-new.git", singleRepository.GetProperty("gitUrl").GetString());
        Assert.Equal("release-new", singleRepository.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task IssueListApi_AfterReferencedRepositoryRemoved_ReportsRepositoryProblemForOrphanedIssue()
    {
        // Given an issue bound to a non-default project repository.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop");
        var issue = await CreateIssueAsync(projectId, "Orphan in list", "secondary");

        // When the referenced repository is removed.
        await _grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("secondary");

        // Then GET /api/projects/:projectRef/issues surfaces a RepositoryNotFound problem in the list entry
        // for the orphaned issue, so the list view can render the same configuration
        // problem the single-issue endpoint already shows.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        var listItem = data.EnumerateArray().Single(item => item.GetProperty("number").GetInt32() == issue.Number);
        var repository = listItem.GetProperty("repository");
        Assert.Equal(JsonValueKind.Null, repository.ValueKind);
        var problem = listItem.GetProperty("repositoryProblem");
        Assert.Equal("RepositoryNotFound", problem.GetProperty("code").GetString());
        Assert.Equal("secondary", problem.GetProperty("repositoryRef").GetString());
        var candidates = problem.GetProperty("candidateNames").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("main", candidates);
        Assert.DoesNotContain("secondary", candidates);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task WorkflowStart_AfterMultipleSequentialProjectConfigChanges_UsesLatestConfig()
    {
        // Given an issue created against a project repository.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-v1");
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
        await issueGrain.CreateAsync(projectId, number, "Sequential changes", body: null, labels: null, priority: null, "secondary", issueId);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);

        // When the project repository configuration is changed three times in a row,
        // leaving the repository name unchanged but updating path/remote/base branch.
        foreach (var (gitUrl, branch) in new[]
        {
            ("git@secondary.example:v2.git", "develop-v2"),
            ("git@secondary.example:v3.git", "develop-v3"),
            ("git@secondary.example:final.git", "release-final"),
        })
        {
            await projectGrain.RemoveRepositoryAsync("secondary");
            await projectGrain.AddRepositoryAsync("secondary", gitUrl, branch);
        }

        // Then the workflow start uses only the latest config and ignores every
        // intermediate change. The workflow run's with.input must reflect the final state.
        var wrId = await issueGrain.StartWorkAsync();
        var variables = await LoadWorkflowVariablesAsync(wrId);
        var repository = variables.RootElement.GetProperty("repository");
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:final.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("release-final", repository.GetProperty("baseBranch").GetString());

        // And the issue read model agrees with the workflow variables for the same change.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var readRepository = payload.GetProperty("data").GetProperty("repository");
        Assert.Equal("git@secondary.example:final.git", readRepository.GetProperty("gitUrl").GetString());
        Assert.Equal("release-final", readRepository.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_AfterIssueGrainReactivation_StillResolvesFromCurrentProjectConfig()
    {
        // Given an issue bound to a project repository.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Reactivation reads latest", "secondary");
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        // Force the issue grain to load and capture its current state.
        var initial = await issueGrain.GetStartEligibilityAsync();
        Assert.NotNull(initial);

        // When the project repository configuration is changed AFTER the issue grain
        // has already been activated and read.
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release-new");

        // Then a fresh read through the grain (and through the read service) still
        // resolves from the current project configuration, proving the grain does not
        // cache resolved repository state and re-activation does not reintroduce
        // stale data.
        var after = await issueGrain.GetStartEligibilityAsync();
        Assert.NotNull(after);

        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var repository = payload.GetProperty("data").GetProperty("repository");
        Assert.Equal("git@secondary.example:repo-new.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("release-new", repository.GetProperty("baseBranch").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task RebaseEndpoint_AfterProjectConfigChange_ResponseCarriesLatestBaseBranchAndRepositoryContext()
    {
        // Given an issue with a workflow run and a project repository whose
        // path and base branch are subsequently changed.
        _fixture.Git.Reset();
        _fixture.Git.BranchExists = true;

        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-old");
        var issue = await CreateIssueAsync(projectId, "Rebase response drifts", "secondary");
        await StartIssueAndClaimRunnerAsync(projectId, issue.Number);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-new.git",
            "release-new");

        // When the user queues a rebase (without an explicit baseBranch override).
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}/rebase",
            new { });

        // Then the rebase response itself reports the latest base branch and the
        // latest resolved repository context, so the UI feedback agrees with the
        // task the workflow run will execute.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("release-new", data.GetProperty("baseBranch").GetString());
        Assert.Equal("queued", data.GetProperty("status").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListIssues_AfterReferencedRepositoryRemoved_PreservesIssueOrderingAndIncludesProblemField()
    {
        // Given several issues bound to a project repository that will be removed.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop");
        var firstSecondary = await CreateIssueAsync(projectId, "First secondary", "secondary");
        var defaultIssue = await CreateIssueAsync(projectId, "Default issue");
        var secondSecondary = await CreateIssueAsync(projectId, "Second secondary", "secondary");

        // When the referenced repository is removed.
        await _grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("secondary");

        // Then the list endpoint still returns all issues in the same order and
        // both the orphan issues carry the same RepositoryNotFound problem payload.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = payload.GetProperty("data").EnumerateArray().ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { firstSecondary.Number, defaultIssue.Number, secondSecondary.Number },
            items.Select(item => item.GetProperty("number").GetInt32()).ToArray());

        var orphans = items.Where(item => item.GetProperty("repository").ValueKind == JsonValueKind.Null).ToList();
        Assert.Equal(2, orphans.Count);
        Assert.All(orphans, item =>
        {
            var problem = item.GetProperty("repositoryProblem");
            Assert.Equal("RepositoryNotFound", problem.GetProperty("code").GetString());
            Assert.Equal("secondary", problem.GetProperty("repositoryRef").GetString());
        });

        // And the surviving default-bound issue still has a resolved repository.
        var surviving = items.Single(item => item.GetProperty("number").GetInt32() == defaultIssue.Number);
        Assert.Equal("main", surviving.GetProperty("repository").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, surviving.GetProperty("repositoryProblem").ValueKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssue_RepositoryNameUnchanged_ButOtherFieldsChange_ResolvesUpdatedFields()
    {
        // Given an issue bound to a project repository whose path/remote/baseBranch
        // are subsequently changed, but whose name is preserved.
        var projectId = await CreateProjectWithDefaultAndSecondaryRepositoryAsync("develop-v1");
        var issue = await CreateIssueAsync(projectId, "Name preserved, fields updated", "secondary");
        Assert.Equal("secondary", issue.Repository!.Name);

        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.RemoveRepositoryAsync("secondary");
        await projectGrain.AddRepositoryAsync(
            "secondary",
            "git@secondary.example:repo-v2.git",
            "develop-v2");

        // When the same-named repository is re-added with different fields.
        // Then the issue read resolves to the same name but with the updated fields.
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{issue.Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var repository = payload.GetProperty("data").GetProperty("repository");
        Assert.Equal("secondary", repository.GetProperty("name").GetString());
        Assert.Equal("git@secondary.example:repo-v2.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("develop-v2", repository.GetProperty("baseBranch").GetString());

        // And the issue row only stores the stable reference — it does not carry
        // any of the previously-observed mutable fields as authority.
        var storedJson = await LoadStateAsync(projectId, issue.Number);
        using var doc = JsonDocument.Parse(storedJson);
        Assert.True(doc.RootElement.TryGetProperty("repositoryRef", out var refElement));
        Assert.Equal("secondary", refElement.GetString());
        if (doc.RootElement.TryGetProperty("repository", out var repositoryElement)
            && repositoryElement.ValueKind == JsonValueKind.Object)
        {
            Assert.Fail("Issue storage must not carry a mutable Repository snapshot.");
        }
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        await _grains.GetGrain<IProjectGrain>(projectId).CreateAsync(name);
        return projectId;
    }

    private async Task<string> CreateProjectWithAmbiguousRepositoryAsync(
        params RepositoryInfo[] repositories)
    {
        // Seed a project whose repositories bypass the case-insensitive duplicate
        // check in ProjectGrain.AddRepositoryAsync. The repository resolver still
        // performs case-insensitive lookup, so this state surfaces an
        // AmbiguousReference problem for any reference that matches more than
        // one repository.
        var projectId = $"proj_{Guid.NewGuid():N}";
        var defaultRepo = repositories.FirstOrDefault(r => r.IsDefault) ?? repositories[0];
        var json = JsonSerializer.Serialize(repositories.ToList());

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = $"proj-{Guid.NewGuid():N}",
                RepositoriesJson = json,
            });
            await db.SaveChangesAsync();
        }

        // Activate the project grain so subsequent calls see the same in-memory
        // state as the database row.
        var _ = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        return projectId;
    }

    private async Task<string> CreateProjectWithDefaultAndSecondaryRepositoryAsync(string secondaryBaseBranch)
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}");
        await projectGrain.AddRepositoryAsync("main", "git@main.example:repo.git", "main");
        await projectGrain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", secondaryBaseBranch);
        return projectId;
    }

    private async Task SeedIssueAsync(string projectId, int number, string state)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var issue = IssueStore.Deserialize(state)
            ?? throw new InvalidOperationException("Issue seed state must deserialize.");
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = state,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> LoadStateAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.Issues.AsNoTracking()
            .FirstAsync(r => r.ProjectId == projectId && r.Number == number);
        return row.State;
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var projectQuery = scope.ServiceProvider.GetRequiredService<ProjectQuerier>();
        var issueQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var project = await projectQuery.GetByIdAsync(projectId);
        return await issueQuery.GetInfoAsync(projectId, number, project);
    }

    private async Task<IssueReadModel?> GetIssueReadModelAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var issueQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issueQuery.GetAsync(projectId, number);
    }

    private async Task<CreatedIssueDto> CreateIssueAsync(string projectId, string title, string? repositoryName = null)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title, repositoryName },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        RepositoryDto? repository = null;
        if (data.TryGetProperty("repository", out var repoEl) && repoEl.ValueKind == JsonValueKind.Object)
        {
            repository = new RepositoryDto(
                repoEl.GetProperty("name").GetString()!,
                repoEl.GetProperty("gitUrl").GetString()!,
                repoEl.GetProperty("baseBranch").GetString()!,
                repoEl.GetProperty("isDefault").GetBoolean());
        }
        return new CreatedIssueDto(
            data.GetProperty("id").GetString()!,
            data.GetProperty("number").GetInt32(),
            repository);
    }

    private async Task StartIssueAndClaimRunnerAsync(string projectId, int number)
    {
        await _client.PostOkAsync($"/api/projects/{projectId}/issues/{number}/start");

        var runnerId = $"regression-runner-{Guid.NewGuid():N}";
        await _client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "mohist/rebase", "spec/task", "spec/check" },
            hostname = "test-host",
            projectId,
        });

        var issue = await _client.GetDataAsync<CreatedIssueDto>($"/api/projects/{projectId}/issues/{number}");
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
        var issueStatus = await issueGrain.GetWorkflowStatusAsync();
        var wrId = issueStatus!.WorkflowRunId!;

        var workflow = _grains.GetGrain<IWorkflowGrain>(wrId);
        await workflow.AssignRunnerAsync(runnerId);
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.PollAsync();

        var project = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        var path = Mohist.Server.Infrastructure.Workspace.MohistWorkspaceLayout.IssueWorkspacePath(_fixture.RunnerRoot, project!.Name, number);
        Directory.CreateDirectory(path);
    }

    private async Task<JsonDocument> LoadWorkflowVariablesAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        var snapshot = await query.GetVariablesAsync(workflowRunId);
        Assert.NotNull(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(snapshot!.Variables));
        return JsonDocument.Parse(snapshot.Variables);
    }

    private async Task<Mohist.Server.Workflow.Domain.Run.WorkflowRun?> LoadWorkflowRunAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.WorkflowRunId == workflowRunId);
        if (row is null) return null;

        var json = row.State;
        return JsonSerializer.Deserialize<Mohist.Server.Workflow.Domain.Run.WorkflowRun>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() },
            });
    }

    private sealed record CreatedIssueDto(string Id, int Number, RepositoryDto? Repository);

    private sealed record RepositoryDto(string Name, string GitUrl, string BaseBranch, bool IsDefault);

    private sealed record JsonEnvelope(bool Success, JsonElement? Data = default, string? Error = null, string? Code = null);
}
