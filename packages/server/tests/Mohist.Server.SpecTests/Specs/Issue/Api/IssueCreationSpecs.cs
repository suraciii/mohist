using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;
[Collection("RunnerMutationIntegration")]
public class IssueCreationSpecs
{
    private readonly IGrainFactory _grains;
    private readonly IServiceProvider _services;
    private readonly string _connectionString;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IssueCreationSpecs(MohistIntegrationFixture fixture)
    {
        _grains = fixture.Grains;
        _services = fixture.Services;
        _connectionString = fixture.ConnectionString;
        _client = fixture.Client;
    }

    private async Task<ProjectInfo> SetupProjectAsync()
    {
        var id = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _grains.GetGrain<IProjectGrain>(id);
        var project = await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}", new Mohist.Server.Project.Domain.RepositoryInfo
        {
            Name = "main",
            GitUrl = "git@example.com:main.git",
            BaseBranch = "main",
            IsDefault = true,
        });
        return project;
    }

    private async Task<IssueInfo> CreateIssueAsync(string projectId, string title, string? body = null, IReadOnlyDictionary<string, string>? labels = null, string? priority = null, string? risk = null, bool isDraft = false, int[]? prerequisiteNumbers = null)
    {
        var number = await _grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = IssueGrain(projectId, number);
        await grain.CreateAsync(projectId, number, title, body, labels, priority, null, risk, isDraft, null, null, prerequisiteNumbers);
        return (await GetIssueInfoAsync(projectId, number))!;
    }

    private IIssueGrain IssueGrain(string projectId, int number) =>
        _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, number)));

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
        Assert.Equal("mohist/local", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task CreateIssue_RejectsIdentityOutsideGrainKey()
    {
        var projectA = await SetupProjectAsync();
        var projectB = await SetupProjectAsync();
        var grain = IssueGrain(projectB.Id, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync(projectA.Id, 1, "cross-project issue", null, null, null));

        Assert.Null(await GetIssueInfoAsync(projectA.Id, 1));
        Assert.Null(await GetIssueInfoAsync(projectB.Id, 1));
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
        var stored = await events.ListIssueEventsAsync(project.Id, issue.Number);

        // issue-361 T-004: events now append inside the state transaction
        // exactly once per IssueEvent recorded by the aggregate, so the
        // single IssuedCreated event lands as a single row.
        var created = Assert.Single(stored);
        Assert.Equal("com.mohist.issue.created", created.Envelope.Type);
        Assert.Equal($"/mohist/projects/{project.Id}/issues/{issue.Number}", created.Envelope.Source.ToString());
    }

    [Fact]
    public async Task CreateIssue_DefaultWorkflowProfile_ComesFromDefaultProfile()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Default profile");

        Assert.Equal("mohist/local", issue.WorkflowProfileId);
    }

    [Fact]
    public async Task StartWorkflow_WithProjectContext_DispatchesProjectVariables()
    {
        // The runner is a global resource. Prior tests in this collection
        // leave runners registered, which can race with this test's poll.
        var registry = _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var staleId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(staleId);
        await WorkflowGrainTestHelpers.ClearBacklogAsync(_grains, _connectionString);

        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Context");

        var grain = IssueGrain(project.Id, created.Number);
        var wrId = await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project"));
        await _grains.GetGrain<IWorkflowGrain>(wrId).EnsureStartedAsync(new WorkflowIssueContext(project.Id, created.Number, null));

        Assert.StartsWith("wr_", wrId);
        Assert.DoesNotContain(project.Id, wrId);
        Assert.False(wrId.EndsWith($"_{created.Number}", StringComparison.Ordinal));

        var runnerId = $"runner-variable-test-{Guid.NewGuid():N}";
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", project.Id));

        var work = await PollWorkForWorkflowAsync(runner, runnerId, wrId);

        Assert.Equal(wrId, work.WorkflowRunId);
        Assert.NotNull(work.Variables);
         Assert.Contains("repository", work.Variables);
         Assert.Contains("workspace", work.Variables);
        Assert.DoesNotContain("project.path", work.Variables);
        Assert.DoesNotContain("project.baseBranch", work.Variables);
         using var variables = JsonDocument.Parse(work.Variables);
         var issue = variables.RootElement.GetProperty("issue");
         Assert.Equal(project.Id, issue.GetProperty("projectId").GetString());
         var repository = variables.RootElement.GetProperty("repository");
        Assert.Equal("main", repository.GetProperty("name").GetString());
        Assert.Equal("git@example.com:main.git", repository.GetProperty("gitUrl").GetString());
        Assert.Equal("main", repository.GetProperty("baseBranch").GetString());

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
    public async Task Querier_ReturnsIssueInfo()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Info test", "desc");

        var info = await GetIssueInfoAsync(project.Id, created.Number);

        Assert.NotNull(info);
        Assert.Equal(created.Number, info.Number);
        Assert.Equal("Info test", info.Title);
        Assert.Equal("desc", info.Body);
    }

    [Fact]
    public async Task Update_ChangesTitleAndBody()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Original", "old body");

        var grain = IssueGrain(project.Id, created.Number);
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

        var grain = IssueGrain(project.Id, created.Number);
        var wrId = await grain.StartWorkAsync();
        await DispatchEventsAsync();

        var wfGrain = _grains.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StopAsync("test-stop");

        await grain.CancelAsync();

        var info = await GetIssueInfoAsync(project.Id, created.Number);
        Assert.NotNull(info);
        Assert.Equal("cancelled", info.Status);
        Assert.Equal("cancelled", info.Health);
    }

    [Fact]
    public async Task Cancel_ActiveIssue_RemovesWorkflowFromRunnerPoll()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Cancelable");

        var issue = IssueGrain(project.Id, created.Number);
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
    public async Task Hydrate_Duplicate_Throws()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Dup");

        var grain = IssueGrain(project.Id, created.Number);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CreateAsync(project.Id, 999, "dup", null, null, null, null));
    }

    [Fact]
    public async Task IssueWorkflowStatus_ProjectsDefaultChangeDirOutsideWorkflowStatus()
    {
        var project = await SetupProjectAsync();
        var created = await CreateIssueAsync(project.Id, "Add Search");

        var grain = IssueGrain(project.Id, created.Number);
        await grain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", RepositoryBaseBranch: "main"));

        var status = await grain.GetWorkflowStatusAsync();

        Assert.NotNull(status);
        Assert.Equal($"openspec/changes/issue-{created.Number}", status.ChangeDir);
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
    public async Task AddPrerequisite_StartReadinessAndStartGateComeFromIssueGrain()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var grain = IssueGrain(project.Id, dependent.Number);

        await grain.AddPrerequisiteAsync(prereq.Number);
        var info = await GetIssueInfoAsync(project.Id, dependent.Number);
        var readiness = await grain.GetStartReadinessAsync();

        Assert.NotNull(info);
        Assert.Contains(prereq.Number, info.PrerequisiteNumbers);
        Assert.False(readiness.CanStart);
        var waiting = Assert.IsType<IssueStartBlockerDto.WaitingForBlocker>(readiness.Blocker);
        Assert.Equal(prereq.Number, waiting.Issue.Number);
        await Assert.ThrowsAsync<IssueStartBlockedException>(() => grain.StartWorkAsync());
    }

    [Fact]
    public async Task CompletedPrerequisite_AllowsDependentIssueToStart()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Prereq");
        var dependent = await CreateIssueAsync(project.Id, "Dependent");
        var prereqGrain = IssueGrain(project.Id, prereq.Number);
        var dependentGrain = IssueGrain(project.Id, dependent.Number);

        await dependentGrain.AddPrerequisiteAsync(prereq.Number);
        var prereqRunId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(project.Id, "My Project", RepositoryBaseBranch: "main"));
        await prereqGrain.CompleteWorkAsync(prereqRunId);

        var readiness = await dependentGrain.GetStartReadinessAsync();

        Assert.True(readiness.CanStart);
        Assert.Null(readiness.Blocker);
    }

    [Fact]
    public async Task CreateIssue_WithRisk_PersistsAndReturnsIt()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "Risked", risk: "high");

        Assert.Equal("high", issue.Risk);
    }

    [Fact]
    public async Task CreateIssue_WithoutRisk_ReturnsNull()
    {
        var project = await SetupProjectAsync();

        var issue = await CreateIssueAsync(project.Id, "NoRisk");

        Assert.Null(issue.Risk);
    }

    [Fact]
    public async Task ReadModel_IncludesRisk_AfterCreate()
    {
        var project = await SetupProjectAsync();

        var number = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var grain = IssueGrain(project.Id, number);
        await grain.CreateAsync(project.Id, number, "Medium risk", body: null, labels: null, priority: null, repositoryRef: null, risk: "medium");

        using var scope = _services.CreateScope();
        var issuesQuery = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var readModel = await issuesQuery.GetAsync(project.Id, number, project);

        Assert.NotNull(readModel);
        Assert.Equal("medium", readModel!.Risk);
    }

    [Fact]
    public async Task CreateIssue_WithInvalidRisk_Throws()
    {
        var project = await SetupProjectAsync();
        var number = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var grain = IssueGrain(project.Id, number);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.CreateAsync(project.Id, number, "Bad", null, null, null, null, "unknown"));
    }

    [Fact]
    public async Task CreateIssueApi_WithPrerequisiteNumbers_BindsCamelCaseAndReturnsReadModels()
    {
        var project = await SetupProjectAsync();
        var prereq = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "API prereq", isDraft = false });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new { title = "API dependent", isDraft = false, prerequisiteNumbers = new[] { prereq.Number } },
            JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<CreateIssueApiDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        var created = Assert.IsType<CreateIssueApiDto>(envelope.Data);
        Assert.Equal(new[] { prereq.Number }, created.PrerequisiteNumbers);
        var summary = Assert.Single(created.Prereq);
        Assert.Equal(prereq.Number, summary.Number);
        Assert.Equal("API prereq", summary.Title);
        Assert.Equal("backlog", summary.Status);
        Assert.Equal("active", summary.Health);
        Assert.False(summary.Completed);
        Assert.False(created.CanStart);
        Assert.NotNull(created.Blocker);
        Assert.Equal("waiting-for", created.Blocker!.Kind);
        Assert.NotNull(created.Blocker.Issue);
        Assert.Equal(prereq.Number, created.Blocker.Issue!.Number);
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
    public async Task CreateIssue_WithPrerequisiteNumbers_RecordsBothAndExposesReadModels()
    {
        var project = await SetupProjectAsync();
        var prereqA = await CreateIssueAsync(project.Id, "Prereq A");
        var prereqB = await CreateIssueAsync(project.Id, "Prereq B");

        var dependent = await CreateIssueAsync(
            project.Id,
            "Dependent",
            prerequisiteNumbers: [prereqA.Number, prereqB.Number]);

        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, dependent.PrerequisiteNumbers);

        var readModel = await GetIssueReadModelAsync(project.Id, dependent.Number);
        Assert.NotNull(readModel);
        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, readModel!.PrerequisiteNumbers);
        Assert.Equal(2, readModel.Prereq.Length);
        var summaryNumbers = readModel.Prereq.Select(p => p.Number).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { prereqA.Number, prereqB.Number }, summaryNumbers);
        Assert.All(readModel.Prereq, p => Assert.False(p.Completed));
        Assert.False(readModel.CanStart);
        var waiting = Assert.IsType<IssueStartBlockerDto.WaitingForBlocker>(readModel.Blocker);
        Assert.Equal(prereqA.Number, waiting.Issue.Number);
    }

    [Fact]
    public async Task CreateIssue_WithPrerequisiteNumbers_CollapsesDuplicatesIdempotently()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Only one prereq");

        var dependent = await CreateIssueAsync(
            project.Id,
            "Dependent",
            prerequisiteNumbers: [prereq.Number, prereq.Number, prereq.Number]);

        Assert.Equal(new[] { prereq.Number }, dependent.PrerequisiteNumbers);
        var readModel = await GetIssueReadModelAsync(project.Id, dependent.Number);
        Assert.NotNull(readModel);
        Assert.Single(readModel!.Prereq);
    }

    [Fact]
    public async Task CreateIssue_WithoutPrerequisiteNumbers_LeavesEmptySet()
    {
        var project = await SetupProjectAsync();

        var plain = await CreateIssueAsync(project.Id, "Plain");

        Assert.Empty(plain.PrerequisiteNumbers);
        var readModel = await GetIssueReadModelAsync(project.Id, plain.Number);
        Assert.NotNull(readModel);
        Assert.Empty(readModel!.Prereq);
        Assert.True(readModel.CanStart || readModel.Blocker is not null);
    }

    [Fact]
    public async Task CreateIssue_WithEmptyPrerequisiteNumbers_BehavesAsAbsent()
    {
        var project = await SetupProjectAsync();

        var plain = await CreateIssueAsync(project.Id, "Plain empty", prerequisiteNumbers: []);

        Assert.Empty(plain.PrerequisiteNumbers);
    }

    [Fact]
    public async Task CreateIssue_WithNonexistentPrerequisite_ThrowsAndLeavesNoIssue()
    {
        var project = await SetupProjectAsync();

        var attemptNumber = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var grain = IssueGrain(project.Id, attemptNumber);

        await Assert.ThrowsAsync<PrerequisiteValidationException>(() =>
            grain.CreateAsync(
                project.Id,
                attemptNumber,
                "Will fail",
                body: null,
                labels: null,
                priority: null,
                repositoryRef: null,
                risk: null,
                isDraft: false,
                attachmentIds: null,
                workflowProfileId: null,
                prerequisiteNumbers: new[] { 999_999 }));

        var readModel = await GetIssueReadModelAsync(project.Id, attemptNumber);
        Assert.Null(readModel);
    }

    [Fact]
    public async Task CreateIssue_WithCrossProjectPrerequisite_ThrowsAsNotFound()
    {
        var projectA = await SetupProjectAsync();
        var projectB = await SetupProjectAsync();
        var issueInA = await CreateIssueAsync(projectA.Id, "A issue");

        await Assert.ThrowsAsync<PrerequisiteValidationException>(() =>
            CreateIssueAsync(projectB.Id, "B dependent", prerequisiteNumbers: [issueInA.Number]));

        var readModel = await GetIssueReadModelAsync(projectB.Id, 1);
        Assert.Null(readModel);
    }

    [Fact]
    public async Task CreateIssue_WithSelfReferencingPrerequisite_ThrowsAndLeavesNoIssue()
    {
        var project = await SetupProjectAsync();

        await CreateIssueAsync(project.Id, "First");

        // AddPrerequisiteAsync path cannot self-reference (it would have to
        // know its own number in advance). For create, we simulate the
        // would-be self-reference by constructing the request directly via
        // the counter+bypass path: increment the counter to reserve the
        // next number, then attempt CreateAsync on a fresh grain with
        // prerequisiteNumbers pointing at it.
        var reserved = await _grains.GetGrain<IIssueCounterGrain>(project.Id).NextAsync();
        var freshGrain = IssueGrain(project.Id, reserved);

        await Assert.ThrowsAsync<PrerequisiteValidationException>(() =>
            freshGrain.CreateAsync(
                project.Id,
                reserved,
                "Self ref",
                body: null,
                labels: null,
                priority: null,
                repositoryRef: null,
                risk: null,
                isDraft: false,
                attachmentIds: null,
                workflowProfileId: null,
                prerequisiteNumbers: new[] { reserved }));

        var readModel = await GetIssueReadModelAsync(project.Id, reserved);
        Assert.Null(readModel);
    }

    [Fact]
    public async Task CreateIssue_WithCompletedPrerequisite_MarksReadinessOpen()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Will complete");
        var prereqGrain = IssueGrain(project.Id, prereq.Number);

        // Drive the prerequisite to a completed workflow so the
        // read-model exposes it as Completed and the blocker is null.
        var wrId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(
            project.Id,
            project.Name,
            RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));
        await prereqGrain.CompleteWorkAsync(wrId);

        var dependent = await CreateIssueAsync(
            project.Id,
            "Dependent of completed prereq",
            prerequisiteNumbers: [prereq.Number]);

        var readModel = await GetIssueReadModelAsync(project.Id, dependent.Number);
        Assert.NotNull(readModel);
        Assert.True(readModel!.CanStart);
        Assert.Null(readModel.Blocker);
        var prereqSummary = readModel.Prereq.Single();
        Assert.True(prereqSummary.Completed);
    }

    [Fact]
    public async Task CreateIssueApi_AllowsArchivedCompletedPrerequisite()
    {
        var project = await SetupProjectAsync();
        var prereq = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Archived completed prereq", isDraft = false });
        var prereqGrain = IssueGrain(project.Id, prereq.Number);
        var wrId = await prereqGrain.StartWorkAsync(new WorkflowProjectContext(
            project.Id,
            project.Name,
            RepositoryBaseBranch: project.DefaultRepository?.BaseBranch ?? "main"));
        await prereqGrain.CompleteWorkAsync(wrId);
        await prereqGrain.ArchiveAsync();

        var dependent = await _client.PostDataAsync<CreateIssueApiDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Dependent of archived completed prereq", isDraft = false, prerequisiteNumbers = new[] { prereq.Number } });

        var summary = Assert.Single(dependent.Prereq);
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
        Assert.Empty(updated.Prereq);
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

    private Task DispatchEventsAsync() =>
        _grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

}
