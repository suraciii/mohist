using Mohist.Server.Sessions.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public sealed class WorkspaceModeNormalizerTests
{
    [Theory]
    [InlineData(null, WorkspaceMode.Inherit)]
    [InlineData("", WorkspaceMode.Inherit)]
    [InlineData("   ", WorkspaceMode.Inherit)]
    [InlineData("inherit", WorkspaceMode.Inherit)]
    [InlineData("worktree", WorkspaceMode.Worktree)]
    public void TryNormalize_AcceptsDefaultAndWhitelist(string? workspace, WorkspaceMode expected)
    {
        Assert.True(WorkspaceModeNormalizer.TryNormalize(workspace, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("INHERIT")]
    [InlineData("Worktree")]
    [InlineData("work-tree")]
    [InlineData("/some/path")]
    [InlineData("worktree2")]
    public void TryNormalize_RejectsAnythingElse(string workspace)
    {
        Assert.False(WorkspaceModeNormalizer.TryNormalize(workspace, out var mode));
        Assert.Equal(default(WorkspaceMode), mode);
    }

    [Theory]
    [InlineData(null, "inherit")]
    [InlineData("", "inherit")]
    [InlineData("inherit", "inherit")]
    [InlineData("worktree", "worktree")]
    public void FingerprintToken_NormalizesValidModes(string? workspace, string token)
    {
        Assert.Equal(token, WorkspaceModeNormalizer.FingerprintToken(workspace));
    }

    [Fact]
    public void FingerprintToken_KeepsInvalidRawTextSoReplayHashesStablyAndConflictsDiffer()
    {
        Assert.Equal("bogus", WorkspaceModeNormalizer.FingerprintToken("bogus"));
        Assert.NotEqual(
            WorkspaceModeNormalizer.FingerprintToken("bogus"),
            WorkspaceModeNormalizer.FingerprintToken("other"));
    }
}
