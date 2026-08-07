using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

/// <summary>
/// Calculation specs for the label-filter + label-catalog read paths
/// exercised by <c>GET /api/projects/{ref}/issues?label=...</c> and
/// <c>GET /api/projects/{ref}/labels</c>. The querier is resolved via
/// <see cref="MohistDbFixture.Services"/> (no web host, no HTTP round-trip).
/// Route contract (400 invalid key/value, 400 malformed filter, 200 list)
/// stays in <c>IssueLabelsApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueLabelFilterQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueLabelFilterQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private DateTime Now => _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
    private DateTimeOffset NowOffset => _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow();

    private IssueQuerier ResolveQuerier() =>
        _fixture.Services.GetRequiredService<IssueQuerier>();

    [Fact]
    public async Task ListWithLabelFilters_SingleFilter_ReturnsOnlyMatching()
    {
        var projectId = await CreateProjectAsync();
        var frontend = await SeedIssueWithLabelsAsync(projectId, "Frontend", new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });
        await SeedIssueWithLabelsAsync(projectId, "Backend", new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "backend" });

        var items = await ResolveQuerier().ListWithLabelFiltersAsync(
            projectId, project: null, stage: null, labels: ["stream=frontend"],
            priority: null, archived: null, all: true);

        var item = Assert.Single(items);
        Assert.Equal(frontend.Number, item.Number);
    }

    [Fact]
    public async Task ListWithLabelFilters_MultipleFilters_ReturnsOnlyIssuesMatchingAll()
    {
        var projectId = await CreateProjectAsync();
        var match = await SeedIssueWithLabelsAsync(projectId, "Frontend auth",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            });
        await SeedIssueWithLabelsAsync(projectId, "Frontend docs",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "docs",
            });

        var items = await ResolveQuerier().ListWithLabelFiltersAsync(
            projectId, project: null, stage: null,
            labels: ["stream=frontend", "module=auth"],
            priority: null, archived: null, all: true);

        var item = Assert.Single(items);
        Assert.Equal(match.Number, item.Number);
    }

    [Fact]
    public async Task ListAsync_WithLabelFilter_ReturnsOnlyMatching()
    {
        var projectId = await CreateProjectAsync();
        var match = await SeedIssueWithLabelsAsync(projectId, "Frontend",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" });
        await SeedIssueWithLabelsAsync(projectId, "Backend",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "backend" });

        var items = await ResolveQuerier().ListAsync(projectId, label: "stream=frontend", all: true);

        var item = Assert.Single(items);
        Assert.Equal(match.Number, item.Number);
    }

    [Fact]
    public async Task ListAsync_WithCommaSeparatedLabelFilters_ReturnsOnlyMatchingAll()
    {
        var projectId = await CreateProjectAsync();
        var match = await SeedIssueWithLabelsAsync(projectId, "Frontend auth",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            });

        var items = await ResolveQuerier().ListAsync(projectId, label: "stream=frontend,module=auth", all: true);

        var item = Assert.Single(items);
        Assert.Equal(match.Number, item.Number);
    }

    [Fact]
    public async Task ListAsync_FullReplaceLabels_ReplacesMap()
    {
        var projectId = await CreateProjectAsync();
        var original = await SeedIssueWithLabelsAsync(projectId, "Replace labels",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["old"] = "stale",
            });

        // Replace the issue's labels in-place; this is a round-trip assertion
        // through the store+querier, not a grain path.
        await using (var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            var row = await db.Issues
                .Where(r => r.ProjectId == projectId && r.Number == original.Number)
                .FirstAsync();
            var fresh = new DomainIssue
            {
                ProjectId = original.ProjectId,
                Number = original.Number,
                Title = original.Title,
                Status = original.Status,
                Priority = original.Priority,
                CreatedAt = original.CreatedAt,
                UpdatedAt = Now,
                ArchivedAt = original.ArchivedAt,
                CompletedAt = original.CompletedAt,
                PrerequisiteNumbers = original.PrerequisiteNumbers,
                IsDraft = original.IsDraft,
                RepositoryRef = original.RepositoryRef,
                Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["module"] = "auth",
                },
            };
            row.State = IssueStore.Serialize(fresh);
            await db.SaveChangesAsync();
        }

        var reread = await ResolveQuerier().ListAsync(projectId, all: true);
        var item = reread.Single();
        Assert.False(item.Labels.ContainsKey("stream"));
        Assert.False(item.Labels.ContainsKey("old"));
        Assert.Equal("auth", item.Labels["module"]);
    }

    [Fact]
    public async Task ListAsync_WithProjectScopedIssues_DistinctSortedLabelKeys()
    {
        var projectId = await CreateProjectAsync();
        await SeedIssueWithLabelsAsync(projectId, "A",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            });
        await SeedIssueWithLabelsAsync(projectId, "B",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "backend",
                ["priority"] = "p1",
            });
        await SeedIssueWithLabelsAsync(projectId, "C",
            new Dictionary<string, string>(StringComparer.Ordinal));

        var issues = await ResolveQuerier().ListAsync(projectId, all: true);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            foreach (var k in issue.Labels.Keys)
                keys.Add(k);
        }

        Assert.Equal(new[] { "module", "priority", "stream" }, keys.ToArray());
    }

    [Fact]
    public async Task CreateIssue_WithKeyValueLabels_PersistsAndReturnsMap()
    {
        var projectId = await CreateProjectAsync();
        var seeded = await SeedIssueWithLabelsAsync(projectId, "Key value issue",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            });

        Assert.Equal("frontend", seeded.Labels["stream"]);
        Assert.Equal("auth", seeded.Labels["module"]);

        var items = await ResolveQuerier().ListAsync(projectId, all: true);
        var fetched = items.Single();
        Assert.Equal("frontend", fetched.Labels["stream"]);
        Assert.Equal("auth", fetched.Labels["module"]);
    }

    [Fact]
    public void LabelFilterTokens_ParsesCommaSeparatedTokens()
    {
        var tokens = IssueQuerier.LabelFilterTokens("stream=frontend,module=auth");

        Assert.Equal(new[] { "stream=frontend", "module=auth" }, tokens);
    }

    [Fact]
    public void ParseLabelFilter_HandlesTokensWithoutValue()
    {
        var (key, value) = IssueQuerier.ParseLabelFilter("stream");

        Assert.Null(key);
        Assert.Equal("stream", value);
    }

    private async Task<string> CreateProjectAsync()
    {
        var projectId = $"proj-lbl-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = $"label-filter-{Guid.NewGuid():N}",
            CreatedAt = NowOffset,
            UpdatedAt = NowOffset,
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task<DomainIssue> SeedIssueWithLabelsAsync(
        string projectId,
        string title,
        Dictionary<string, string> labels)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var existing = await db.Issues
            .Where(r => r.ProjectId == projectId)
            .Select(r => (int?)r.Number)
            .MaxAsync();
        var number = (existing ?? 0) + 1;
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.Backlog,
            Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
        return issue;
    }
}