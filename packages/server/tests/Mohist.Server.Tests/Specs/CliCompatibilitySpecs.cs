using Mohist.Cli;
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
    public async Task CliServerHealth_ReportsHealth()
    {
        var result = await RunCliAsync("server", "health");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ok", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CliProviderCommands_ExposeLocalOpencodeRuntimeAndSaveConfig()
    {
        var runtime = await RunCliAsync("providers", "runtime");
        Assert.Equal(0, runtime.ExitCode);
        Assert.Contains("local-opencode", runtime.Stdout);

        var test = await RunCliAsync("providers", "test");
        Assert.Equal(0, test.ExitCode);
        Assert.Contains("configuration-only", test.Stdout);

        var providerId = $"custom-{Guid.NewGuid():N}";
        var save = await RunCliAsync("providers", "save", providerId, "--name", "Custom AI", "--base-url", "https://api.example.test", "--model", "model-a");
        Assert.Equal(0, save.ExitCode);
        Assert.Contains(providerId, save.Stdout);

        var list = await RunCliAsync("providers", "list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("Custom AI", list.Stdout);
        Assert.DoesNotContain("sk-test", list.Stdout);
    }

    [Fact]
    public async Task CliInstallCommands_WriteSystemdUserUnits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-cli-install-{Guid.NewGuid():N}");
        var unitDir = Path.Combine(root, "units");
        var repoRoot = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoRoot);

        try
        {
            var server = await RunCliAsync("server", "install", "--dry-run", "--unit-dir", unitDir, "--repo-root", repoRoot, "--listen-url", "http://127.0.0.1:4567");
            Assert.Equal(0, server.ExitCode);
            var serverUnit = await File.ReadAllTextAsync(Path.Combine(unitDir, "mohist.service"));
            Assert.Contains("Description=Mohist Server", serverUnit);
            Assert.Contains("ExecStart=dotnet run --project", serverUnit);
            Assert.Contains("Mohist.Server.csproj", serverUnit);
            Assert.Contains("http://127.0.0.1:4567", serverUnit);
            Assert.Contains("SuccessExitStatus=0 143", serverUnit);

            var runnerRoot = Path.Combine(root, "runner-root");
            var runner = await RunCliAsync("runner", "install", "--dry-run", "--unit-dir", unitDir, "--repo-root", repoRoot, "--server-url", "http://127.0.0.1:4567", "--runner-root", runnerRoot);
            Assert.Equal(0, runner.ExitCode);
            var runnerUnit = await File.ReadAllTextAsync(Path.Combine(unitDir, "mohist-runner.service"));
            Assert.Contains("Description=Mohist Runner", runnerUnit);
            Assert.Contains("ExecStart=npm run start -w packages/runner", runnerUnit);
            Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", runnerUnit);
            Assert.Contains($"Environment=\"RUNNER_ROOT={runnerRoot}\"", runnerUnit);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CliServiceLifecycleCommands_ControlSystemdUserUnits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-cli-service-{Guid.NewGuid():N}");
        var unitDir = Path.Combine(root, "units");

        try
        {
            var serverStart = await RunCliAsync("server", "start", "--dry-run");
            Assert.Equal(0, serverStart.ExitCode);
            Assert.Contains("systemctl --user start mohist.service", serverStart.Stdout);

            var serverStatus = await RunCliAsync("server", "status", "--dry-run");
            Assert.Equal(0, serverStatus.ExitCode);
            Assert.Contains("systemctl --user status --no-pager mohist.service", serverStatus.Stdout);

            var serverLogs = await RunCliAsync("server", "logs", "--dry-run", "-n", "25", "--follow");
            Assert.Equal(0, serverLogs.ExitCode);
            Assert.Contains("journalctl --user -u mohist.service --no-pager -n 25 -f", serverLogs.Stdout);

            var serverUninstall = await RunCliAsync("server", "uninstall", "--dry-run", "--unit-dir", unitDir);
            Assert.Equal(0, serverUninstall.ExitCode);
            Assert.Contains("systemctl --user disable --now mohist.service", serverUninstall.Stdout);
            Assert.Contains(Path.Combine(unitDir, "mohist.service"), serverUninstall.Stdout);

            var runnerRestart = await RunCliAsync("runner", "restart", "--dry-run");
            Assert.Equal(0, runnerRestart.ExitCode);
            Assert.Contains("systemctl --user restart mohist-runner.service", runnerRestart.Stdout);

            var runnerUninstall = await RunCliAsync("runner", "uninstall", "--dry-run", "--unit-dir", unitDir);
            Assert.Equal(0, runnerUninstall.ExitCode);
            Assert.Contains("systemctl --user disable --now mohist-runner.service", runnerUninstall.Stdout);
            Assert.Contains(Path.Combine(unitDir, "mohist-runner.service"), runnerUninstall.Stdout);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private async Task<CliResult> RunCliAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await MohistCliCommands.RunAsync(_fixture.Client, args, stdout, stderr);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
