using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

/// <summary>
/// Calculation specs for <see cref="IIssueGrain.AddCommentAsync"/>:
/// the grain trims whitespace, persists the row in a single transaction,
/// rejects invalid authors without leaking partial state, and keeps the
/// service principal attribution separate from the user-supplied display
/// alias. Migrated from <c>MohistIntegrationFixture</c> to
/// <c>WorkflowGrainFixture</c> (#290 batch C item 18) — the spec already
/// drove grains directly via <c>_fixture.Grains</c>, so this is a pure
/// fixture swap with no behavioural change. Test logic is unchanged.
/// </summary>
[Collection("WorkflowGrain")]
public class IssueCommentGrainSpecs
{
    private readonly WorkflowGrainFixture _fixture;

    public IssueCommentGrainSpecs(WorkflowGrainFixture fixture) => _fixture = fixture;

    private IGrainFactory Grains => _fixture.Grains;

    [Fact]
    public async Task AddCommentAsync_TrimsPersistsAndReturnsDeclaredAuthor()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync();

        var result = await grain.AddCommentAsync("  Ada Lovelace  ", null, "Looks good");

        Assert.Equal(projectId, result.ProjectId);
        Assert.Equal(issueNumber, result.IssueNumber);
        Assert.Equal("Ada Lovelace", result.Author);
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<MohistDbContext>().IssueComments
            .AsNoTracking()
            .SingleAsync(comment => comment.Id == result.Id);
        Assert.Equal(projectId, row.ProjectId);
        Assert.Equal(issueNumber, row.IssueNumber);
        Assert.Equal("Ada Lovelace", row.Author);
        Assert.Equal("Looks good", row.Body);
        Assert.Equal(row.CreatedAt.ToString("o"), result.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task AddCommentAsync_RejectsInvalidAuthorWithoutCreatingRow(string author)
    {
        var (_, _, grain) = await CreateIssueAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => grain.AddCommentAsync(author, null, "Not persisted"));

        Assert.Contains(author.Length > 100 ? "100" : "required", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<MohistDbContext>().IssueComments
            .AnyAsync(comment => comment.Body == "Not persisted"));
    }

    [Fact]
    public async Task AddCommentAsync_StoresDisplayAliasSeparatelyFromAuthor()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync();

        var result = await grain.AddCommentAsync("service", "  Ada Lovelace  ", "Looks good");

        Assert.Equal("service", result.Author);
        Assert.Equal("Ada Lovelace", result.DisplayName);
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<MohistDbContext>().IssueComments
            .AsNoTracking()
            .SingleAsync(comment => comment.Id == result.Id);
        Assert.Equal("service", row.Author);
        Assert.Equal("Ada Lovelace", row.DisplayName);
    }

    [Fact]
    public async Task AddCommentAsync_OverlongDisplayAlias_RejectedWithoutCreatingRow()
    {
        var (_, _, grain) = await CreateIssueAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => grain.AddCommentAsync("service", new string('x', 101), "Not persisted"));

        Assert.Contains("100", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<MohistDbContext>().IssueComments
            .AnyAsync(comment => comment.Body == "Not persisted"));
    }

    private async Task<(string ProjectId, int IssueNumber, IIssueGrain Grain)> CreateIssueAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        await Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"comment-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "origin",
                GitUrl = "git@example.com:mohist-local.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        var issueNumber = await Grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.CreateAsync(projectId, issueNumber, "Commented issue", null, null, null, isDraft: false);
        return (projectId, issueNumber, grain);
    }
}
