using Mohist.Server.Workspace.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Workspace;

public class WorkspacePolicyTests
{
    private static readonly WorkspaceOrigin Manual = new WorkspaceOrigin.Manual();

    [Fact]
    public void ValidateCreate_EmptyName_ReportsWorkspaceNameInvalid()
    {
        var error = WorkspacePolicy.ValidateCreate("  ", Manual, [], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_name_invalid", error!.Code);
    }

    [Fact]
    public void ValidateCreate_NameWithColon_ReportsWorkspaceNameInvalid()
    {
        var error = WorkspacePolicy.ValidateCreate("a:b", Manual, [], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_name_invalid", error!.Code);
    }

    [Fact]
    public void ValidateCreate_UnknownRepository_ReportsWorkspaceRepositoryNotFound()
    {
        var error = WorkspacePolicy.ValidateCreate("pay", Manual, ["missing"], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_repository_not_found", error!.Code);
    }

    [Fact]
    public void ValidateCreate_DuplicateRepository_ReportsWorkspaceRepositoryDuplicate()
    {
        var error = WorkspacePolicy.ValidateCreate("pay", Manual, ["server", "server"], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_repository_duplicate", error!.Code);
    }

    [Fact]
    public void ValidateCreate_UnknownRepositoryNameIsCaseInsensitive()
    {
        var error = WorkspacePolicy.ValidateCreate("pay", Manual, ["SERVER"], ["server"]);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_ValidInput_ReturnsNoError()
    {
        var error = WorkspacePolicy.ValidateCreate("pay", Manual, ["server", "web"], ["server", "web"]);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateAddRepository_EmptyName_ReportsRepositoryRequired()
    {
        var error = WorkspacePolicy.ValidateAddRepository(" ", [], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_repository_required", error!.Code);
    }

    [Fact]
    public void ValidateAddRepository_Duplicate_ReportsDuplicate()
    {
        var error = WorkspacePolicy.ValidateAddRepository("server", ["server"], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_repository_duplicate", error!.Code);
    }

    [Fact]
    public void ValidateAddRepository_NotOnProject_ReportsNotFound()
    {
        var error = WorkspacePolicy.ValidateAddRepository("infra", ["server"], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_repository_not_found", error!.Code);
    }

    [Fact]
    public void ValidateRemoveRepository_Missing_ReportsNotFound()
    {
        var error = WorkspacePolicy.ValidateRemoveRepository("web", ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_repository_not_found", error!.Code);
    }

    [Fact]
    public void ValidateRemoveRepository_Present_ReturnsNoError()
    {
        var error = WorkspacePolicy.ValidateRemoveRepository("server", ["server"]);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateActiveSessions_ActiveSession_ReportsConflict()
    {
        var error = WorkspacePolicy.ValidateActiveSessions("pay", 2);

        Assert.NotNull(error);
        Assert.Equal("workspace_has_active_sessions", error!.Code);
        Assert.Contains("2 active bound session", error.Message);
    }

    [Fact]
    public void ValidateActiveSessions_NoActiveSessions_ReturnsNoError()
    {
        Assert.Null(WorkspacePolicy.ValidateActiveSessions("pay", 0));
    }

    [Fact]
    public void IsManual_ManualOrigin_True()
    {
        Assert.True(WorkspacePolicy.IsManual(new WorkspaceOrigin.Manual()));
        Assert.False(WorkspacePolicy.IsManual(new WorkspaceOrigin.Issue(1)));
    }

    [Theory]
    [InlineData("issue-42")]
    [InlineData("issue-1")]
    [InlineData("issue-99999")]
    public void TryNormalizeName_IssuePrefix_Accepts(string raw)
    {
        Assert.True(WorkspacePolicy.TryNormalizeName(raw, out var name));
        Assert.Equal(raw, name);
    }

    [Fact]
    public void ValidateCreate_IssueOrigin_ValidInput_ReturnsNoError()
    {
        var issueOrigin = new WorkspaceOrigin.Issue(42);
        var error = WorkspacePolicy.ValidateCreate("issue-42", issueOrigin, ["server"], ["server"]);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateCreate_IssueOrigin_NullOrigin_ReportsRequired()
    {
        var error = WorkspacePolicy.ValidateCreate("issue-42", null!, ["server"], ["server"]);
        Assert.NotNull(error);
        Assert.Equal("workspace_origin_required", error!.Code);
    }
}
