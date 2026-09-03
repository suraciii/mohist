using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Project.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Issue.Api;

/// <summary>
/// Application-level issue creation claims (#676): HTTP binding and error
/// envelopes for the create/prerequisite endpoints, and behaviors that span
/// the issue grain and the workflow/runner runtime (start dispatch, cancel,
/// completion-gated readiness). The grain-internal create/update matrix —
/// numbering, identity, prerequisite validation, risk, hydration — is owned
/// by Mohist.Server.Tests IssueGrainCreationSpecs.
/// </summary>
[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
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
        }, "git diff --check");
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
    public async Task CreateIssueApi_AllowsArchivedCompletedPrerequisite()
    {
        var project = await SetupProjectAsync();
        var prereq = await CreateIssueAsync(project.Id, "Archived completed prereq", isDraft: false);
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
    private async Task<WorkDispatch> PollAnyWorkAsync(IRunnerGrain runner)
    {
        var work = await runner.PollAsync(_services);
        Assert.NotNull(work);
        return work;
    }

    private Task DispatchEventsAsync() =>
        _services.GetRequiredService<IEventDispatcher>().DrainAsync();

}
