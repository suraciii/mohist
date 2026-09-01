using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using HttpClient = System.Net.Http.HttpClient;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

public class IssueWorkflowLifecycleSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly HttpClient _client;
    private readonly string _connectionString;

    public IssueWorkflowLifecycleSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _grains = fixture.Grains;
        _services = fixture.Services;
        _client = fixture.Client;
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public async Task CancelAsync_WhenIssueIsDoneWithPreservedReference_RejectsViaCloseOnly()
    {
        // Contract: a Done issue preserves its workflowRunId as an
        // execution fact. CancelAsync must not run the "cannot close
        // while workflow is running" check (the workflow is not
        // active); Close() itself rejects Done/archived with a
        // different error. The grain-level "CancelAsync rejects while
        // running" / "CancelAsync transitions when stopped" assertions
        // are sunk into IssueWorkflowLifecycleGrainSpecs (batch C).
        var (_, _, _, issueKey, wrId) = await SeedIssueInProgressAsync();

        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.CompleteWorkAsync(wrId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => issue.CancelAsync());
        Assert.Contains("cannot close", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflow is", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteWorkAsync_ForIssueNotInProgress_StaysInCurrentState()
    {
        // Contract: calling CompleteWorkAsync on an issue that was never
        // started is a no-op — the issue stays in Backlog. The
        // grain-level "CompleteWorkAsync transitions in-progress to done"
        // assertion is sunk into IssueWorkflowLifecycleGrainSpecs
        // (batch C).
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);

        var wrId = $"wr_{Guid.NewGuid():N}";
        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        await issue.CompleteWorkAsync(wrId);

        var final = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(final);
        Assert.Equal("backlog", final!.Status);
    }

    [Fact]
    public async Task StartWorkAsync_MissingVerificationCommand_DoesNotMutateIssue()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectName = $"missing-command-{Guid.NewGuid():N}";
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectName,
                RepositoriesJson = JSON.Serialize(new[]
                {
                    new RepositoryInfo
                    {
                        Name = "main",
                        GitUrl = "git@example.com:main.git",
                        BaseBranch = "main",
                        IsDefault = true,
                    },
                }),
                RepositoryRevision = 1,
                CreatedAt = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
                VerificationCommand = null,
            });
            await db.SaveChangesAsync();
        }

        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        var issue = _grains.GetGrain<IIssueGrain>(issueKey);

        var ex = await Assert.ThrowsAsync<ProjectVerificationConfigurationMissingException>(
            () => issue.StartWorkAsync());
        Assert.Contains(projectId, ex.Message);

        var current = await GetIssueInfoAsync(projectId, issueNumber);
        Assert.NotNull(current);
        Assert.Equal("backlog", current!.Status);
        Assert.Null(current.WorkflowRunId);

        await using var verifyScope = _services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await verifyDb.WorkflowRuns
            .Where(row => row.MetadataProjectId == projectId)
            .ToListAsync());
    }

    [Fact]
    public async Task StartWorkAsync_ProjectCommandEditAfterBinding_ReplayKeepsOriginalCapturedCommand()
    {
        var (projectId, _) = await SeedProjectAsync();
        var (issueKey, issueNumber) = await CreateIssueInBacklogAsync(projectId);
        var issue = _grains.GetGrain<IIssueGrain>(issueKey);
        var workflowRunId = await issue.StartWorkAsync();
        var original = await LoadWorkflowRunAsync(workflowRunId);
        Assert.NotNull(original);
        Assert.Equal("git diff --check", original!.VerificationCommand);

        await _grains.GetGrain<IProjectGrain>(projectId)
            .SetVerificationCommandAsync("dotnet test");
        var editedProject = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        Assert.Equal("dotnet test", editedProject!.VerificationCommand);

        await MarkIssueWorkStartedUndeliveredAsync(projectId, issueNumber);
        await DispatchEventsAsync();

        var replayed = await LoadWorkflowRunAsync(workflowRunId);
        Assert.NotNull(replayed);
        Assert.Equal("git diff --check", replayed!.VerificationCommand);
    }

    [Fact]
    public async Task NoWorkflowIssue_StartDoneCloseAndReopen_NeverCreatesWorkflowRun()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"no-workflow-{Guid.NewGuid():N}");
        var created = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = "External delivery",
            isDraft = false,
            noWorkflow = true,
        });

        var beforeStart = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.True(beforeStart!.NoWorkflow);
        Assert.Equal("none", beforeStart.WorkflowProfileMode);
        Assert.Null(beforeStart.WorkflowProfileId);

        using var start = await _client.PostAsync($"/api/projects/{project.Id}/issues/{created.Number}/start", null);
        start.EnsureSuccessStatusCode();
        var started = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(started);
        Assert.Equal("in_progress", started!.Status);
        Assert.True(started.NoWorkflow);
        Assert.Null(started.WorkflowRunId);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var matchingRuns = (await db.WorkflowRuns.ToListAsync()).Where(row =>
                row.State.Contains($"\"projectId\":\"{project.Id}\"", StringComparison.Ordinal)
                && row.State.Contains($"\"issueNumber\":{created.Number}", StringComparison.Ordinal)).ToList();
            Assert.True(matchingRuns.Count == 0, string.Join("\n", matchingRuns.Select(row => row.State)));
            Assert.Empty(await db.Workspaces.Where(row => row.ProjectId == project.Id && row.Name == $"issue-{created.Number}").ToListAsync());
        }

        using var done = await _client.PostAsync($"/api/projects/{project.Id}/issues/{created.Number}/done", null);
        done.EnsureSuccessStatusCode();
        Assert.Equal("done", (await GetIssueInfoAsync(project.Id, created.Number))!.Status);

        var cancelled = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = "External cancellation",
            isDraft = false,
            noWorkflow = true,
        });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{cancelled.Number}/start");
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{cancelled.Number}/close");
        Assert.Equal("cancelled", (await GetIssueInfoAsync(project.Id, cancelled.Number))!.Status);
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{cancelled.Number}/reopen");
        var reopened = await GetIssueInfoAsync(project.Id, cancelled.Number);
        Assert.Equal("backlog", reopened!.Status);
        Assert.True(reopened.NoWorkflow);
    }

    [Fact]
    public async Task WorkflowSelectionPatch_ReplacesThePriorModeAndOmittedFieldsPreserveIt()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"workflow-selection-{Guid.NewGuid():N}");
        var created = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = "Selection transitions",
            workflowProfileId = "mohist/local",
        });

        using (var toNone = await _client.PatchAsJsonAsync(
                   $"/api/projects/{project.Id}/issues/{created.Number}",
                   new { noWorkflow = true }))
        {
            toNone.EnsureSuccessStatusCode();
        }
        var noWorkflow = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.True(noWorkflow!.NoWorkflow);
        Assert.Null(noWorkflow.WorkflowProfileId);

        using (var toProfile = await _client.PatchAsJsonAsync(
                   $"/api/projects/{project.Id}/issues/{created.Number}",
                   new { workflowProfileId = "mohist/local" }))
        {
            toProfile.EnsureSuccessStatusCode();
        }
        var explicitProfile = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.False(explicitProfile!.NoWorkflow);
        Assert.Equal("mohist/local", explicitProfile.WorkflowProfileId);

        using (var unrelated = await _client.PatchAsJsonAsync(
                   $"/api/projects/{project.Id}/issues/{created.Number}",
                   new { title = "Selection preserved" }))
        {
            unrelated.EnsureSuccessStatusCode();
        }
        var preserved = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.False(preserved!.NoWorkflow);
        Assert.Equal("mohist/local", preserved.WorkflowProfileId);
    }

    [Fact]
    public async Task NoWorkflowIssue_AfterStartRejectsWorkflowSelectionWithStableConflict()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"no-workflow-lock-{Guid.NewGuid():N}");
        var created = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = "Locked external delivery",
            isDraft = false,
            noWorkflow = true,
        });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{created.Number}/start");

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{created.Number}",
            new { workflowProfileId = "mohist/local" });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("workflow_profile_locked", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartIssue_WithIncompletePrerequisite_IsRejectedByWorkflowGate()
    {
        // Contract: POST /api/projects/{ref}/issues/{n}/start on an
        // issue with an undelivered prerequisite returns 400. The
        // grain-level prereq-blocks-StartWorkAsync assertion is sunk
        // into IssueWorkflowLifecycleGrainSpecs (batch C).
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"web-prereq-gate-{Guid.NewGuid():N}");

        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var prereq = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Gate prereq", projectId = project.Id, isDraft = false });
        var dependent = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = "Gate dependent", projectId = project.Id, isDraft = false });
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{dependent.Number}/prerequisites", new { prerequisiteNumber = prereq.Number });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{dependent.Number}/start", null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(string projectId, string projectName)> SeedProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var name = $"mohist-local-{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        await projectGrain.CreateAsync(name, new RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist-local.git",
            BaseBranch = "main",
            IsDefault = true,
        }, "git diff --check");
        return (id, name);
    }

    private async Task<(string issueKey, int number)> CreateIssueInBacklogAsync(string projectId)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueKey = GrainKey.Issue(new IssueKey(projectId, number));
        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        await grain.CreateAsync(projectId, number, "Lifecycle", null, null, null, isDraft: false);
        return (issueKey, number);
    }

    private async Task<(string projectId, string projectName, int number, string issueKey, string wrId)> SeedIssueInProgressAsync()
    {
        var (projectId, projectName) = await SeedProjectAsync();
        var (issueKey, number) = await CreateIssueInBacklogAsync(projectId);

        var grain = _grains.GetGrain<IIssueGrain>(issueKey);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();

        return (projectId, projectName, number, issueKey, wrId);
    }

    private async Task DispatchEventsAsync()
    {
        await _fixture.Services.GetRequiredService<IEventDispatcher>().DrainAsync();
    }

    private async Task DeleteWorkflowRunAsync(string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.FindAsync(workflowRunId)
            ?? throw new InvalidOperationException($"Workflow run {workflowRunId} was not stored");
        db.WorkflowRuns.Remove(row);
        await db.SaveChangesAsync();
    }

    private async Task MarkIssueWorkStartedUndeliveredAsync(string projectId, int issueNumber)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.IssueEvents
            .Where(entry => entry.Source == IssueEventPersistence.IssueSource(projectId, issueNumber)
                && entry.Type == EventCatalog.ReverseDns.IssueWorkStarted)
            .OrderByDescending(entry => entry.Id)
            .FirstAsync();
        row.DispatchedAt = null;
        await db.SaveChangesAsync();
    }

    private async Task AssertIssueWorkStartedDispatchedAsync(string projectId, int issueNumber)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.IssueEvents
            .AsNoTracking()
            .Where(entry => entry.Source == IssueEventPersistence.IssueSource(projectId, issueNumber)
                && entry.Type == EventCatalog.ReverseDns.IssueWorkStarted)
            .OrderByDescending(entry => entry.Id)
            .FirstAsync();
        Assert.NotNull(row.DispatchedAt);
    }

    private async Task<WorkflowRun?> LoadWorkflowRunAsync(string workflowRunId)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
        return row is null ? null : JSON.Deserialize<WorkflowRun>(row.State);
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await querier.GetInfoAsync(projectId, number);
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number);
}
