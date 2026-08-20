using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

/// <summary>
/// Calculation specs for the repository resolver projection exercised
/// by <c>POST /api/projects/{ref}/issues</c> (default / explicit name)
/// and <c>GET /api/projects/{ref}/issues/{n}</c> (re-resolve after
/// project changes). The route contract (400 unknown repo, 400 create
/// failed, 404) stays in <c>IssueRepositoryApiSpecs</c> and
/// <c>IssueRepositoryBindingApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueRepositoryResolverSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueRepositoryResolverSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ProjectInfo_GetRepository_DefaultRepository()
    {
        var project = new ProjectInfo
        {
            Id = "p1",
            Name = "P",
            Repositories = new List<RepositoryInfo>
            {
                new() { Name = "main", GitUrl = "git@main", BaseBranch = "main", IsDefault = true },
                new() { Name = "secondary", GitUrl = "git@secondary", BaseBranch = "develop" },
            }
        };

        var repo = project.DefaultRepository;

        Assert.NotNull(repo);
        Assert.Equal("main", repo!.Name);
        Assert.True(repo.IsDefault);
    }

    [Fact]
    public void ProjectInfo_GetRepository_ByName_CaseInsensitive()
    {
        var project = new ProjectInfo
        {
            Id = "p1",
            Name = "P",
            Repositories = new List<RepositoryInfo>
            {
                new() { Name = "main", GitUrl = "git@main", BaseBranch = "main", IsDefault = true },
                new() { Name = "secondary", GitUrl = "git@secondary", BaseBranch = "develop" },
            }
        };

        var lower = project.GetRepository("secondary");
        var upper = project.GetRepository("SECONDARY");
        var mixed = project.GetRepository("Secondary");

        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.NotNull(mixed);
        Assert.Equal("secondary", lower!.Name);
        Assert.Equal("secondary", upper!.Name);
        Assert.Equal("secondary", mixed!.Name);
    }

    [Fact]
    public void ProjectInfo_GetRepository_UnknownReturnsNull()
    {
        var project = new ProjectInfo
        {
            Id = "p1",
            Name = "P",
            Repositories = new List<RepositoryInfo>
            {
                new() { Name = "main", GitUrl = "git@main", BaseBranch = "main", IsDefault = true },
            }
        };

        var repo = project.GetRepository("ghost");

        Assert.Null(repo);
    }

    [Fact]
    public void ProjectInfo_GetRepository_NullOrWhitespaceReturnsDefault()
    {
        var project = new ProjectInfo
        {
            Id = "p1",
            Name = "P",
            Repositories = new List<RepositoryInfo>
            {
                new() { Name = "main", GitUrl = "git@main", BaseBranch = "main", IsDefault = true },
                new() { Name = "secondary", GitUrl = "git@secondary", BaseBranch = "develop" },
            }
        };

        var defaultRepo = project.GetRepository(null);
        var whitespaceRepo = project.GetRepository("   ");

        Assert.NotNull(defaultRepo);
        Assert.NotNull(whitespaceRepo);
        Assert.Equal("main", defaultRepo!.Name);
        Assert.Equal("main", whitespaceRepo!.Name);
    }

    [Fact]
    public void RepositoryInfo_ResolvedBaseBranch_FallsBackToMainWhenEmpty()
    {
        var repo = new RepositoryInfo { Name = "x", GitUrl = "git@x" };

        Assert.Equal("main", repo.ResolvedBaseBranch);
    }

    [Fact]
    public void RepositoryInfo_ResolvedBaseBranch_PreservesValueWhenPresent()
    {
        var repo = new RepositoryInfo { Name = "x", GitUrl = "git@x", BaseBranch = "develop" };

        Assert.Equal("develop", repo.ResolvedBaseBranch);
    }
}