using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Xunit;

namespace Mohist.Server.Tests.Issue.Services;

[Trait("level", "L0")]
public sealed class RepositorySourcePreflightTests
{
    [Fact]
    public async Task CheckAsync_ExistingRemoteBaseBranch_IsValid()
    {
        string? observedUrl = null;
        string? observedBranch = null;
        var control = new RecordingRunnerControlTransport();
        control.SetInvocationResponseFactory("repository.preflight", args =>
        {
            var request = Assert.IsType<RepositoryPreflightParams>(args[0]);
            observedUrl = request.GitUrl;
            observedBranch = request.BaseBranch;
            return new RepositoryPreflightResult(0);
        });
        var preflight = CreatePreflight(control);

        var result = await preflight.CheckAsync("project", new RepositoryInfo
        {
            Name = "origin",
            GitUrl = " git@example.com:mohist.git ",
            BaseBranch = " main ",
        });

        Assert.True(result.IsValid);
        Assert.Equal("git@example.com:mohist.git", observedUrl);
        Assert.Equal("main", observedBranch);
    }

    [Fact]
    public async Task CheckAsync_MissingRemoteBaseBranch_ReportsActionableFailure()
    {
        var preflight = CreatePreflight(exitCode: 2);

        var result = await preflight.CheckAsync("project", new RepositoryInfo
        {
            Name = "origin",
            GitUrl = "git@example.com:mohist.git",
            BaseBranch = "release",
        });

        Assert.False(result.IsValid);
        Assert.Equal("repository_base_branch_missing", result.Code);
        Assert.Equal("repository.baseBranch", result.Field);
        Assert.Contains("origin/release", result.NextStep);
        Assert.Contains("No Workspace, WorkflowRun, or AgentJob was created", result.NextStep);
    }

    [Fact]
    public async Task CheckAsync_InvalidRemoteUrl_DoesNotProbe()
    {
        var control = new RecordingRunnerControlTransport();
        var preflight = CreatePreflight(control);

        var result = await preflight.CheckAsync("project", new RepositoryInfo
        {
            Name = "origin",
            GitUrl = "https://user:password@example.com/mohist.git",
            BaseBranch = "main",
        });

        Assert.False(result.IsValid);
        Assert.Equal("repository_remote_invalid", result.Code);
        Assert.Equal("repository.gitUrl", result.Field);
        Assert.Empty(control.Invocations);
        Assert.Contains("No Workspace, WorkflowRun, or AgentJob was created", result.NextStep);
    }

    [Theory]
    [InlineData("ext::sh -c 'touch /tmp/pwned'")]
    [InlineData("helper::arbitrary-command")]
    [InlineData("evil://host/repo")]
    [InlineData("git+ssh://host/repo")]
    public async Task CheckAsync_ExecutableTransport_IsRejectedWithoutProbe(string gitUrl)
    {
        var control = new RecordingRunnerControlTransport();
        var preflight = CreatePreflight(control);

        var result = await preflight.CheckAsync("project", new RepositoryInfo
        {
            Name = "origin",
            GitUrl = gitUrl,
            BaseBranch = "main",
        });

        Assert.False(result.IsValid);
        Assert.Equal("repository_remote_unsafe_transport", result.Code);
        Assert.Equal("repository.gitUrl", result.Field);
        Assert.Contains("unsupported Git transport", result.Message);
        Assert.Empty(control.Invocations);
    }

    [Fact]
    public async Task CheckAsync_GitExecutableUnavailable_ReportsActionableFailure()
    {
        var preflight = CreatePreflight(exitCode: -1);

        var result = await preflight.CheckAsync("project", new RepositoryInfo
        {
            Name = "origin",
            GitUrl = "https://example.com/mohist.git",
            BaseBranch = "main",
        });

        Assert.False(result.IsValid);
        Assert.Equal("repository_git_unavailable", result.Code);
        Assert.Equal("repository.gitUrl", result.Field);
        Assert.Contains("Git", result.NextStep);
        Assert.Contains("No Workspace, WorkflowRun, or AgentJob was created", result.NextStep);
    }

    [Fact]
    public async Task CheckAsync_NoConnectedRunner_RejectsBeforeDispatch()
    {
        var control = new RecordingRunnerControlTransport();
        var preflight = CreatePreflight(control);

        var result = await preflight.CheckAsync("project", new RepositoryInfo
        {
            Name = "origin", GitUrl = "https://example.com/mohist.git", BaseBranch = "main",
        });

        Assert.Equal("repository_runner_unavailable", result.Code);
        Assert.Empty(control.Invocations);
    }

    private static RepositorySourcePreflight CreatePreflight(int exitCode)
    {
        var control = new RecordingRunnerControlTransport();
        control.SetInvocationResponse("repository.preflight", new RepositoryPreflightResult(exitCode));
        return CreatePreflight(control);
    }

    private static RepositorySourcePreflight CreatePreflight(RecordingRunnerControlTransport control) =>
        new(control, _ => Task.FromResult<IReadOnlyList<RunnerInfo>>(
            [new RunnerInfo("runner-1", [], "test-host", "project")]));
}
