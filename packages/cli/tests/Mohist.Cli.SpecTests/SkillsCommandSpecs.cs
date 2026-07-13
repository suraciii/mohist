using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.TestSupport;
using Xunit;

namespace Mohist.Cli.SpecTests;

public sealed class SkillsCommandSpecs
{
    [Fact]
    public async Task SkillsHelp_DescribesCoderAgentSkillManagement_AndListsExpectedSubcommands()
    {
        var (exitCode, stdout, stderr) = await InvokeAsync("skills", "--help");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Manage coder agent skills", stdout);
        Assert.Contains("install", stdout);
        Assert.Contains("list", stdout);
        Assert.Contains("get", stdout);
        Assert.Contains("path", stdout);
        Assert.DoesNotContain("update", stdout);
    }

    [Fact]
    public async Task SkillsUpdate_IsNotRegistered()
    {
        var (exitCode, _, stderr) = await InvokeAsync("skills", "update");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("update", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await MohistCliCommands.RunAsync(
            new HttpClient(new RejectingHttpHandler())
            {
                BaseAddress = new Uri(CliTestFactory.BaseAddress),
            },
            args,
            stdout,
            stderr,
            new FakeFileSystem(),
            new RejectingCommandExecutor(),
            new MockEnvironmentVariableProvider());

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class RejectingCommandExecutor : ICommandExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName,
            string[] args,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"Unexpected command: {fileName}");
    }

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}");
    }
}
