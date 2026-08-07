using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Route contract for the <c>Waiting</c> array on
/// <c>GET /api/agent/activity</c>: the empty-array shape on the wire
/// (route envelope, no in-progress issue paused on an approval gate).
/// The calculation matrix (approval-gate detection, empty-set, only
/// in-progress) is owned by <c>ActivityEvidenceAssemblerSpecs</c> and
/// exercised without an HTTP round-trip; see
/// <see cref="Mohist.Server.SpecTests.Specs.AgentOps.ActivityEvidenceAssemblerSpecs"/>.
/// </summary>
[Collection("IntegrationApi")]
public class ActivityWaitingApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public ActivityWaitingApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetActivity_WhenNoIssuePausedOnApprovalGate_HasEmptyWaitingArray()
    {
        var project = await CreateProjectAsync();
        await InsertBacklogIssueAsync(project.Id, number: 1, title: "Backlog only");

        var response = await _client.GetDataAsync<ActivityResponseDto>(
            $"/api/projects/{project.Id}/agent/activity");

        Assert.Empty(response.Waiting);
        Assert.Equal(0, response.Summary.Waiting);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"waiting-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", name);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return project;
    }

    private async Task InsertBacklogIssueAsync(string projectId, int number, string title)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.Backlog,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name);
    private sealed record ActivityWaitingEntryDto(int IssueNumber, string IssueTitle, string? Stage, string Label, string? RequestedAt, string? Preview);
    private sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed);
    private sealed record ActivityResponseDto(ActivitySummaryDto Summary, object[] Sessions, ActivityWaitingEntryDto[] Waiting);
}
