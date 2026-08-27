using Mohist.Server.GitHub.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.GitHub;

public sealed class GitHubConnectionTests
{
    private static GitHubConnection Valid() => new()
    {
        Id = "ghconn_1",
        ProjectId = "proj_1",
        Owner = "octocat",
        Repo = "hello-world",
        RepositoryName = "hello-world",
    };

    [Fact]
    public void Validate_AcceptsDefaultConnection()
    {
        Valid().Validate(requireInstallationId: false);
    }

    [Fact]
    public void Validate_RejectsInvalidEnums()
    {
        var status = Valid();
        status.Status = "archived";
        Assert.Throws<GitHubConnectionValidationException>(() => status.Validate(requireInstallationId: false));

        var identity = Valid();
        identity.IdentityKind = "machine-user";
        Assert.Throws<GitHubConnectionValidationException>(() => identity.Validate(requireInstallationId: false));
    }

    [Fact]
    public void Validate_RequiresInstallationIdForAppIdentityWhenDemanded()
    {
        var connection = Valid();
        connection.IdentityKind = GitHubIdentityKind.App;

        var ex = Assert.Throws<GitHubConnectionValidationException>(() => connection.Validate());
        Assert.Equal("installation_id_required", ex.Code);

        connection.InstallationId = "123456";
        connection.Validate();
    }

    [Fact]
    public void Validate_AllowsMissingInstallationIdForPatIdentity()
    {
        var connection = Valid();
        connection.IdentityKind = GitHubIdentityKind.Pat;

        connection.Validate();
    }
}
