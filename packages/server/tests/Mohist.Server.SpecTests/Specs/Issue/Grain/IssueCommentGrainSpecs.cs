using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Grain;

[Collection("IssueLifecycle")]
public class IssueCommentGrainSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueCommentGrainSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AddCommentAsync_TrimsPersistsAndReturnsDeclaredAuthor()
    {
        var (projectId, issueNumber, grain) = await CreateIssueAsync();

        var result = await grain.AddCommentAsync("  Ada Lovelace  ", "Looks good");

        Assert.Equal("Ada Lovelace", result.Author);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<MohistDbContext>().IssueComments
            .AsNoTracking()
            .SingleAsync(comment => comment.Id == result.Id);
        Assert.Equal(projectId, row.ProjectId);
        Assert.Equal(issueNumber, row.IssueNumber);
        Assert.Equal("Ada Lovelace", row.Author);
        Assert.Equal("Looks good", row.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task AddCommentAsync_RejectsInvalidAuthorWithoutCreatingRow(string author)
    {
        var (_, _, grain) = await CreateIssueAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => grain.AddCommentAsync(author, "Not persisted"));

        Assert.Contains(author.Length > 100 ? "100" : "required", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var scope = _fixture.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<MohistDbContext>().IssueComments
            .AnyAsync(comment => comment.Body == "Not persisted"));
    }

    private async Task<(string ProjectId, int IssueNumber, IIssueGrain Grain)> CreateIssueAsync()
    {
        var projectId = $"proj_{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"comment-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "origin",
                GitUrl = "git@example.com:mohist-local.git",
                BaseBranch = "main",
                IsDefault = true,
            });
        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(projectId).NextAsync();
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.CreateAsync(projectId, issueNumber, "Commented issue", null, null, null, isDraft: false);
        return (projectId, issueNumber, grain);
    }
}
