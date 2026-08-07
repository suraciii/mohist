using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.AgentOps;

/// <summary>
/// Calculation specs for <see cref="ActivityEvidenceAssembler"/>, the
/// service behind <c>GET /api/projects/{projectRef}/activity</c>. Covers
/// the read-merging semantics (project isolation, runner always repeats
/// as global, limit-after-stable-merged ordering, default 100), the
/// approval-gate waiting bucket (in-progress only, with
/// <c>workflowStatus = awaiting-approval</c>), and the empty-waiting
/// shape. Runs against <c>MohistDbFixture</c> without an HTTP round-trip.
/// The route contract (400 limit-out-of-range, one success-path shape)
/// stays in <c>ActivityEvidenceApiSpecs</c>; the waiting route contract
/// (empty waiting shape) stays in <c>ActivityWaitingApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class ActivityEvidenceAssemblerSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MohistDbFixture _fixture;

    public ActivityEvidenceAssemblerSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private ActivityEvidenceAssembler CreateAssembler() =>
        _fixture.Services.GetRequiredService<ActivityEvidenceAssembler>();

    private async Task<string> CreateProjectAsync(string suffix = "activity")
    {
        var projectId = $"project-{suffix}-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var now = FixedTime;
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private async Task AppendIssueEventAsync(
        string projectId,
        int issueNumber,
        string type,
        DateTimeOffset time,
        string? subject = null)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Events.IEventStore>();
        var data = JsonSerializer.SerializeToElement(new { }, Mohist.Server.Infrastructure.Events.CloudEvent.JsonOptions);
        var envelope = new Mohist.Server.Infrastructure.Events.CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri($"/mohist/projects/{projectId}/issues/{issueNumber}", UriKind.Relative),
            type: type,
            time: time,
            data: data,
            subject: subject,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
                ["issue"] = issueNumber.ToString(),
            });
        await store.AppendAsync(envelope);
    }

    private async Task SeedIssueAsync(string projectId, int number)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Issue #{number}",
            Status = IssueStatus.InProgress,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertApprovalGateIssueAsync(
        string projectId,
        int number,
        string title,
        DateTimeOffset approvalRequestedAt)
    {
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.InProgress,
        };
        issue.StartWorkflow(workflowRunId);

        var runState = JsonSerializer.Serialize(new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = FixedTime, Name = "test" },
            Status = "AwaitingApproval",
            CurrentStageId = "plan",
            Stages = new[]
            {
                new
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "AwaitingApproval",
                    Tasks = new[]
                    {
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/opencode" },
                    },
                    Checks = new[]
                    {
                        new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = (string?)null,
                        RequestedAt = approvalRequestedAt.ToString("O"),
                        RespondedAt = (string?)null,
                    },
                },
            },
        });

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, runState);
    }

    private async Task InsertIssueWithStatusAsync(string projectId, int number, string title, IssueStatus status)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = status,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private static RunnerStatusView BuildRunnerStatus(string runnerId, string hostname, DateTimeOffset registeredAt) =>
        new(
            Id: runnerId,
            Kind: "embedded",
            Hostname: hostname,
            Scope: new RunnerScopeView("global"),
            Status: "idle",
            RegisteredAt: registeredAt,
            LastHeartbeatAt: registeredAt,
            ConnectionState: "connected",
            Capabilities: new[] { "spec/*" },
            CoderModels: Array.Empty<string>(),
            CoderModelCount: 0,
            Capacity: new RunnerCapacityView(0, 1),
            ActiveWorks: Array.Empty<RunnerActiveWorkView>());

    [Fact]
    public async Task ListAsync_IsolatesProjectEvidenceWhileRepeatingRunnerOnlyAsGlobal()
    {
        var firstProject = await CreateProjectAsync("first");
        var secondProject = await CreateProjectAsync("second");
        var runnerId = $"runner-{Guid.NewGuid():N}";

        await SeedIssueAsync(firstProject, 11);
        await SeedIssueAsync(secondProject, 22);
        await AppendIssueEventAsync(firstProject, 11, "first.project.event", FixedTime);
        await AppendIssueEventAsync(secondProject, 22, "second.project.event", FixedTime);

        GetRunnerStatusService().SetRunners(new[] { BuildRunnerStatus(runnerId, "shared-host", FixedTime) });

        var first = await CreateAssembler().ListAsync(firstProject, 200);
        var second = await CreateAssembler().ListAsync(secondProject, 200);

        Assert.Contains(first, entry => entry.EventType == "first.project.event");
        Assert.DoesNotContain(first, entry => entry.EventType == "second.project.event");
        Assert.Contains(second, entry => entry.EventType == "second.project.event");
        Assert.DoesNotContain(second, entry => entry.EventType == "first.project.event");
        Assert.Contains(first, entry => entry.RunnerId == runnerId && entry.Scope == "global");
        Assert.Contains(second, entry => entry.RunnerId == runnerId && entry.Scope == "global");
        Assert.DoesNotContain(first.Concat(second), entry => entry.RunnerId == runnerId && entry.Scope == "project");
    }

    [Fact]
    public async Task ListAsync_AppliesLimitAfterStableMergedOrdering()
    {
        var projectId = await CreateProjectAsync("limit");
        await SeedIssueAsync(projectId, 1);
        await AppendIssueEventAsync(projectId, 1, "older", FixedTime.AddMinutes(-1));
        await AppendIssueEventAsync(projectId, 1, "newer", FixedTime.AddMinutes(1));

        var assembler = CreateAssembler();
        var all = await assembler.ListAsync(projectId, 200);
        var first = await assembler.ListAsync(projectId, 1);
        var second = await assembler.ListAsync(projectId, 1);

        Assert.Single(first);
        Assert.Equal(first, second);
        Assert.Equal(all.Take(1), first);
        Assert.True(all.ToList().FindIndex(entry => entry.EventType == "newer")
            < all.ToList().FindIndex(entry => entry.EventType == "older"));
    }

    [Fact]
    public async Task ListAsync_DefaultLimitIsOneHundred()
    {
        var projectId = await CreateProjectAsync("default-limit");
        await SeedIssueAsync(projectId, 1);
        var t0 = FixedTime.AddHours(-10);
        for (var i = 0; i < 105; i++)
        {
            await AppendIssueEventAsync(projectId, 1, $"event-{i:D3}", t0.AddSeconds(i));
        }

        var assembler = CreateAssembler();
        var all = await assembler.ListAsync(projectId, 200);
        var result = await assembler.ListAsync(projectId, 100);

        Assert.Equal(100, result.Count);
        Assert.Equal(all.Take(100), result);
    }

    [Fact]
    public async Task ListAsync_WhenIssuePausedOnApprovalGate_AppearsInWaitingArray()
    {
        var projectId = await CreateProjectAsync("gated");
        await InsertApprovalGateIssueAsync(projectId, 1, "Awaiting product review", FixedTime.AddMinutes(-3));

        var entries = await CreateAssembler().ListAsync(projectId, 200);

        var waiting = entries.Where(entry => entry.Kind == "waiting").ToList();
        var entry = Assert.Single(waiting, e => e.IssueNumber == 1);
        Assert.Equal("Awaiting product review", entry.Title);
        Assert.Equal("Needs Approval at plan", entry.Description);
        Assert.Equal("waiting", entry.Status);
    }

    [Fact]
    public async Task ListAsync_WhenNoIssuePausedOnApprovalGate_HasEmptyWaitingArray()
    {
        var projectId = await CreateProjectAsync("no-gate");
        await InsertIssueWithStatusAsync(projectId, 1, "Backlog only", IssueStatus.Backlog);

        var entries = await CreateAssembler().ListAsync(projectId, 200);

        Assert.DoesNotContain(entries, entry => entry.Kind == "waiting");
    }

    [Fact]
    public async Task ListAsync_OnlyIncludesInProgressIssues_NotBacklogOrDone()
    {
        var projectId = await CreateProjectAsync("only-in-progress");
        await InsertIssueWithStatusAsync(projectId, 1, "Backlog", IssueStatus.Backlog);
        await InsertIssueWithStatusAsync(projectId, 2, "Done", IssueStatus.Done);
        await InsertApprovalGateIssueAsync(projectId, 3, "Gated", FixedTime);

        var entries = await CreateAssembler().ListAsync(projectId, 200);

        var waiting = entries.Where(entry => entry.Kind == "waiting").ToList();
        var entry = Assert.Single(waiting);
        Assert.Equal(3, entry.IssueNumber);
        Assert.Equal("Gated", entry.Title);
    }

    private MohistDbFixture.NoopRunnerStatusService GetRunnerStatusService() =>
        (MohistDbFixture.NoopRunnerStatusService)_fixture.Services.GetRequiredService<RunnerStatusService>();
}
