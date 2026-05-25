using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Runner;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;
using Mohist.Server.Runner.Embedded;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class DefaultWorkflowIntegrationSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public DefaultWorkflowIntegrationSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task DefaultWorkflow_IntegratesRealWorkspaceBeforeIssueCompletes()
    {
        using var repo = new TestTempDir();
        await InitRepositoryAsync(repo.Path);
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new
        {
            name = $"default-workflow-{Guid.NewGuid():N}",
            path = repo.Path,
            baseBranch = "main"
        });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new
        {
            title = "Deliver default workflow",
            body = "body",
            labels = Array.Empty<string>(),
            priority = "p1",
            projectId = project.Id
        });
        var changeDir = $"openspec/changes/issue-{issue.Number}";

        await _client.PostOkAsync($"/api/issues/{issue.Number}/start?projectId={project.Id}");

        await using var scope = _fixture.Services.CreateAsyncScope();
        await using var runnerServices = RunnerServices(repo.Path, issue.Number, changeDir);
        var runnerId = $"default-workflow-runner-{Guid.NewGuid():N}";
        var connection = new EmbeddedRunnerConnection(
            _fixture.Grains,
            scope.ServiceProvider.GetRequiredService<Sessions.AgentSessionService>(),
            scope.ServiceProvider.GetRequiredService<ILogger<EmbeddedRunnerConnection>>(),
            runnerId);
        var actionManager = new ActionManager(runnerServices, runnerServices.GetRequiredService<ILogger<ActionManager>>());
        RunnerActionCatalog.RegisterDefaults(actionManager, runnerServices);
        var executor = new WorkExecutor(
            actionManager,
            scope.ServiceProvider.GetRequiredService<ILogger<WorkExecutor>>(),
            new WorkspaceManager(runnerServices.GetRequiredService<ILogger<WorkspaceManager>>(), _fixture.RunnerRoot));
        var host = new RunnerHost(
            connection,
            executor,
            scope.ServiceProvider.GetRequiredService<ILogger<RunnerHost>>(),
            TimeProvider.System,
            new RunnerHostOptions { IdleDelay = TimeSpan.FromMilliseconds(10), HeartbeatInterval = TimeSpan.FromHours(1) });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var runTask = host.RunAsync(cts.Token);
        try
        {
            await WaitForWorkflowAsync(project.Id, issue.Number, "AwaitingApproval", "plan");
            await _client.PostOkAsync($"/api/issues/{issue.Number}/approve?projectId={project.Id}");
            await WaitForWorkflowAsync(project.Id, issue.Number, "AwaitingApproval", "check");
            await _client.PostOkAsync($"/api/issues/{issue.Number}/approve?projectId={project.Id}");
            await WaitForWorkflowAsync(project.Id, issue.Number, "Completed", null);
        }
        finally
        {
            await cts.CancelAsync();
            await runTask;
        }

        var completed = await _client.GetDataAsync<IssueDto>($"/api/issues/{issue.Number}?projectId={project.Id}");
        Assert.Equal("done", completed.Stage);
        Assert.Equal("completed", completed.Status);

        var mainFiles = await GitOutputAsync(repo.Path, "ls-tree", "-r", "--name-only", "main");
        Assert.True(File.Exists(Path.Combine(repo.Path, "feature.txt")), $"main tree:\n{mainFiles}");
        Assert.Equal("delivered", await File.ReadAllTextAsync(Path.Combine(repo.Path, "feature.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "specs", "feature", "spec.md")));
        Assert.Contains("Requirement", await File.ReadAllTextAsync(Path.Combine(repo.Path, "specs", "feature", "spec.md")));
        Assert.Contains(
            Directory.EnumerateDirectories(Path.Combine(repo.Path, "openspec", "changes", "archive")),
            path => path.EndsWith($"issue-{issue.Number}", StringComparison.Ordinal));
        Assert.Equal("main", await GitOutputAsync(repo.Path, "branch", "--show-current"));

        var events = await _client.GetDataAsync<EventDto[]>($"/api/issues/{issue.Number}/events?projectId={project.Id}");
        Assert.Contains(events, e => e.Type == "workflow_completed");
        Assert.Contains(events, e => e.Type == "issue_completed");
    }

    private async Task WaitForWorkflowAsync(string projectId, int issueNumber, string status, string? stage)
    {
        for (var i = 0; i < 200; i++)
        {
            var current = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/issues/{issueNumber}/workflow/status?projectId={projectId}");
            if (current.Workflow?.Status == status && (stage is null || current.Workflow.CurrentStage == stage))
                return;
            await Task.Delay(50);
        }

        var final = await _client.GetDataAsync<IssueWorkflowStatusDto>($"/api/issues/{issueNumber}/workflow/status?projectId={projectId}");
        Assert.Fail($"Workflow did not reach {status}/{stage ?? "*"}; current={final.Workflow?.Status}/{final.Workflow?.CurrentStage}; failure={final.Workflow?.Failure?.Message}");
    }

    private static ServiceProvider RunnerServices(string repoPath, int issueNumber, string changeDir)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentExecutor>(new DefaultWorkflowFakeAgentExecutor(repoPath, issueNumber, changeDir));
        services.AddSingleton<ISessionTelemetrySink, NullSessionTelemetrySink>();
        services.AddSingleton<IAgentCompletionVerifier, AgentCompletionVerifier>();
        services.AddSingleton<IAgentSessionRepairer, NoopAgentSessionRepairer>();
        return services.BuildServiceProvider();
    }

    private sealed class DefaultWorkflowFakeAgentExecutor : IAgentExecutor
    {
        private readonly string _repoPath;
        private readonly int _issueNumber;
        private readonly string _changeDir;

        public DefaultWorkflowFakeAgentExecutor(string repoPath, int issueNumber, string changeDir)
        {
            _repoPath = repoPath;
            _issueNumber = issueNumber;
            _changeDir = changeDir;
        }

        public async Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request)
        {
            switch (request.Task)
            {
                case "proposal":
                    await WriteAsync(request.WorkDir, "proposal.md", "# Proposal\n");
                    break;
                case "specs":
                    await WriteAsync(request.WorkDir, Path.Combine("specs", "feature", "spec.md"), "Requirement: deliver feature\n");
                    break;
                case "design":
                    await WriteAsync(request.WorkDir, "design.md", "# Design\n");
                    break;
                case "tasks":
                    await WriteAsync(request.WorkDir, "tasks.json", JsonSerializer.Serialize(new
                    {
                        tasks = new[]
                        {
                            new { id = "build-feature", title = "Build feature", uses = "mohist/agent" }
                        }
                    }));
                    break;
                case "self-review":
                    await WriteAsync(request.WorkDir, "self-review.md", "PASS\n");
                    break;
                case var task when request.Stage == "build" && task.StartsWith("build-feature", StringComparison.Ordinal):
                    await File.WriteAllTextAsync(Path.Combine(request.WorkDir, "feature.txt"), "delivered", request.CancellationToken);
                    await GitAsync(request.WorkDir, request.CancellationToken, "add", ".");
                    await GitAsync(request.WorkDir, request.CancellationToken, "commit", "-m", $"issue {_issueNumber} implementation");
                    break;
                case "ai-review":
                    await WriteAsync(request.WorkDir, "review.md", "PASS\n");
                    break;
                default:
                    break;
            }

            return new AgentExecutionResult(0, "ok");
        }

        private async Task WriteAsync(string workspace, string relativePath, string content)
        {
            var path = Path.Combine(workspace, _changeDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }
    }

    private static async Task InitRepositoryAsync(string path)
    {
        await GitAsync(path, CancellationToken.None, "init", "-b", "main");
        await GitAsync(path, CancellationToken.None, "config", "user.email", "mohist@example.test");
        await GitAsync(path, CancellationToken.None, "config", "user.name", "Mohist Test");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(path, "package.json"), """
        {
          "scripts": {
            "typecheck": "node -e \"process.exit(0)\"",
            "build": "node -e \"process.exit(0)\"",
            "test": "node -e \"process.exit(0)\""
          },
          "devDependencies": {}
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(path, "package-lock.json"), """
        {
          "name": "mohist-default-workflow-fixture",
          "lockfileVersion": 3,
          "requires": true,
          "packages": {
            "": {
              "devDependencies": {}
            }
          }
        }
        """);
        await GitAsync(path, CancellationToken.None, "add", ".");
        await GitAsync(path, CancellationToken.None, "commit", "-m", "initial");
    }

    private static async Task GitAsync(string workDir, CancellationToken ct, params string[] args)
    {
        var result = await RunGitAsync(workDir, ct, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}{result.Output}");
    }

    private static async Task<string> GitOutputAsync(string workDir, params string[] args)
    {
        var result = await RunGitAsync(workDir, CancellationToken.None, args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        return result.Output.Trim();
    }

    private static async Task<(string Output, string Error, int ExitCode)> RunGitAsync(string workDir, CancellationToken ct, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (output, error, process.ExitCode);
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace)
        ? $"\"{value.Replace("\"", "\\\"")}\""
        : value;

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(int Number, string Stage, string Status);
    private sealed record IssueWorkflowStatusDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage, FailureDto? Failure);
    private sealed record FailureDto(string? Message);
    private sealed record EventDto(string Type);

    private sealed class TestTempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mohist-default-workflow-{Guid.NewGuid():N}");

        public TestTempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
