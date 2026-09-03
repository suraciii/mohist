using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Subscriptions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Orleans;

namespace Mohist.Server.Tests.GitHub;

internal sealed class GitHubPullRequestReviewHandlerTestFactory : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    public const string ProjectId = "project-1";
    public const string ConnectionId = "connection-1";
    public const string RepositoryName = "hello-world";
    public const int PullRequestNumber = 7;
    public const string RepositoryRemote = "https://github.com/octocat/hello-world.git";

    private readonly SqliteConnection _keeper;
    private readonly ServiceProvider _services;
    private readonly GitHubPullRequestReviewHandler _handler;

    private GitHubPullRequestReviewHandlerTestFactory()
    {
        _keeper = new SqliteConnection("Data Source=:memory:");
        _keeper.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(_keeper).Options;
        DbFactory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyModelSchemaTo(_keeper);

        var workflow = DispatchProxy.Create<IWorkflowGrain, RecordingWorkflowGrain>();
        Workflow = (RecordingWorkflowGrain)(object)workflow;
        var grains = DispatchProxy.Create<IGrainFactory, WorkflowGrainFactory>();
        Grains = (WorkflowGrainFactory)(object)grains;
        Grains.Workflow = workflow;

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MohistDbContext>>(DbFactory);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddSingleton<ISecretStore, UnusedSecretStore>();
        services.AddSingleton<GitHubConnectionGate>();
        services.AddScoped<GitHubConnectionStore>();
        services.AddScoped<ProjectQuerier>();
        services.AddScoped<WorkflowRunQuerier>();
        _services = services.BuildServiceProvider();
        _handler = new GitHubPullRequestReviewHandler(
            _services.GetRequiredService<IServiceScopeFactory>(),
            grains,
            NullLogger<GitHubPullRequestReviewHandler>.Instance);
    }

    public TestDbContextFactory DbFactory { get; }
    public RecordingWorkflowGrain Workflow { get; }
    public WorkflowGrainFactory Grains { get; }

    public static async Task<GitHubPullRequestReviewHandlerTestFactory> CreateAsync(
        IReadOnlyList<string> approvers,
        string projectRemote = RepositoryRemote)
    {
        var factory = new GitHubPullRequestReviewHandlerTestFactory();
        await factory.SeedProjectAndConnectionAsync(approvers, projectRemote);
        return factory;
    }

    public async Task AddRunAsync(
        string runId = "run-1",
        int pullRequestNumber = PullRequestNumber,
        string repositoryRemote = RepositoryRemote,
        WorkflowRunStatus status = WorkflowRunStatus.AwaitingApproval,
        string stageId = "check",
        StageRunStatus stageStatus = StageRunStatus.AwaitingApproval,
        bool requiresApproval = true)
    {
        var repository = new WorkflowRepositoryContext(RepositoryName, repositoryRemote, "main");
        var run = new WorkflowRun
        {
            Id = runId,
            Metadata = new WorkflowRunMetadata(null, Now, ProjectId: ProjectId, IssueNumber: 1),
            Status = status,
            CurrentStageId = stageId,
            Stages =
            [
                new StageRun
                {
                    Id = stageId,
                    Attempt = 1,
                    RequiresApproval = requiresApproval,
                    Status = stageStatus,
                    Initialized = true,
                    ApprovalStatus = stageStatus == StageRunStatus.AwaitingApproval
                        ? new ApprovalStatus(null, Now.ToString("O"), null)
                        : null,
                },
            ],
        };
        run.AssignRepositoryContext(repository);
        run.AssignPullRequestIdentity(repository, pullRequestNumber);

        await using var db = DbFactory.CreateDbContext();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = run.Id,
            State = JSON.Serialize(run),
            PullRequestNumber = pullRequestNumber,
        });
        await db.SaveChangesAsync();
    }

    public CloudEvent Review(
        string state,
        string login,
        int pullRequestNumber = PullRequestNumber,
        string? body = null,
        string? branch = null)
    {
        var pullRequest = new Dictionary<string, object?> { ["number"] = pullRequestNumber };
        if (branch is not null)
            pullRequest["head"] = new { @ref = branch };

        return new CloudEvent(
            "review-1",
            new Uri(IngressEventPersistence.ConnectionSource(ProjectId, ConnectionId), UriKind.Relative),
            EventCatalog.ReverseDns.GitHubPullRequestReviewed,
            Now,
            JsonSerializer.SerializeToElement(new
            {
                action = "submitted",
                review = new { state, body, user = new { login } },
                pull_request = pullRequest,
            }));
    }

    public Task HandleAsync(CloudEvent evt) => _handler.HandleAsync(evt, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _keeper.DisposeAsync();
    }

    private async Task SeedProjectAndConnectionAsync(IReadOnlyList<string> approvers, string projectRemote)
    {
        await using var db = DbFactory.CreateDbContext();
        db.Projects.Add(new ProjectRow
        {
            Id = ProjectId,
            Name = "github-review-handler",
            RepositoriesJson = JSON.Serialize(new List<RepositoryInfo>
            {
                new()
                {
                    Name = RepositoryName,
                    GitUrl = projectRemote,
                    BaseBranch = "main",
                    IsDefault = true,
                },
            }),
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.GitHubConnections.Add(new GitHubConnectionRow
        {
            Id = ConnectionId,
            ProjectId = ProjectId,
            Owner = "octocat",
            Repo = RepositoryName,
            RepositoryName = RepositoryName,
            ApproversJson = JSON.Serialize(approvers),
            Status = GitHubConnectionStatus.Active,
            InstallationId = "installation-1",
            RepositoryNodeId = "repository-node-1",
            NeedsAttention = false,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    public sealed record Approval(string? DecidedBy);
    public sealed record ChangeRequest(string Body, string? DecidedBy);

    public class RecordingWorkflowGrain : DispatchProxy
    {
        public List<Approval> Approvals { get; } = [];
        public List<ChangeRequest> ChangeRequests { get; } = [];
        public Exception? RequestChangesFailure { get; set; }
        public IReadOnlyList<object> Decisions => [.. Approvals, .. ChangeRequests];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IWorkflowGrain.ApproveAsync))
            {
                Approvals.Add(new Approval((string?)args![0]));
                return Task.CompletedTask;
            }

            if (targetMethod?.Name == nameof(IWorkflowGrain.RequestChangesAsync))
            {
                ChangeRequests.Add(new ChangeRequest((string)args![0]!, (string?)args[1]));
                return RequestChangesFailure is null
                    ? Task.FromResult("feedback-1")
                    : Task.FromException<string>(RequestChangesFailure);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class WorkflowGrainFactory : DispatchProxy
    {
        public IWorkflowGrain Workflow { get; set; } = null!;
        public int WorkflowRequests { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IWorkflowGrain))
            {
                WorkflowRequests++;
                return Workflow;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class UnusedSecretStore : ISecretStore
    {
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(true);
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
