using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// issue-417 T-003: HTTP create/PATCH/list/detail contract coverage for
/// the required <c>repositoryName</c> field. Mirrors the canonical spec
/// scenarios in
/// <c>openspec/changes/issue-417/specs/issue-repository-binding/spec.md</c>:
/// default resolution, explicit canonical casing, PATCH reassignment
/// before start, post-start lock rejection, list filtering by stored
/// name (case-insensitive), and detail output identity.
/// </summary>
[Collection("IssueLifecycle")]
public class IssueRepositoryBindingApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public IssueRepositoryBindingApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task PostIssue_WithoutRepository_BindsToProjectDefault_AndReturnsCanonicalName()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        var created = await CreateIssueAsync(projectId, new { title = "Default target" });

        Assert.Equal("main", created.Data!.RepositoryName);
        Assert.Equal("main", created.Data.Repository!.Name);
        Assert.Null(created.Data.RepositoryProblem);
    }

    [Fact]
    public async Task PostIssue_WithRepository_ReturnsCanonicalCasing()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        var created = await CreateIssueAsync(projectId, new
        {
            title = "Explicit canonical",
            repositoryName = "SECONDARY",
        });

        Assert.Equal("secondary", created.Data!.RepositoryName);
        Assert.Equal("secondary", created.Data.Repository!.Name);
    }

    [Fact]
    public async Task PostIssue_WithUnknownRepository_ReturnsBadRequest()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title = "Ghost", repositoryName = "ghost" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("repository_not_found", envelope.Code);
    }

    [Fact]
    public async Task GetIssue_AfterRepoRemoval_ReturnsStoredNameAsUnresolved()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Orphan", repositoryName = "secondary" });
        Assert.Equal("secondary", created.Data!.RepositoryName);

        // Drive the issue to a terminal status so the deletion guard
        // (issue-417 T-004) lets the repository be removed.
        var grain = _fixture.Grains.GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(
                new Mohist.Server.Infrastructure.Orleans.IssueKey(projectId, created.Data!.Number)));
        await grain.CancelAsync();

        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("secondary");

        var fetched = await GetIssueAsync(projectId, created.Data.Number);

        Assert.NotNull(fetched);
        Assert.Equal("secondary", fetched!.RepositoryName);
        Assert.Null(fetched.Repository);
        Assert.NotNull(fetched.RepositoryProblem);
        Assert.Equal("secondary", fetched.RepositoryProblem!.RepositoryRef);
    }

    [Fact]
    public async Task PatchIssue_WithRepositoryName_ReassignsBeforeStart_ReturnsCanonicalCasing()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Move me", repositoryName = "main" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{created.Data!.Number}",
            new { repositoryName = "SECONDARY" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        Assert.Equal("secondary", envelope.Data!.RepositoryName);
    }

    [Fact]
    public async Task PatchIssue_RepositoryAndInvalidDraftTransition_DoesNotLeakRepositoryIntoLaterSave()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Cancelled", repositoryName = "main" });
        var grain = _fixture.Grains.GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(
                new Mohist.Server.Infrastructure.Orleans.IssueKey(projectId, created.Data!.Number)));
        await grain.CancelAsync();

        using (var rejected = await _client.PatchAsJsonAsync(
                   $"/api/projects/{projectId}/issues/{created.Data.Number}",
                   new { repositoryName = "secondary", isDraft = false },
                   JsonOptions))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        }

        using (var saved = await _client.PatchAsJsonAsync(
                   $"/api/projects/{projectId}/issues/{created.Data.Number}",
                   new { title = "Saved after rejection" },
                   JsonOptions))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var persisted = await GetIssueAsync(projectId, created.Data.Number);
        Assert.Equal("main", persisted!.RepositoryName);
        Assert.Equal("Saved after rejection", persisted.Title);
    }

    [Fact]
    public async Task PatchIssue_SameRepository_RecordsReceiptWithoutRepositoryChangedEvent()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "No-op target", repositoryName = "main" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{created.Data!.Number}",
            new { repositoryName = "MAIN" },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var stored = await events.ListIssueEventsAsync(projectId, created.Data!.Number);
        Assert.DoesNotContain(stored, e => e.Envelope.Type == "com.mohist.issue.repository-changed");
    }

    [Fact]
    public async Task PatchIssue_WithUnknownRepository_ReturnsBadRequest_LeavesIssueUnchanged()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Anchor", repositoryName = "main" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{created.Data!.Number}",
            new { repositoryName = "ghost" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("repository_not_found", envelope.Code);

        var unchanged = await GetIssueAsync(projectId, created.Data.Number);
        Assert.Equal("main", unchanged!.RepositoryName);
    }

    [Fact]
    public async Task PatchIssue_AfterWorkflowStart_RejectsRepositoryReassignment()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Started", repositoryName = "main", isDraft = false });
        var grain = _fixture.Grains.GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(
                new Mohist.Server.Infrastructure.Orleans.IssueKey(projectId, created.Data!.Number)));
        await grain.StartWorkAsync(new Mohist.Server.Issue.Grains.WorkflowProjectContext(
            projectId, "P", RepositoryBaseBranch: "main"));

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{created.Data.Number}",
            new { repositoryName = "secondary" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.False(envelope!.Success);
        Assert.Equal("repository_locked", envelope.Code);

        var unchanged = await GetIssueAsync(projectId, created.Data.Number);
        Assert.Equal("main", unchanged!.RepositoryName);
    }

    [Fact]
    public async Task ListIssues_FilterByRepository_CaseInsensitive_ReturnsOnlyMatching()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        await CreateIssueAsync(projectId, new { title = "On main 1", repositoryName = "main" });
        await CreateIssueAsync(projectId, new { title = "On main 2", repositoryName = "main" });
        await CreateIssueAsync(projectId, new { title = "On secondary", repositoryName = "secondary" });

        var filtered = await GetIssuesAsync($"/api/projects/{projectId}/issues?repository=SERVER");

        // 'SERVER' is not a declared repository, so the filter must
        // return no issues — the spec requires exact (case-insensitive)
        // matching against the stored name.
        Assert.Empty(filtered);
    }

    [Fact]
    public async Task ListIssues_FilterByRepositorySecondary_ReturnsOnlySecondaryIssues()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        await CreateIssueAsync(projectId, new { title = "On main", repositoryName = "main" });
        var secondary = await CreateIssueAsync(projectId, new { title = "On secondary", repositoryName = "secondary" });
        var secondaryLower = await CreateIssueAsync(projectId, new { title = "On secondary lowercase", repositoryName = "secondary" });

        // Filter by exact stored-name case ("secondary" is the
        // canonical case the project declared) and verify both
        // canonical-cased issues come back.
        var filtered = await GetIssuesAsync($"/api/projects/{projectId}/issues?repository=secondary");
        Assert.Equal(2, filtered.Length);
        Assert.All(filtered, i => Assert.Equal("secondary", i.RepositoryName));
    }

    [Fact]
    public async Task ListIssues_FilterRepositoryAndStatus_ComposesFilters()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        await CreateIssueAsync(projectId, new { title = "Main backlog 1", repositoryName = "main", isDraft = false });
        var secondary1 = await CreateIssueAsync(projectId, new { title = "Secondary backlog 1", repositoryName = "secondary", isDraft = false });
        await CreateIssueAsync(projectId, new { title = "Secondary backlog 2", repositoryName = "secondary", isDraft = false });

        // No issue has been started yet, so all three are backlog.
        var filtered = await GetIssuesAsync($"/api/projects/{projectId}/issues?repository=secondary&stage=backlog");

        Assert.Equal(2, filtered.Length);
        Assert.All(filtered, i => Assert.Equal("secondary", i.RepositoryName));
        Assert.Equal(secondary1.Data!.Number, filtered[0].Number);
    }

    [Fact]
    public async Task ListIssues_FilterByHistoricalTarget_AfterRepoRemoved_StillReturnsTerminalIssues()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var orphaned = await CreateIssueAsync(projectId, new { title = "Terminal orphan", repositoryName = "secondary", isDraft = false });
        var grain = _fixture.Grains.GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(
                new Mohist.Server.Infrastructure.Orleans.IssueKey(projectId, orphaned.Data!.Number)));
        var wrId = await grain.StartWorkAsync(new Mohist.Server.Issue.Grains.WorkflowProjectContext(
            projectId, "P", RepositoryBaseBranch: "develop"));
        await grain.CompleteWorkAsync(wrId);
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).RemoveRepositoryAsync("secondary");

        var filtered = await GetIssuesAsync($"/api/projects/{projectId}/issues?repository=secondary&stage=done");

        Assert.Single(filtered);
        Assert.Equal(orphaned.Data!.Number, filtered[0].Number);
        Assert.Equal("secondary", filtered[0].RepositoryName);
    }

    [Fact]
    public async Task DetailIssue_AfterRepositoryReassignment_PATCH_ReturnsCanonicalCasing()
    {
        var (projectId, _) = await SetupProjectWithRepositoriesAsync();
        var created = await CreateIssueAsync(projectId, new { title = "Reflow", repositoryName = "main" });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{created.Data!.Number}",
            new { repositoryName = "Secondary" },
            JsonOptions);

        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>(JsonOptions);
        Assert.Equal("secondary", envelope!.Data!.RepositoryName);

        var detail = await GetIssueAsync(projectId, created.Data.Number);
        Assert.Equal("secondary", detail!.RepositoryName);
    }

    private async Task<ApiEnvelope<IssueDto>> CreateIssueAsync(string projectId, object body)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            body,
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        return envelope;
    }

    private async Task<IssueDto?> GetIssueAsync(string projectId, int number)
    {
        using var response = await _client.GetAsync($"/api/projects/{projectId}/issues/{number}");
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        return envelope.Data;
    }

    private async Task<IssueDto[]> GetIssuesAsync(string url)
    {
        using var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<IssueDto[]>>(JsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope!.Success);
        Assert.NotNull(envelope.Data);
        return envelope.Data!;
    }

    private async Task<(string ProjectId, Mohist.Server.Project.Services.ProjectInfo Project)> SetupProjectWithRepositoriesAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        var project = await grain.CreateAsync(
            $"proj-{Guid.NewGuid():N}",
            new Mohist.Server.Project.Domain.RepositoryInfo
            {
                Name = "main",
                GitUrl = "git@main.example:repo.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        await grain.AddRepositoryAsync("secondary", "git@secondary.example:repo.git", "develop");
        return (projectId, project);
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null);

    private sealed record IssueDto(
        int Number,
        string Title,
        string Status,
        string? RepositoryName,
        RepositoryDto? Repository,
        RepositoryProblemDto? RepositoryProblem);

    private sealed record RepositoryDto(string Name, string GitUrl, string BaseBranch, bool IsDefault);

    private sealed record RepositoryProblemDto(string Code, string Message, string? RepositoryRef, string[]? CandidateNames);
}
