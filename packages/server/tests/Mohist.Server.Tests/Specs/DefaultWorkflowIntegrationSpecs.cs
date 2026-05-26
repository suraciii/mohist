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
        var projectName = $"default-workflow-{Guid.NewGuid():N}";
        var projectResult = await RunCliAsync("project", "create", projectName, "--path", repo.Path, "--base-branch", "main");
        Assert.Equal(0, projectResult.ExitCode);
        var project = JsonSerializer.Deserialize<ProjectDto>(projectResult.Stdout, JsonOptions)!;

        var issueResult = await RunCliAsync("issue", "create", "Deliver default workflow", "--body", "body", "--priority", "p1", "--project-id", project.Id);
        Assert.Equal(0, issueResult.ExitCode);
        var issue = JsonSerializer.Deserialize<IssueDto>(issueResult.Stdout, JsonOptions)!;
        var changeDir = $"openspec/changes/issue-{issue.Number}";

        var start = await RunCliAsync("issue", "start", issue.Number.ToString(), "--project-id", project.Id);
        Assert.Equal(0, start.ExitCode);

        await using var scope = _fixture.Services.CreateAsyncScope();
        await using var runnerServices = RunnerServices(
            repo.Path,
            issue.Number,
            changeDir,
            scope.ServiceProvider.GetRequiredService<Sessions.AgentSessionService>());
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
            var approvePlan = await RunCliAsync("issue", "approve", issue.Number.ToString(), "--project-id", project.Id);
            Assert.Equal(0, approvePlan.ExitCode);
            await WaitForWorkflowAsync(project.Id, issue.Number, "AwaitingApproval", "check");
            await AssertProductSurfaceDuringWorkflowAsync(project.Id, issue.Number);
            var approveCheck = await RunCliAsync("issue", "approve", issue.Number.ToString(), "--project-id", project.Id);
            Assert.Equal(0, approveCheck.ExitCode);
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
        Assert.Equal("completed", await File.ReadAllTextAsync(Path.Combine(repo.Path, "feature.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "specs", "feature", "spec.md")));
        Assert.Contains("Requirement", await File.ReadAllTextAsync(Path.Combine(repo.Path, "specs", "feature", "spec.md")));
        Assert.Contains(
            Directory.EnumerateDirectories(Path.Combine(repo.Path, "openspec", "changes", "archive")),
            path => path.EndsWith($"issue-{issue.Number}", StringComparison.Ordinal));
        Assert.Equal("main", await GitOutputAsync(repo.Path, "branch", "--show-current"));

        var timeline = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issue.Number}/workflow/timeline?projectId={project.Id}");
        Assert.Equal("Completed", timeline.Status);
        Assert.Contains(timeline.Stages, s => s.Stage == "plan" && s.Approval?.Status == "approved");
        Assert.Contains(timeline.Stages, s => s.Stage == "check" && s.Approval?.Status == "approved");
        Assert.Contains(timeline.Stages, s => s.Stage == "integrate" && s.Tasks.Any(t => t.Uses == "mohist/merge" && t.Status == "completed"));

        var diff = await _client.GetDataAsync<IssueDiffDto>($"/api/issues/{issue.Number}/diff?projectId={project.Id}");
        Assert.True(diff.Available);
        Assert.Equal($"mo/issue-{issue.Number}", diff.Head);
        Assert.Contains(diff.Files, f => f.File == "feature.txt");
        Assert.Contains(diff.Files, f => f.File == "specs/feature/spec.md");

        var worktree = await _client.GetDataAsync<WorktreeStatusDto>($"/api/issues/{issue.Number}/worktree-status?projectId={project.Id}");
        Assert.False(worktree.Exists);

        var sessions = await _client.GetDataAsync<CoderSessionSummaryDto[]>($"/api/issues/{issue.Number}/coder-sessions?projectId={project.Id}");
        Assert.Contains(sessions, s => s.Stage == "plan" && s.Status == "completed");
        Assert.Contains(sessions, s => s.Stage == "build" && s.Status == "completed");

        var detail = await _client.GetDataAsync<CoderSessionDetailDto>($"/api/issues/{issue.Number}/coder-sessions/{sessions[0].Id}?projectId={project.Id}");
        Assert.Equal("completed", detail.Status);
        Assert.NotNull(detail.Metadata);

        var events = await _client.GetDataAsync<EventDto[]>($"/api/issues/{issue.Number}/events?projectId={project.Id}");
        Assert.Contains(events, e => e.Type == "workflow_completed");
        Assert.Contains(events, e => e.Type == "issue_completed");

        var logs = await _client.GetDataAsync<WorkflowLogDto[]>($"/api/issues/{issue.Number}/logs?projectId={project.Id}");
        Assert.Contains(logs, e => e.EventType == "workflow_task_completed");
        Assert.Contains(logs, e => e.EventType == "issue_completed");

        var activity = await _client.GetDataAsync<AgentActivityDto>($"/api/agent/activity?projectId={project.Id}");
        Assert.True(activity.Summary.Completed >= sessions.Length);
        Assert.Contains(activity.Sessions, s => s.IssueNumber == issue.Number && s.Status == "completed");
    }

    private async Task AssertProductSurfaceDuringWorkflowAsync(string projectId, int issueNumber)
    {
        var timeline = await _client.GetDataAsync<WorkflowTimelineDto>($"/api/issues/{issueNumber}/workflow/timeline?projectId={projectId}");
        Assert.Equal("AwaitingApproval", timeline.Status);
        Assert.Equal("check", timeline.CurrentStage);
        Assert.Contains(timeline.Stages, s => s.Stage == "build" && s.Tasks.Any(t => t.Status == "completed"));
        Assert.Contains(timeline.Stages, s => s.Stage == "check" && s.Approval?.Status == "awaiting");

        var worktree = await _client.GetDataAsync<WorktreeStatusDto>($"/api/issues/{issueNumber}/worktree-status?projectId={projectId}");
        Assert.True(worktree.Exists);
        Assert.Equal($"mo/issue-{issueNumber}", worktree.Branch);

        var diff = await _client.GetDataAsync<IssueDiffDto>($"/api/issues/{issueNumber}/diff?projectId={projectId}");
        Assert.True(diff.Available);
        Assert.Contains(diff.Files, f => f.File == "feature.txt");

        var commits = await _client.GetDataAsync<IssueCommitsDto>($"/api/issues/{issueNumber}/commits?projectId={projectId}");
        Assert.True(commits.Available);
        Assert.NotEmpty(commits.Commits);

        var sessions = await _client.GetDataAsync<CoderSessionSummaryDto[]>($"/api/issues/{issueNumber}/coder-sessions?projectId={projectId}");
        Assert.Contains(sessions, s => s.Stage == "plan" && s.Status == "completed");
        Assert.Contains(sessions, s => s.Stage == "build" && s.Status == "completed");

        var activity = await _client.GetDataAsync<AgentActivityDto>($"/api/agent/activity?projectId={projectId}");
        Assert.True(activity.Summary.Completed > 0);
        Assert.Contains(activity.Waiting, w => w.IssueNumber == issueNumber && w.Stage == "check");
    }

    private async Task<CliResult> RunCliAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await new MohistCli(args, stdout, stderr, _fixture.Client).RunAsync();
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
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

    private static ServiceProvider RunnerServices(string repoPath, int issueNumber, string changeDir, Sessions.AgentSessionService sessions)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAgentExecutor>(new DefaultWorkflowFakeAgentExecutor(repoPath, issueNumber, changeDir));
        services.AddSingleton<ISessionTelemetrySink>(new EmbeddedSessionTelemetrySink(sessions));
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
                    await File.WriteAllTextAsync(Path.Combine(request.WorkDir, "feature.txt"), "completed", request.CancellationToken);
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(int Number, string Stage, string Status);
    private sealed record IssueWorkflowStatusDto(WorkflowStatusDto? Workflow);
    private sealed record WorkflowStatusDto(string Status, string? CurrentStage, FailureDto? Failure);
    private sealed record FailureDto(string? Message);
    private sealed record EventDto(string Type);
    private sealed record WorkflowLogDto(string EventType);
    private sealed record WorkflowTimelineDto(string WorkflowRunId, string Status, string? CurrentStage, WorkflowStageDto[] Stages);
    private sealed record WorkflowStageDto(string Stage, string Status, WorkflowTaskDto[] Tasks, ApprovalDto? Approval);
    private sealed record WorkflowTaskDto(string Id, string Title, string? Uses, string Status);
    private sealed record ApprovalDto(string Status);
    private sealed record WorktreeStatusDto(bool Exists, string? Branch, string? BaseBranch, int Ahead, int Behind, bool RebaseInProgress, string[] ConflictingFiles);
    private sealed record IssueDiffDto(bool Available, string? Reason, string? Base, string? Head, DiffSummaryDto? Summary, DiffFileDto[] Files);
    private sealed record DiffSummaryDto(int FilesChanged, int Commits, int Additions, int Deletions);
    private sealed record DiffFileDto(string File, int Additions, int Deletions, string Diff, bool IsBinary);
    private sealed record IssueCommitsDto(bool Available, string? Reason, CommitDto[] Commits);
    private sealed record CommitDto(string Hash, string ShortHash, string Message, string Author, string Date);
    private sealed record CoderSessionSummaryDto(string Id, string SessionId, string ExecutionId, string? Title, string Status, string CreatedAt, string? CompletedAt, string? Model, string? Provider, string? Stage, string? TaskDescription, string? LastDataAt, string? Summary, string? TranscriptPath, string? FailureReason);
    private sealed record CoderSessionDetailDto(string Id, string SessionId, string ExecutionId, string? Title, string Status, string CreatedAt, string? CompletedAt, string? Model, string? Provider, string? Stage, string? TaskDescription, object? Metadata);
    private sealed record AgentActivityDto(AgentActivitySummaryDto Summary, AgentActivityCardDto[] Sessions, AgentActivityWaitingCardDto[] Waiting);
    private sealed record AgentActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, AgentActivitySlotUsageDto Slots);
    private sealed record AgentActivitySlotUsageDto(int Active, int Max);
    private sealed record AgentActivityCardDto(int IssueNumber, string Status);
    private sealed record AgentActivityWaitingCardDto(int IssueNumber, string? Stage);

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
