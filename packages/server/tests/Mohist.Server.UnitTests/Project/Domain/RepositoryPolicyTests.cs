using Mohist.Server.Project.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Project.Domain;

public class RepositoryPolicyTests
{
    [Fact]
    public void Validate_EmptyList_ReportsRepositoryListEmpty()
    {
        var errors = RepositoryPolicy.Validate([]);
        Assert.Contains(errors, e => e.Code == "repository_list_empty");
    }

    [Fact]
    public void Validate_MissingDefault_ReportsRepositoryDefaultMissing()
    {
        var errors = RepositoryPolicy.Validate(
            [
                new RepositoryPolicy.NormalizedRepository(
                    Name: "server",
                    GitUrl: "git@example.com:server.git",
                    BaseBranch: "main",
                    IsDefault: false),
            ]);

        Assert.Contains(errors, e => e.Code == "repository_default_missing");
    }

    [Fact]
    public void Validate_MultipleDefaults_ReportsRepositoryDefaultMultiple()
    {
        var errors = RepositoryPolicy.Validate(
            [
                new("server", "git@example.com:server.git", "main", true),
                new("web", "git@example.com:web.git", "main", true),
            ]);

        Assert.Contains(errors, e => e.Code == "repository_default_multiple");
    }

    [Fact]
    public void Validate_DuplicateCaseInsensitive_ReportsDuplicateName()
    {
        var errors = RepositoryPolicy.Validate(
            [
                new("server", "git@example.com:server.git", "main", true),
                new("SERVER", "git@example.com:server2.git", "main", false),
            ]);

        Assert.Contains(errors, e => e.Code.Contains("name", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_BlankGitUrl_ReportsError()
    {
        var errors = RepositoryPolicy.Validate(
            [
                new("server", "", "main", true),
            ]);

        Assert.Contains(errors, e => e.Code.Contains("gitUrl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_CompleteDeclarationWithSingleDefault_HasNoErrors()
    {
        var errors = RepositoryPolicy.Validate(
            [
                new("server", "git@example.com:server.git", "main", true),
            ]);

        Assert.Empty(errors);
    }

    [Fact]
    public void Normalize_NoDefaults_PicksFirstAsDefault()
    {
        var normalized = RepositoryPolicy.Normalize(
            [
                new("a", "git@a.git", "main", false),
                new("b", "git@b.git", "main", false),
            ]);

        Assert.True(normalized[0].IsDefault);
        Assert.False(normalized[1].IsDefault);
        Assert.Equal("a", normalized[0].Name);
    }

    [Fact]
    public void Normalize_MultipleDefaults_KeepsFirstMarkedAndClearsOthers()
    {
        var normalized = RepositoryPolicy.Normalize(
            [
                new("a", "git@a.git", "main", true),
                new("b", "git@b.git", "main", true),
                new("c", "git@c.git", "main", true),
            ]);

        Assert.True(normalized[0].IsDefault);
        Assert.False(normalized[1].IsDefault);
        Assert.False(normalized[2].IsDefault);
    }

    [Fact]
    public void Normalize_SingleDefault_PreservesDefault()
    {
        var normalized = RepositoryPolicy.Normalize(
            [
                new("a", "git@a.git", "main", false),
                new("b", "git@b.git", "main", true),
                new("c", "git@c.git", "main", false),
            ]);

        Assert.False(normalized[0].IsDefault);
        Assert.True(normalized[1].IsDefault);
        Assert.False(normalized[2].IsDefault);
    }

    [Fact]
    public void Normalize_BlankBaseBranch_BecomesMain()
    {
        var normalized = RepositoryPolicy.Normalize(
            [
                new("a", "git@a.git", "", true),
            ]);

        Assert.Equal("main", normalized[0].BaseBranch);
    }

    [Fact]
    public void Normalize_PreservesOrderAndNames()
    {
        var normalized = RepositoryPolicy.Normalize(
            [
                new("third", "git@third.git", "develop", false),
                new("first", "git@first.git", "main", true),
                new("second", "git@second.git", "main", false),
            ]);

        Assert.Equal("third", normalized[0].Name);
        Assert.Equal("first", normalized[1].Name);
        Assert.Equal("second", normalized[2].Name);
    }

    [Fact]
    public void Normalize_EmptyList_ReturnsEmpty()
    {
        var normalized = RepositoryPolicy.Normalize([]);
        Assert.Empty(normalized);
    }

    [Fact]
    public void CreateInitial_SetsIsDefaultTrue()
    {
        var initial = RepositoryPolicy.CreateInitial("main", "git@example.com:main.git", null);
        Assert.True(initial.IsDefault);
        Assert.Equal("main", initial.BaseBranch);
    }

    [Fact]
    public void BuildAdd_NewName_FirstRepoBecomesDefault()
    {
        var current = RepositoryPolicy.Normalize([]);
        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(
                Name: "main",
                GitUrl: "git@example.com:main.git",
                BaseBranch: "main"),
            current);

        Assert.True(build.IsSuccess);
        Assert.True(build.Value.IsDefault);
    }

    [Fact]
    public void BuildAdd_DuplicateCaseInsensitive_RejectedWithoutMutation()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(
                Name: "SERVER",
                GitUrl: "git@example.com:server2.git",
                BaseBranch: "main"),
            current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildAdd_WithoutSetDefault_PreservesExistingDefault()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(
                Name: "web",
                GitUrl: "git@example.com:web.git",
                BaseBranch: "main"),
            current);

        Assert.True(build.IsSuccess);
        Assert.False(build.Value.IsDefault);
    }

    [Fact]
    public void BuildAdd_WithSetDefault_BecomesDefault()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(
                Name: "web",
                GitUrl: "git@example.com:web.git",
                BaseBranch: "main",
                SetDefault: true),
            current);

        Assert.True(build.IsSuccess);
        Assert.True(build.Value.IsDefault);
    }

    [Fact]
    public void BuildAdd_BlankGitUrl_RejectedWithValidationError()
    {
        var current = RepositoryPolicy.Normalize([]);
        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(
                Name: "server",
                GitUrl: "",
                BaseBranch: "main"),
            current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Code.Contains("gitUrl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildAdd_BlankName_RejectedWithValidationError()
    {
        var current = RepositoryPolicy.Normalize([]);
        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(
                Name: "",
                GitUrl: "git@example.com:server.git",
                BaseBranch: "main"),
            current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Code.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildAdd_NullBaseBranch_DefaultsToMain()
    {
        var current = RepositoryPolicy.Normalize([]);
        var build = RepositoryPolicy.BuildAdd(
            new RepositoryPolicy.TransitionInput(
                Name: "server",
                GitUrl: "git@example.com:server.git",
                BaseBranch: null),
            current);

        Assert.True(build.IsSuccess);
        Assert.Equal("main", build.Value.BaseBranch);
    }

    [Fact]
    public void BuildUpdate_OnlyGitUrl_PreservesBaseBranch()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "release", true)]);

        var build = RepositoryPolicy.BuildUpdate(
            "server",
            new RepositoryPolicy.TransitionInput(
                Name: "server",
                GitUrl: "git@example.com:server-v2.git",
                BaseBranch: null),
            current);

        Assert.True(build.IsSuccess);
        Assert.Equal("git@example.com:server-v2.git", build.Value.Next.GitUrl);
        Assert.Equal("release", build.Value.Next.BaseBranch);
    }

    [Fact]
    public void BuildUpdate_OnlyBaseBranch_PreservesGitUrl()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildUpdate(
            "server",
            new RepositoryPolicy.TransitionInput(
                Name: "server",
                GitUrl: null,
                BaseBranch: "develop"),
            current);

        Assert.True(build.IsSuccess);
        Assert.Equal("git@example.com:server.git", build.Value.Next.GitUrl);
        Assert.Equal("develop", build.Value.Next.BaseBranch);
    }

    [Fact]
    public void BuildUpdate_EmptyPatch_RejectedWithUpdateError()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildUpdate(
            "server",
            new RepositoryPolicy.TransitionInput(
                Name: "server",
                GitUrl: null,
                BaseBranch: null),
            current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Code == "update");
    }

    [Fact]
    public void BuildUpdate_UnknownName_RejectedWithNotFoundError()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildUpdate(
            "ghost",
            new RepositoryPolicy.TransitionInput(
                Name: "ghost",
                GitUrl: "git@example.com:other.git",
                BaseBranch: null),
            current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildUpdate_NameIsImmutable_NextNameRemainsOriginal()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildUpdate(
            "server",
            new RepositoryPolicy.TransitionInput(
                Name: "different",
                GitUrl: "git@example.com:server-v2.git",
                BaseBranch: null),
            current);

        Assert.True(build.IsSuccess);
        Assert.Equal("server", build.Value.Next.Name);
    }

    [Fact]
    public void BuildSetDefault_NonDefaultRepository_BecomesOnlyDefault()
    {
        var current = RepositoryPolicy.Normalize(
            [
                new("server", "git@example.com:server.git", "main", true),
                new("web", "git@example.com:web.git", "main", false),
            ]);

        var build = RepositoryPolicy.BuildSetDefault("web", current);

        Assert.True(build.IsSuccess);
        Assert.True(build.Value.Next.IsDefault);
        Assert.Equal("web", build.Value.Next.Name);
    }

    [Fact]
    public void BuildSetDefault_OnCurrentDefault_IsNoOp()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildSetDefault("server", current);

        Assert.True(build.IsSuccess);
        Assert.True(build.Value.Next.IsDefault);
        Assert.Same(build.Value.Previous, build.Value.Next);
    }

    [Fact]
    public void BuildSetDefault_UnknownName_RejectedWithNotFoundError()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildSetDefault("ghost", current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildRemove_NonDefaultRepository_Succeeds()
    {
        var current = RepositoryPolicy.Normalize(
            [
                new("server", "git@example.com:server.git", "main", true),
                new("web", "git@example.com:web.git", "main", false),
            ]);

        var build = RepositoryPolicy.BuildRemove("web", current);

        Assert.True(build.IsSuccess);
        Assert.False(build.Value.IsDefault);
    }

    [Fact]
    public void BuildRemove_DefaultRepository_RejectedAsConflict()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildRemove("server", current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Code == "repository_default_deletion_conflict");
    }

    [Fact]
    public void BuildRemove_UnknownName_RejectedWithNotFoundError()
    {
        var current = RepositoryPolicy.Normalize(
            [new("server", "git@example.com:server.git", "main", true)]);

        var build = RepositoryPolicy.BuildRemove("ghost", current);

        Assert.False(build.IsSuccess);
        Assert.Contains(build.Errors, e => e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveBaseBranch_BlankInput_ReturnsMain()
    {
        Assert.Equal("main", RepositoryPolicy.ResolveBaseBranch(""));
        Assert.Equal("main", RepositoryPolicy.ResolveBaseBranch(null));
        Assert.Equal("main", RepositoryPolicy.ResolveBaseBranch("   "));
        Assert.Equal("develop", RepositoryPolicy.ResolveBaseBranch("develop"));
    }
}
