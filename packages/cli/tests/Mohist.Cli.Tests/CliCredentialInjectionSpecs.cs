using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

/// <summary>
/// The command credential is injected centrally by
/// <see cref="CliCredentialHandler"/> for every mo command — including
/// ones that never mention credentials themselves (issue list stands in
/// for the whole non-Slack/non-dead-letter command surface, AC #3 of
/// issue #318: "本机 mo 零配置可用").
/// </summary>
public sealed class CliCredentialInjectionSpecs
{
    private const string FileToken = "file-admin-token-0123456789abcdef";
    private const string EnvToken = "env-token-0123456789abcdef0123456789";

    [Fact]
    public async Task IssueList_WithAdminTokenFile_SendsAuthorizationBearer()
    {
        var env = NewEnvironment(home: "/home/test", tokenEnvironmentVariable: null);
        env.FileSystem.AddFile("/home/test/.mohist/admin-token", FileToken);

        var exitCode = await RunIssueListAsync(env);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(
            $"Bearer {FileToken}",
            Assert.Single(request.Headers["Authorization"]));
    }

    [Fact]
    public async Task IssueList_WithAdminTokenPathEnvironment_SendsAuthorizationBearer()
    {
        var env = NewEnvironment(home: "/home/test", tokenEnvironmentVariable: null);
        env.Environment[CliCredentialProvider.AdminTokenPathEnvironmentVariable] = "/run/mohist/admin-token";
        env.FileSystem.AddFile("/run/mohist/admin-token", FileToken);

        var exitCode = await RunIssueListAsync(env);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(
            $"Bearer {FileToken}",
            Assert.Single(request.Headers["Authorization"]));
    }

    [Fact]
    public async Task IssueList_WithMohistTokenEnvironmentVariable_SendsAuthorizationBearer()
    {
        var env = NewEnvironment(home: "/home/test", tokenEnvironmentVariable: EnvToken);

        var exitCode = await RunIssueListAsync(env);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(
            $"Bearer {EnvToken}",
            Assert.Single(request.Headers["Authorization"]));
    }

    [Fact]
    public async Task IssueList_WithoutAnyCredential_SendsNoAuthorizationHeader()
    {
        var env = NewEnvironment(home: "/home/no-credential", tokenEnvironmentVariable: null);

        var exitCode = await RunIssueListAsync(env);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.False(request.Headers.TryGetValue("Authorization", out _));
    }

    [Fact]
    public async Task IssueList_EnvironmentToken_IsSentToNonLoopbackServers()
    {
        var env = NewEnvironment(home: "/home/test", tokenEnvironmentVariable: EnvToken);
        env.Http.BaseAddress = new Uri("https://mohist.example.test");

        var exitCode = await RunIssueListAsync(env);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.Equal(
            $"Bearer {EnvToken}",
            Assert.Single(request.Headers["Authorization"]));
    }

    [Fact]
    public async Task IssueList_FileToken_IsNotSentToNonLoopbackServers()
    {
        var env = NewEnvironment(home: "/home/test", tokenEnvironmentVariable: null);
        env.FileSystem.AddFile("/home/test/.mohist/admin-token", FileToken);
        env.Http.BaseAddress = new Uri("https://mohist.example.test");

        var exitCode = await RunIssueListAsync(env);

        Assert.Equal(0, exitCode);
        var request = Assert.Single(env.Handler.Requests);
        Assert.False(request.Headers.TryGetValue("Authorization", out _));
    }

    private static async Task<int> RunIssueListAsync(CliEnvironment env)
    {
        return await MohistCliCommands.RunAsync(
            env.Http,
            ["issue", "list"],
            env.Output,
            env.Error,
            env.FileSystem,
            env.Executor,
            env.Environment);
    }

    private static CliEnvironment NewEnvironment(string home, string? tokenEnvironmentVariable)
    {
        var (handler, http, output, error, fileSystem, executor) = CliTestFactory.Create(
            (_, _) => Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = Array.Empty<object>(),
            })));
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
        if (tokenEnvironmentVariable is not null)
            environment[CliCredentialProvider.TokenEnvironmentVariable] = tokenEnvironmentVariable;
        environment["HOME"] = home;
        return new CliEnvironment(http, handler, output, error, fileSystem, executor, environment);
    }

    private sealed record CliEnvironment(
        HttpClient Http,
        RecordingHttpHandler Handler,
        StringWriter Output,
        StringWriter Error,
        FakeFileSystem FileSystem,
        FakeCommandExecutor Executor,
        MockEnvironmentVariableProvider Environment);
}
