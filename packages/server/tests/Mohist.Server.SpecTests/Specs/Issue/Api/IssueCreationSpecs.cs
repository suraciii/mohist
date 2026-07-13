using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssueLifecycle")]
public class IssueCreationSpecs
{
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IssueCreationSpecs(MohistIntegrationFixture fixture)
    {
        _grains = fixture.Grains;
        _services = fixture.Services;
        _client = fixture.Client;
    }

    private async Task<ProjectInfo> SetupProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        var project = await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}");
        await projectGrain.AddRepositoryAsync("main", $"file://{Guid.NewGuid():N}", "main");
        return project;
    }

    private async Task<IssueInfo> CreateIssueAsync(string projectId, string title, string? body = null, IReadOnlyDictionary<string, string>? labels = null, string? priority = null, string? risk = null, bool isDraft = false, int[]? prerequisiteNumbers = null, string? repositoryRef = null)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var issueId = $"issue_{Guid.NewGuid():N}";
        var grain = _grains.GetGrain<IIssueGrain>(issueId);
        await grain.CreateAsync(projectId, number, title, body, labels, priority, repositoryRef, issueId, risk, isDraft, null, null, prerequisiteNumbers);
        return (await GetIssueInfoAsync(projectId, number))!;
    }

    private async Task<IssueInfo?> GetIssueInfoAsync(string projectId, int number)
    {
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetInfoAsync(projectId, number);
    }

    private async Task<IssueReadModel?> GetIssueReadModelAsync(string projectId, int number)
    {
        var project = await _grains.GetGrain<IProjectGrain>(projectId).GetAsync();
        using var scope = _services.CreateScope();
        var issues = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        return await issues.GetAsync(projectId, number, project);
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> GetWorkflowEventsAsync(string workflowRunId)
    {
        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        return (await events.ListAsync(workflowRunId)).ToList();
    }

    [Fact]
    public async Task CreateIssue_ReturnsInfoWithNumber()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Test issue", "body");

        Assert.Equal(1, issue.Number);
        Assert.Equal("Test issue", issue.Title);
        Assert.Equal("body", issue.Body);
        Assert.Equal("backlog", issue.Status);
        Assert.Equal("active", issue.Health);
        Assert.Equal(project.Id, issue.ProjectId);
        Assert.StartsWith("issue_", issue.Id);
        Assert.Equal("mohist/local", issue.WorkflowProfileId);
    }

    // Regression guard for the bug that left IssueEvents permanently empty:
    // SaveIssueAsync snapshotted PendingEvents by reference, then
    // ClearPendingEvents() drained the same list, so the publish path
    // no-op'd on an empty collection and no issue lifecycle CloudEvent ever
    // reached EventStore. WorkflowRunEvents and EpicEvents worked because
    // their drain paths snapshot via ToList(). This spec is the only place
    // that asserts the issue→IssueEvents append actually happens end-to-end
    // through the real grain + EventStore.
    [Fact]
    public async Task CreateIssue_PersistsCreatedEventToIssueEvents()
    {
        var project = await SetupProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Event persistence probe");

        using var scope = _services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var stored = await events.ListIssueEventsAsync(issue.Id);

        // issue-361 T-004: events now append inside the state transaction
        // exactly once per IssueEvent recorded by the aggregate, so the
        // single IssuedCreated event lands as a single row.
        var created = Assert.Single(stored);
        Assert.Equal("com.mohist.issue.created", created.Envelope.Type);
        Assert.Equal($"/mohist/issues/{issue.Id}", created.Envelope.Source.ToString());
    }

    [Fact]
    public async Task StartWorkflow_WithProjectContext_DispatchesProjectVariables()
    {
        // The runner is a global resource. Prior tests in this collection
        // leave runners registered, which can race with this test's poll.
        var registry = _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);

        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Context");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project"));

        Assert.StartsWith("wr_", wrId);
        Assert.DoesNotContain(project.Id, wrId);
        Assert.False(wrId.EndsWith($"_{created.Number}", StringComparison.Ordinal));

        var runnerId = $"runner-variable-test-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));

        var work = await PollWorkForWorkflowAsync(runner, runnerId, wrId);

        Assert.Equal(wrId, work.WorkflowRunId);
        Assert.NotNull(work.Variables);
        Assert.Contains("My Project", work.Variables);
        Assert.Contains("repository", work.Variables);
        Assert.Contains("main", work.Variables);
        Assert.Contains("workspace", work.Variables);
        Assert.DoesNotContain("project.path", work.Variables);
        Assert.DoesNotContain("project.baseBranch", work.Variables);

        await runner.UnregisterAsync();
    }

    [Fact]
    public async Task StartWorkflow_UsesProjectDefaultTemplate()
    {
        // The runner is a global resource. Prior tests in this collection
        // leave runners registered, which can race with this test's poll.
        var registry = _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);

        var project = await SetupProjectAsync();
        using (var scope = _services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<ProjectWorkflowProfileManager>();
            await profiles.CreateTemplateAsync(project.Id, """
                id: project-custom
                stages:
                  - stage: custom-stage
                    tasks:
                      - id: custom-task
                        title: Custom task
                        uses: spec/task
                        with:
                          prompt: Project template prompt
                    checks: []
                """);
            await profiles.SetDefaultTemplateAsync(project.Id, "project-custom");
        }

        var created = await CreateIssueAsync(project.Id, "Project template issue");
        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        var wrId = await grain.StartWorkAsync();

        var runnerId = $"runner-template-test-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));

        var work = await PollWorkForWorkflowAsync(runner, runnerId, wrId);

        Assert.Equal(wrId, work.WorkflowRunId);
        Assert.Equal("custom-stage", work.Stage);
        Assert.StartsWith("custom-task.", work.WorkId);
        Assert.Contains("Project template prompt", work.With);

        await runner.UnregisterAsync();
    }

    [Fact]
    public async Task CreateIssue_SequentialNumbers()
    {
        var project = await SetupProjectAsync();

        var first = await CreateIssueAsync(project.Id, "First");
        var second = await CreateIssueAsync(project.Id, "Second");

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task CreateIssue_WithLabelsAndPriority()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(
            project.Id,
            "Labeled",
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["priority"] = "p0",
            },
            priority: "p0");

        Assert.Equal("frontend", issue.Labels["stream"]);
        Assert.Equal("p0", issue.Labels["priority"]);
        Assert.Equal("p0", issue.Priority);
    }

    [Fact]
    public async Task Update_ChangesTitleAndBody()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Original", "old body");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        await grain.UpdateAsync("Updated", "new body");
        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal("Updated", info.Title);
        Assert.Equal("new body", info.Body);
    }

    [Fact]
    public async Task Close_ActiveIssue_CancelsIssueWithoutRewritingLifecycleToWorkflowStage()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Closable");

        var grain = _grains.GetGrain<IIssueGrain>(created.Id);
        var wrId = await grain.StartWorkAsync();

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("test-stop");

        await grain.CancelAsync();

        var info = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(info);
        Assert.Equal("cancelled", info.Status);
        Assert.Equal("cancelled", info.Health);
    }

    [Fact]
    public async Task StartIssue_WhenWorkIsAlreadyActive_ReusesExistingWorkflow()
    {
        var project = await SetupProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Start once");

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        var first = await GetIssueInfoAsync(project.Id, issue.Number);
        var firstRunId = first?.WorkflowRunId;
        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        var second = await GetIssueInfoAsync(project.Id, issue.Number);

        Assert.NotNull(firstRunId);
        Assert.Equal(firstRunId, second?.WorkflowRunId);
    }

    [Fact]
    public async Task StartIssue_AfterWorkWasStopped_CreatesNewWorkflow()
    {
        var project = await SetupProjectAsync();
        var issue = await CreateIssueAsync(project.Id, "Restart work");

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        var first = await GetIssueInfoAsync(project.Id, issue.Number);
        var firstRunId = first?.WorkflowRunId;
        Assert.NotNull(firstRunId);
        await _grains.GetGrain<IWorkflowGrain>(firstRunId!).StopAsync("test-stop");

        await _client.PostOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start");
        var restarted = await GetIssueInfoAsync(project.Id, issue.Number);

        var restartedRunId = restarted?.WorkflowRunId;
        Assert.NotNull(restartedRunId);
        Assert.NotEqual(firstRunId, restartedRunId);
    }

    [Fact]
    public async Task StartIssue_AfterReferencedRepositoryIsRemoved_ReturnsConflictWithoutCreatingWork()
    {
        var project = await SetupProjectAsync();
        var projectGrain = _grains.GetGrain<IProjectGrain>(project.Id);
        await projectGrain.AddRepositoryAsync("secondary", $"file://{Guid.NewGuid():N}", "release");
        var issue = await CreateIssueAsync(project.Id, "Removed repository", repositoryRef: "secondary");
        await projectGrain.RemoveRepositoryAsync("secondary");

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null((await GetIssueInfoAsync(project.Id, issue.Number))?.WorkflowRunId);
    }

    [Fact]
    public async Task Cancel_ActiveIssue_RemovesWorkflowFromRunnerPoll()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Cancelable");

        var issue = _grains.GetGrain<IIssueGrain>(created.Id);
        var workflowRunId = await issue.StartWorkAsync(new WorkflowProjectContext(project.Id, project.Name, RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));

        var runnerId = $"runner-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));

        await PollAnyWorkAsync(runner, runnerId);

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await wfGrain.StopAsync("user-cancel");

        await issue.CancelAsync();

        var info = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(info);
        Assert.Equal("cancelled", info!.Status);
        Assert.Equal("cancelled", info.Health);

        Assert.Null(await runner.PollAsync(_services));

        var events = await GetWorkflowEventsAsync(workflowRunId);
        Assert.Single(events, e => e.Envelope.Type == "com.mohist.workflow.run.stopped");
    }

    [Fact]
    public async Task DifferentProjects_IndependentNumbering()
    {
        var project1 = await SetupProjectAsync();
        var project2 = await SetupProjectAsync();

        var issue1 = await CreateIssueAsync(project1.Id, "P1-Issue");
        var issue2 = await CreateIssueAsync(project2.Id, "P2-Issue");

        Assert.Equal(1, issue1.Number);
        Assert.Equal(1, issue2.Number);
    }

    [Fact]
    public async Task CreateIssueApi_WithRisk_ReturnsRiskAcrossCreateAndRead()
    {
        var project = await SetupProjectAsync();

        var created = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Risked", isDraft = false, risk = "high" });
        var fetched = await _client.GetDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues/{created.Number}");

        Assert.Equal("high", created.Risk);
        Assert.Equal("high", fetched.Risk);
    }

    [Fact]
    public async Task CreateIssueApi_WithPrerequisiteNumbers_BindsCamelCaseAndReturnsReadModels()
    {
        var project = await SetupProjectAsync();
        var prereqA = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "API prereq A", isDraft = false });
        var prereqB = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "API prereq B", isDraft = false });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "API dependent", isDraft = false, prerequisiteNumbers = new[] { prereqA.Number, prereqB.Number } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<CreateIssueApiDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        var created = Assert.IsType<CreateIssueApiDto>(envelope.Data);
        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, created.PrerequisiteNumbers);
        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, created.Prerequisites.Select(summary => summary.Number).OrderBy(number => number));
        Assert.All(created.Prerequisites, summary =>
        {
            Assert.Equal("backlog", summary.Status);
            Assert.Equal("active", summary.Health);
            Assert.False(summary.Completed);
        });
        Assert.False(created.CanStart);
        Assert.NotNull(created.Blocker);
        Assert.Equal("waiting-for", created.Blocker!.Kind);
        Assert.NotNull(created.Blocker.Issue);
        Assert.Equal(prereqA.Number, created.Blocker.Issue!.Number);
    }

    [Fact]
    public async Task CreateIssueApi_WithoutPrerequisiteNumbers_ReturnsEmptyPrerequisiteReadModels()
    {
        var project = await SetupProjectAsync();

        var created = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "No prereqs API", isDraft = false });

        Assert.Empty(created.PrerequisiteNumbers);
        Assert.Empty(created.Prerequisites);
    }

    [Fact]
    public async Task CreateIssueApi_WithEmptyPrerequisiteNumbers_ReturnsEmptyPrerequisiteReadModels()
    {
        var project = await SetupProjectAsync();

        var created = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Empty prereqs API", isDraft = false, prerequisiteNumbers = Array.Empty<int>() });

        Assert.Empty(created.PrerequisiteNumbers);
        Assert.Empty(created.Prerequisites);
    }

    [Fact]
    public async Task CreateIssueApi_WithDuplicatePrerequisiteNumbers_CollapsesDuplicates()
    {
        var project = await SetupProjectAsync();
        var prereq = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Duplicate API prereq", isDraft = false });

        var dependent = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Duplicate API dependent", isDraft = false, prerequisiteNumbers = new[] { prereq.Number, prereq.Number, prereq.Number } });

        Assert.Equal(new[] { prereq.Number }, dependent.PrerequisiteNumbers);
        Assert.Single(dependent.Prerequisites);
    }

    [Fact]
    public async Task CreateIssueApi_WithNonexistentPrerequisite_ReturnsBadRequestAndLeavesNoIssue()
    {
        var project = await SetupProjectAsync();
        await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Existing API prereq", isDraft = false });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "Rejected API dependent", isDraft = false, prerequisiteNumbers = new[] { 1, 999_999 } },
            JsonOptions);

        await AssertCreatePrerequisiteFailureAsync(project.Id, response, "prerequisite_not_found", "999999");
        using var getAttempt = await _client.GetAsync($"/api/projects/{project.Id}/issues/2");
        Assert.Equal(HttpStatusCode.NotFound, getAttempt.StatusCode);
        var issues = await _client.GetDataAsync<CreateIssueApiDto[]>($"/api/projects/{project.Id}/issues?all=true");
        Assert.DoesNotContain(issues, issue => issue.Title == "Rejected API dependent");

        var next = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "After rejected API dependent", isDraft = false });
        Assert.Equal(3, next.Number);
    }

    [Fact]
    public async Task CreateIssueApi_WithCrossProjectPrerequisite_ReturnsBadRequestAndLeavesNoIssue()
    {
        var sourceProject = await SetupProjectAsync();
        var targetProject = await SetupProjectAsync();
        await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{sourceProject.Id}/issues",
            new { title = "Source one", isDraft = false });
        await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{sourceProject.Id}/issues",
            new { title = "Source two", isDraft = false });
        var sourceOnly = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{sourceProject.Id}/issues",
            new { title = "Source three", isDraft = false });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{targetProject.Id}/issues",
            new { title = "Rejected cross-project dependent", isDraft = false, prerequisiteNumbers = new[] { sourceOnly.Number } },
            JsonOptions);

        await AssertCreatePrerequisiteFailureAsync(targetProject.Id, response, "prerequisite_not_found", sourceOnly.Number.ToString());
        using var getAttempt = await _client.GetAsync($"/api/projects/{targetProject.Id}/issues/1");
        Assert.Equal(HttpStatusCode.NotFound, getAttempt.StatusCode);
        var issues = await _client.GetDataAsync<CreateIssueApiDto[]>($"/api/projects/{targetProject.Id}/issues?all=true");
        Assert.Empty(issues);
    }

    [Fact]
    public async Task CreateIssueApi_WithSelfReferencingPrerequisite_ReturnsBadRequestAndLeavesNoIssue()
    {
        var project = await SetupProjectAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "Rejected self dependent", isDraft = false, prerequisiteNumbers = new[] { 1 } },
            JsonOptions);

        await AssertCreatePrerequisiteFailureAsync(project.Id, response, "circular_prerequisite", "1");
        using var getAttempt = await _client.GetAsync($"/api/projects/{project.Id}/issues/1");
        Assert.Equal(HttpStatusCode.NotFound, getAttempt.StatusCode);
        var issues = await _client.GetDataAsync<CreateIssueApiDto[]>($"/api/projects/{project.Id}/issues?all=true");
        Assert.Empty(issues);
    }

    [Fact]
    public async Task AddPrerequisiteApi_RejectsActualCircularDependency()
    {
        var project = await SetupProjectAsync();
        var first = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "First cycle node", isDraft = false });
        var second = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Second cycle node", isDraft = false });

        var afterFirstAdd = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues/{first.Number}/prerequisites",
            new { prerequisiteNumber = second.Number });
        Assert.Equal(new[] { second.Number }, afterFirstAdd.PrerequisiteNumbers);

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues/{second.Number}/prerequisites",
            new { prerequisiteNumber = first.Number },
            JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Contains("cycle", envelope.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var unchanged = await _client.GetDataAsync<CreateIssueApiDto>($"/api/projects/{project.Id}/issues/{second.Number}");
        Assert.Empty(unchanged.PrerequisiteNumbers);
    }

    [Fact]
    public async Task CreateIssueApi_WithCompletedPrerequisite_ReturnsOpenStartGateInCreatedResponse()
    {
        var project = await SetupProjectAsync();
        var prereq = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Completed API prereq", isDraft = false });
        var prereqInfo = await GetIssueInfoAsync(project.Id, prereq.Number);
        var prereqGrain = _grains.GetGrain<IIssueGrain>(prereqInfo!.Id);
        var wrId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(
            project.Id,
            project.Name,
            RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));
        await prereqGrain.CompleteWorkAsync(wrId);

        var dependent = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Dependent of completed API prereq", isDraft = false, prerequisiteNumbers = new[] { prereq.Number } });

        Assert.Equal(new[] { prereq.Number }, dependent.PrerequisiteNumbers);
        var summary = Assert.Single(dependent.Prerequisites);
        Assert.Equal("done", summary.Status);
        Assert.Equal("done", summary.Health);
        Assert.True(summary.Completed);
        Assert.True(dependent.CanStart);
        Assert.Null(dependent.Blocker);
    }

    [Fact]
    public async Task CreateIssueApi_AllowsArchivedCompletedPrerequisite()
    {
        var project = await SetupProjectAsync();
        var prereq = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Archived completed prereq", isDraft = false });
        var prereqInfo = await GetIssueInfoAsync(project.Id, prereq.Number);
        var prereqGrain = _grains.GetGrain<IIssueGrain>(prereqInfo!.Id);
        var wrId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(
            project.Id,
            project.Name,
            RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));
        await prereqGrain.CompleteWorkAsync(wrId);
        await prereqGrain.ArchiveAsync();

        var dependent = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Dependent of archived completed prereq", isDraft = false, prerequisiteNumbers = new[] { prereq.Number } });

        var summary = Assert.Single(dependent.Prerequisites);
        Assert.Equal(prereq.Number, summary.Number);
        Assert.True(summary.Completed);
        Assert.True(dependent.CanStart);
    }

    [Fact]
    public async Task SingleAddEndpoint_StillWorks_AfterCreateWithPrerequisitesAdded()
    {
        var project = await SetupProjectAsync();
        var initial = await CreateIssueAsync(project.Id, "Will add later");
        var dependent = await CreateIssueAsync(project.Id, "Dependent", prerequisiteNumbers: [initial.Number]);

        // After create-with-prerequisites, the legacy single-add endpoint
        // must continue to work unchanged.
        var later = await CreateIssueAsync(project.Id, "Added via legacy endpoint");
        var updated = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues/{dependent.Number}/prerequisites",
            new { prerequisiteNumber = later.Number });

        var numbers = updated.PrerequisiteNumbers.OrderBy(n => n).ToArray();
        Assert.Equal(new[] { initial.Number, later.Number }, numbers);
    }

    [Fact]
    public async Task SingleRemoveEndpoint_StillWorks_AfterCreateWithPrerequisitesAdded()
    {
        var project = await SetupProjectAsync();
        var initial = await CreateIssueAsync(project.Id, "Will be removed");
        var dependent = await CreateIssueAsync(project.Id, "Dependent", prerequisiteNumbers: [initial.Number]);

        using var response = await _client.DeleteAsync($"/api/projects/{project.Id}/issues/{dependent.Number}/prerequisites/{initial.Number}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<CreateIssueApiDto>>(JsonOptions);
        Assert.NotNull(envelope);
        var updated = Assert.IsType<CreateIssueApiDto>(envelope!.Data);
        Assert.Empty(updated.PrerequisiteNumbers);
        Assert.Empty(updated.Prerequisites);
        Assert.True(updated.CanStart);
        Assert.Null(updated.Blocker);
    }

    private async Task AssertCreatePrerequisiteFailureAsync(
        string projectId,
        HttpResponseMessage response,
        string expectedCode,
        string expectedMessageFragment)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal(expectedCode, envelope.Code);
        Assert.Contains(expectedMessageFragment, envelope.Error ?? string.Empty, StringComparison.Ordinal);
        var issues = await _client.GetDataAsync<CreateIssueApiDto[]>($"/api/projects/{projectId}/issues?all=true");
        Assert.DoesNotContain(issues, issue => issue.Title.Contains("Rejected", StringComparison.Ordinal));
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null);

    private sealed record CreateIssueApiDto(
        string Id,
        int Number,
        string Title,
        int[] PrerequisiteNumbers,
        CreateIssueApiPrerequisiteDto[] Prerequisites,
        bool CanStart,
        CreateIssueApiBlockerDto? Blocker,
        string? Risk);

    private sealed record CreateIssueApiPrerequisiteDto(
        int Number,
        string Title,
        string Status,
        string Health,
        bool Completed);

    private sealed record CreateIssueApiBlockerDto(string Kind, CreateIssueApiBlockerIssueDto? Issue);

    private sealed record CreateIssueApiBlockerIssueDto(int Number, string Title);

    private async Task<WorkDispatch> PollWorkForWorkflowAsync(IRunnerGrain runner, string runnerId, string workflowRunId)
    {
        var work = await TestWait.ForAsync(
            () => runner.PollAsync(_services),
            value => string.Equals(value?.WorkflowRunId, workflowRunId, StringComparison.Ordinal),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{runnerId}' to receive work for workflow '{workflowRunId}'");
        return work!;
    }

    private async Task<WorkDispatch> PollAnyWorkAsync(IRunnerGrain runner, string runnerId)
    {
        var work = await TestWait.ForAsync(
            () => runner.PollAsync(_services),
            value => value is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{runnerId}' to receive work");
        return work!;
    }

}
