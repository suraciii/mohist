using Mohist.Server.GitHub.Domain;
using Xunit;

namespace Mohist.Server.L0Tests.GitHub;

public sealed class GitHubConnectionTests
{
    private static GitHubConnection Valid() => new()
    {
        Id = "ghconn_1",
        ProjectId = "proj_1",
        Owner = "octocat",
        Repo = "hello-world",
        RepositoryName = "hello-world",
        InstallationId = "installation-1",
        RepositoryNodeId = "repo-node-1",
    };

    [Fact]
    public void Validate_AcceptsVerifiedAppConnection()
    {
        Valid().Validate();
    }

    [Fact]
    public void Validate_RejectsInvalidStatus()
    {
        var connection = Valid();
        connection.Status = "archived";
        Assert.Throws<GitHubConnectionValidationException>(() => connection.Validate());
    }

    [Fact]
    public void Validate_RequiresInstallationAndRepositoryIdentity()
    {
        var connection = Valid();
        connection.InstallationId = null;
        var ex = Assert.Throws<GitHubConnectionValidationException>(() => connection.Validate());
        Assert.Equal("installation_id_required", ex.Code);

        connection.InstallationId = "installation-1";
        connection.RepositoryNodeId = null;
        ex = Assert.Throws<GitHubConnectionValidationException>(() => connection.Validate());
        Assert.Equal("repository_node_id_required", ex.Code);
    }

    [Fact]
    public void Validate_RejectsReconnectRequiredActiveConnection()
    {
        var connection = Valid();
        connection.ReconnectRequired = true;
        var ex = Assert.Throws<GitHubConnectionValidationException>(() => connection.Validate());
        Assert.Equal("invalid_reconnect_state", ex.Code);
    }
}
