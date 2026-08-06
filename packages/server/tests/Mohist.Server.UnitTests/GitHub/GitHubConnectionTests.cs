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
    public void Validate_RejectsReservedIntakeLabelPrefix()
    {
        var connection = Valid();
        connection.IntakeLabel = "mohist:in-progress";

        var ex = Assert.Throws<GitHubConnectionValidationException>(() => connection.Validate(requireInstallationId: false));
        Assert.Equal("intake_label_prefix_reserved", ex.Code);
    }

    [Fact]
    public void Validate_RejectsInvalidEnums()
    {
        var feedMode = Valid();
        feedMode.FeedMode = "instant";
        Assert.Throws<GitHubConnectionValidationException>(() => feedMode.Validate(requireInstallationId: false));

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
