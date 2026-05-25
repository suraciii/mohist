using Mohist.Server.Tests.Support;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class CliCompatibilitySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public CliCompatibilitySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CliProjectAndIssueCommands_CallCurrentDotNetApi()
    {
        var projectName = $"cli-{Guid.NewGuid():N}";
        var createProject = await RunCliAsync("project", "create", projectName, "--path", "/tmp/mohist-cli", "--base-branch", "main");
        Assert.Equal(0, createProject.ExitCode);
        Assert.Contains(projectName, createProject.Stdout);
        var projectId = JsonDocument.Parse(createProject.Stdout).RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(projectId));

        var projects = await RunCliAsync("project", "list");
        Assert.Equal(0, projects.ExitCode);
        Assert.Contains(projectName, projects.Stdout);

        var issue = await RunCliAsync("issue", "create", "CLI parity issue", "--body", "body", "--priority", "p1", "--project-id", projectId!);
        Assert.Equal(0, issue.ExitCode);
        Assert.Contains("CLI parity issue", issue.Stdout);
        var issueNumber = JsonDocument.Parse(issue.Stdout).RootElement.GetProperty("number").GetInt32();

        var issues = await RunCliAsync("issue", "list", "--all", "--project-id", projectId!);
        Assert.Equal(0, issues.ExitCode);
        Assert.Contains("CLI parity issue", issues.Stdout);

        var show = await RunCliAsync("issue", "show", issueNumber.ToString(), "--project-id", projectId!);
        Assert.Equal(0, show.ExitCode);
        Assert.Contains($"\"number\": {issueNumber}", show.Stdout);

        var status = await RunCliAsync("status");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains(projectName, status.Stdout);
    }

    [Fact]
    public async Task CliServerStatus_ReportsHealth()
    {
        var result = await RunCliAsync("server", "status");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ok", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CliResult> RunCliAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await new MohistCli(args, stdout, stderr, _fixture.Client).RunAsync();
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
