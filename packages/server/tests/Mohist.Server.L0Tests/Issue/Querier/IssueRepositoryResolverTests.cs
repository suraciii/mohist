using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Issue.Querier;

[Trait("level", "L0")]
public class IssueRepositoryResolverTests
{
    private readonly IssueRepositoryResolver _resolver = new();

    [Fact]
    public void Resolve_EmptyReference_OnProjectWithDefault_ReturnsDefaultRepository()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "main", GitUrl = "git@example.com:main.git", BaseBranch = "main", IsDefault = true },
                new RepositoryInfo { Name = "secondary", GitUrl = "git@example.com:secondary.git", BaseBranch = "develop", IsDefault = false },
            ],
        };

        var result = _resolver.Resolve(project, repositoryRef: null);

        Assert.False(result.HasProblem);
        Assert.NotNull(result.Repository);
        Assert.Equal("main", result.Repository!.Name);
        Assert.Equal("git@example.com:main.git", result.Repository.GitUrl);
        Assert.Equal("main", result.Repository.BaseBranch);
        Assert.True(result.Repository.IsDefault);
    }

    [Fact]
    public void Resolve_ExplicitReference_ReturnsMatchingProjectRepository()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "main", GitUrl = "git@example.com:main.git", BaseBranch = "main", IsDefault = true },
                new RepositoryInfo { Name = "secondary", GitUrl = "git@secondary.example:repo.git", BaseBranch = "develop", IsDefault = false },
            ],
        };

        var result = _resolver.Resolve(project, "secondary");

        Assert.False(result.HasProblem);
        Assert.NotNull(result.Repository);
        Assert.Equal("secondary", result.Repository!.Name);
        Assert.Equal("git@secondary.example:repo.git", result.Repository.GitUrl);
        Assert.Equal("develop", result.Repository.BaseBranch);
        Assert.False(result.Repository.IsDefault);
    }

    [Fact]
    public void Resolve_ExplicitReference_StaleStoredFieldsDoNotOverrideProjectRepository()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "main", GitUrl = "git@current.example:repo.git", BaseBranch = "current-branch", IsDefault = true },
            ],
        };

        var result = _resolver.Resolve(project, "main");

        Assert.False(result.HasProblem);
        Assert.NotNull(result.Repository);
        Assert.Equal("git@current.example:repo.git", result.Repository!.GitUrl);
        Assert.Equal("current-branch", result.Repository.BaseBranch);
        Assert.True(result.Repository.IsDefault);
    }

    [Fact]
    public void Resolve_ProjectMissing_ReturnsProjectMissingProblem()
    {
        var result = _resolver.Resolve(project: null, repositoryRef: "main");

        Assert.True(result.HasProblem);
        Assert.Null(result.Repository);
        Assert.Equal(IssueRepositoryProblemCode.ProjectMissing, result.Problem!.Code);
        Assert.Null(result.Problem.RepositoryRef);
    }

    [Fact]
    public void Resolve_ProjectWithNoRepositories_ReturnsProjectHasNoRepositoriesProblem()
    {
        var project = new ProjectInfo
        {
            Id = "proj-empty",
            Name = "Empty",

            Repositories = [],
        };

        var result = _resolver.Resolve(project, "main");

        Assert.True(result.HasProblem);
        Assert.Null(result.Repository);
        Assert.Equal(IssueRepositoryProblemCode.ProjectHasNoRepositories, result.Problem!.Code);
        Assert.Contains("proj-empty", result.Problem.Message);
    }

    [Fact]
    public void Resolve_EmptyReferenceAndNoDefault_ReturnsDefaultRepositoryMissingProblem()
    {
        var project = new ProjectInfo
        {
            Id = "proj-nodefault",
            Name = "NoDefault",

            Repositories =
            [
                new RepositoryInfo { Name = "secondary", GitUrl = "git@example.com:secondary.git", BaseBranch = "develop", IsDefault = false },
            ],
        };

        var result = _resolver.Resolve(project, repositoryRef: null);

        Assert.True(result.HasProblem);
        Assert.Null(result.Repository);
        Assert.Equal(IssueRepositoryProblemCode.DefaultRepositoryMissing, result.Problem!.Code);
    }

    [Fact]
    public void Resolve_UnknownExplicitReference_ReturnsRepositoryNotFoundProblem()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "main", GitUrl = "git@example.com:main.git", BaseBranch = "main", IsDefault = true },
                new RepositoryInfo { Name = "secondary", GitUrl = "git@example.com:secondary.git", BaseBranch = "develop", IsDefault = false },
            ],
        };

        var result = _resolver.Resolve(project, "ghost");

        Assert.True(result.HasProblem);
        Assert.Null(result.Repository);
        Assert.Equal(IssueRepositoryProblemCode.RepositoryNotFound, result.Problem!.Code);
        Assert.Equal("ghost", result.Problem.RepositoryRef);
        Assert.NotNull(result.Problem.CandidateNames);
        Assert.Contains("main", result.Problem.CandidateNames!);
        Assert.Contains("secondary", result.Problem.CandidateNames!);
    }

    [Fact]
    public void Resolve_UnknownReference_NeverFallsBackToDefaultOrImplicitMainBranch()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "main", GitUrl = "git@example.com:main.git", BaseBranch = "main", IsDefault = true },
            ],
        };

        var result = _resolver.Resolve(project, "ghost");

        Assert.Null(result.Repository);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void Resolve_AmbiguousExplicitReference_ReturnsAmbiguousReferenceProblem()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "main", GitUrl = "git@example.com:main.git", BaseBranch = "main", IsDefault = true },
                new RepositoryInfo { Name = "MAIN", GitUrl = "git@example.com:MAIN.git", BaseBranch = "main", IsDefault = false },
            ],
        };

        var result = _resolver.Resolve(project, "main");

        Assert.True(result.HasProblem);
        Assert.Null(result.Repository);
        Assert.Equal(IssueRepositoryProblemCode.AmbiguousReference, result.Problem!.Code);
        Assert.Equal("main", result.Problem.RepositoryRef);
        Assert.NotNull(result.Problem.CandidateNames);
        Assert.Contains("main", result.Problem.CandidateNames!);
        Assert.Contains("MAIN", result.Problem.CandidateNames!);
    }

    [Fact]
    public void Resolve_ReferenceLookupIsCaseInsensitive()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "Main", GitUrl = "git@example.com:Main.git", BaseBranch = "main", IsDefault = true },
            ],
        };

        var result = _resolver.Resolve(project, "main");

        Assert.False(result.HasProblem);
        Assert.NotNull(result.Repository);
        Assert.Equal("Main", result.Repository!.Name);
    }

    [Fact]
    public void Resolve_WhitespaceOnlyReference_BehavesLikeEmpty()
    {
        var project = new ProjectInfo
        {
            Id = "proj-1",
            Name = "Project",

            Repositories =
            [
                new RepositoryInfo { Name = "main", GitUrl = "git@example.com:main.git", BaseBranch = "main", IsDefault = true },
            ],
        };

        var result = _resolver.Resolve(project, "   ");

        Assert.False(result.HasProblem);
        Assert.NotNull(result.Repository);
        Assert.Equal("main", result.Repository!.Name);
    }
}
