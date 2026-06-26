using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Subscriptions;

[Collection("MohistIntegration")]
public class RunnerWorkflowTerminalStatusHandlerSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly RecordingRunnerHubContext _hub;
    private readonly RunnerConnectionTracker _tracker;
    private readonly List<string> _registeredRunnerIds = [];
    private readonly List<string> _seededWorkflowIds = [];

    public RunnerWorkflowTerminalStatusHandlerSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _hub = (RecordingRunnerHubContext)_fixture.Services.GetRequiredService<IHubContext<RunnerHub>>();
        _tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
    }

    public Task InitializeAsync()
    {
        _hub.Clear();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var runnerId in _registeredRunnerIds)
        {
            using var _ = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
        foreach (var workflowRunId in _seededWorkflowIds)
        {
            try
            {
                await _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId).DeactivateForTestAsync();
            }
            catch
            {
                // best-effort: ignore deactivation errors in dispose
            }
        }
    }

    private async Task<string> RegisterRunnerWithConnectionAsync(string connectionId)
    {
        var runnerId = $"runner-terminal-{Guid.NewGuid():N}";
        using var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "terminal-host",
        });
        register.EnsureSuccessStatusCode();

        using var heartbeat = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/heartbeat", new
        {
            connectionId,
        });
        heartbeat.EnsureSuccessStatusCode();

        _registeredRunnerIds.Add(runnerId);
        return runnerId;
    }

    private async Task<string> SeedRunningWorkflowAsync(string runnerId)
    {
        var workflowRunId = $"wf-terminal-{Guid.NewGuid():N}";
        var assignedAt = DateTimeOffset.UtcNow;

        // Persist a Running row with an assignment so the workflow grain
        // returns the runner id when asked. Status is canonical
        // WorkflowRunStatus.Failed so the test can verify the router
        // receives the terminal name end-to-end. The route handlers and
        // hub invocation are exercised regardless of which terminal
        // status we choose.
        var run = new WorkflowRun
        {
            Id = workflowRunId,
            Metadata = new WorkflowRunMetadata(
                Name: "terminal-test",
                CreatedAt: DateTimeOffset.UtcNow,
                Labels: null,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = "terminal-test-project",
                }),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Assignment = new WorkflowAssignment(runnerId, assignedAt),
            Stages = new List<StageRun>
            {
                new()
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Initialized = true,
                    Tasks = new List<TaskRun>(),
                    Checks = new List<StageCheck>(),
                },
            },
        };

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        await using (var db = new MohistDbContext(options))
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = workflowRunId,
                State = JSON.Serialize(run),
            });
            await db.SaveChangesAsync();
        }

        _seededWorkflowIds.Add(workflowRunId);
        return workflowRunId;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task FailedTerminalEvent_RunnerConnected_PushesReceiveWorkflowRunStatus()
    {
        var runnerId = await RegisterRunnerWithConnectionAsync("conn-failed-push");
        var workflowRunId = await SeedRunningWorkflowAsync(runnerId);

        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.StopAsync("test-failed-stop");

        var pushed = await WaitForPushAsync(connectionId: "conn-failed-push", workflowRunId: workflowRunId);
        Assert.NotNull(pushed);
        Assert.Equal("ReceiveWorkflowRunStatus", pushed!.Method);
        var payload = Assert.IsType<WorkflowRunStatusNotification>(Assert.Single(pushed.Arguments));
        Assert.Equal(workflowRunId, payload.WorkflowRunId);
        Assert.Equal("Stopped", payload.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task TerminalEvent_RunnerNotConnected_NoPushDelivered()
    {
        // Register a runner but do NOT register a SignalR connection id,
        // so the tracker has no entry and the router drops the push.
        var runnerId = $"runner-terminal-no-conn-{Guid.NewGuid():N}";
        using var register = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "no-conn-host",
        });
        register.EnsureSuccessStatusCode();
        _registeredRunnerIds.Add(runnerId);

        var workflowRunId = await SeedRunningWorkflowAsync(runnerId);

        _hub.Clear();
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.StopAsync("test-no-conn-stop");

        // Give the bus a moment to deliver to subscribers; nothing should
        // arrive because the runner has no connection id.
        await Task.Delay(200);
        Assert.DoesNotContain(_hub.SentMessages, m => m.Method == "ReceiveWorkflowRunStatus");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task TerminalEvent_NoAssignment_NoPushDelivered()
    {
        // Register a runner with a connection, but the seeded workflow
        // has no assignment — the router must drop the push because
        // there is no owning runner to route to.
        var runnerId = await RegisterRunnerWithConnectionAsync("conn-unassigned");
        var workflowRunId = await SeedRunningWorkflowAsync("different-runner-id");

        _hub.Clear();
        var workflow = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId);
        await workflow.StopAsync("test-no-assignment-stop");

        await Task.Delay(200);
        Assert.DoesNotContain(_hub.SentMessages, m => m.Method == "ReceiveWorkflowRunStatus");
    }

    private async Task<RecordedRunnerHubMessage?> WaitForPushAsync(string connectionId, string workflowRunId)
    {
        // The CloudEvent bus + workflow grain commit + SignalR push are
        // three asynchronous hops. Poll the recorded messages for up to
        // ~2 seconds; the test environment is in-process so this is well
        // within tolerance.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = _hub.SentMessages.FirstOrDefault(m =>
                m.Method == "ReceiveWorkflowRunStatus" &&
                string.Equals(m.ConnectionId, connectionId, StringComparison.Ordinal));
            if (match is not null)
                return match;

            await Task.Delay(25);
        }

        return null;
    }
}