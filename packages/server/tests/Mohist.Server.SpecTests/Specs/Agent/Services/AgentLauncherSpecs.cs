using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

/// <summary>
/// Specs for the shared <see cref="IAgentLauncher"/> service extracted in
/// issue-391 T-001 (originally inlined in
/// <c>AgentSessionLaunchRoutes.cs:73-97</c>). The HTTP manual launch path
/// is covered by <see cref="Api.AgentSessionLaunchRoutesSpecs"/>; this
/// file proves the launcher's per-invocation contract holds end-to-end:
/// <list type="bullet">
///   <item>
///     trigger labels are merged into the resulting session's metadata
///     labels (subscription-driven launches) — covers D6 from the
///     change design doc.
///   </item>
///   <item>
///     no <c>mohist.io/trigger/*</c> labels appear on sessions started
///     with the default trigger label (manual HTTP launch path) — covers
///     the visibility spec "Manually launched sessions carry no trigger
///     labels".
///   </item>
///   <item>
///     prompt validation rejects empty/whitespace prompts before any
///     grain call (so a partial state isn't left in the silo or DB) —
///     covers the launcher-side defense in addition to the HTTP-route
///     prompt_required gate.
///   </item>
/// </list>
/// </summary>
[Collection("IntegrationRunner")]
public class AgentLauncherSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentLauncherSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Launch_WithTriggerLabels_MergesThemIntoSessionMetadataLabels()
    {
        var projectId = await CreateProjectAsync("launcher-trigger-merge");
        var agent = await CreateAgentAsync(projectId, "trigger-merge-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "please review",
                new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                triggerLabels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GenericAgentSessionMetadata.TriggerEventId] = "evt_abc123",
                    [GenericAgentSessionMetadata.TriggerRuleId] = "sub_def456",
                });
        }

        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.Equal(agent.Id, result.AgentId);
        Assert.Equal("trigger-merge-agent", result.AgentName);

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);
        Assert.Equal(
            "evt_abc123",
            record!.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerEventId));
        Assert.Equal(
            "sub_def456",
            record.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerRuleId));

        // Sanity: subscription-driven launch still carries the generic
        // labels that every agent-launch session has.
        Assert.Equal(agent.Id, record.Session.Metadata.Label(GenericAgentSessionMetadata.AgentId));
        Assert.Equal("trigger-merge-agent", record.Session.Metadata.Label(GenericAgentSessionMetadata.AgentName));
        Assert.Equal(projectId, record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId));
        Assert.Equal(
            "agent-launch",
            record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.SourceKind));
    }

    [Fact]
    public async Task Launch_RepeatedTrigger_ReusesStableSession()
    {
        var projectId = await CreateProjectAsync("launcher-trigger-idempotent");
        var agent = await CreateAgentAsync(projectId, "trigger-idempotent-agent");
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = "evt_repeat",
            [GenericAgentSessionMetadata.TriggerRuleId] = "sub_repeat",
        };

        AgentLaunchResult first;
        AgentLaunchResult second;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            var context = new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null);
            first = await launcher.LaunchAsync(agent, "review once", context, labels);
            second = await launcher.LaunchAsync(agent, "review once", context, labels);
        }

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.StartsWith("agent-session-", first.SessionId, StringComparison.Ordinal);
        Assert.Equal(1, await CountSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_WithContextRefs_RecordsThemAsSessionMetadataLabelsOnly()
    {
        var projectId = await CreateProjectAsync("launcher-context-refs");
        var agent = await CreateAgentAsync(projectId, "context-refs-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "look at the issue",
                new AgentLaunchContext(
                    ProjectId: projectId,
                    IssueNumber: 42,
                    EpicNumber: 7,
                    Repository: "feature-repo",
                    WorkspacePath: "/tmp/launch-ctx",
                    Title: null),
                triggerLabels: null);
        }

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);
        Assert.Equal("42", record!.Session.Metadata.Label(GenericAgentSessionMetadata.IssueNumber));
        Assert.Equal("7", record.Session.Metadata.Label(GenericAgentSessionMetadata.EpicNumber));
        Assert.Equal("feature-repo", record.Session.Metadata.Label(GenericAgentSessionMetadata.Repository));
        Assert.Equal("/tmp/launch-ctx", record.Session.Metadata.Label(GenericAgentSessionMetadata.WorkspacePath));

        // Context refs are prompt context only — no lifecycle labels.
        Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
        Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.SessionName));
    }

    [Fact]
    public async Task Launch_TriggerReplayAfterJobDeactivation_ReusesDurableWork()
    {
        var projectId = await CreateProjectAsync("launcher-trigger-replay");
        var agent = await CreateAgentAsync(projectId, "trigger-replay-agent");
        var eventId = "evt_trigger_replay";
        var subscriptionId = "sub_trigger_replay";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = eventId,
            [GenericAgentSessionMetadata.TriggerRuleId] = subscriptionId,
        };
        var runnerId = $"launcher-trigger-runner-{Guid.NewGuid():N}";
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "launcher-trigger-host",
            projectId));

        try
        {
            AgentLaunchResult first;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                first = await launcher.LaunchAsync(
                    agent,
                    "resume this trigger",
                    new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                    labels);
            }

            var jobKey = TriggerJobKey(projectId, eventId, subscriptionId);
            var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobKey);
            var before = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Running, before.Status);
            Assert.Equal(runnerId, before.RunnerId);
            Assert.False(string.IsNullOrWhiteSpace(before.CurrentWorkId));

            await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

            AgentLaunchResult replay;
            await using (var scope = _fixture.Services.CreateAsyncScope())
            {
                var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
                replay = await launcher.LaunchAsync(
                    agent,
                    "resume this trigger",
                    new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                    labels);
            }

            var after = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(first.SessionId, replay.SessionId);
            Assert.Equal(before.CurrentWorkId, after.CurrentWorkId);
            var runnerState = await runner.GetRuntimeStateAsync();
            var work = Assert.Single(runnerState.ActiveWorks, item => item.OwnerId == jobKey);
            Assert.Equal(before.CurrentWorkId, work.WorkId);
        }
        finally
        {
            await _fixture.Grains
                .GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .UnregisterAsync(runnerId);
        }
    }

    [Fact]
    public async Task DispatcherDelivery_ResolvesSiloScopedAgentLaunchServices()
    {
        var projectId = await CreateProjectAsync("launcher-dispatcher-scope");
        var agent = await CreateAgentAsync(projectId, "dispatcher-scope-agent");
        var eventId = $"evt_dispatcher_scope_{Guid.NewGuid():N}";
        var subscriptionId = $"sub_dispatcher_scope_{Guid.NewGuid():N}";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var subscriptions = scope.ServiceProvider.GetRequiredService<AgentSubscriptionStore>();
            await subscriptions.CreateAsync(new AgentSubscription
            {
                Id = subscriptionId,
                ProjectId = projectId,
                AgentId = agent.Id,
                Name = subscriptionId,
                Filter = new SubscriptionFilter { Type = "test.agent.launch" },
                ResponsePrompt = "handle the event",
                Status = SubscriptionStatus.Active,
            });
        }

        await _fixture.Services.GetRequiredService<IEventStore>().AppendAsync(new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/issues/issue_{Guid.NewGuid():N}", UriKind.Relative),
            type: "test.agent.launch",
            time: new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
            data: null,
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectid"] = projectId,
            }));

        await _fixture.Grains
            .GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global)
            .DispatchNowAsync();

        await using var readScope = _fixture.Services.CreateAsyncScope();
        var sessions = await readScope.ServiceProvider
            .GetRequiredService<AgentSessionQuery>()
            .ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GenericAgentSessionMetadata.TriggerEventId] = eventId,
                [GenericAgentSessionMetadata.TriggerRuleId] = subscriptionId,
            });
        var session = Assert.Single(sessions);
        Assert.Equal(agent.Id, session.Session.Metadata.Label(GenericAgentSessionMetadata.AgentId));
    }

    [Fact]
    public async Task Launch_WithoutTriggerLabels_ProducesNoTriggerMetadataLabels()
    {
        var projectId = await CreateProjectAsync("launcher-no-trigger");
        var agent = await CreateAgentAsync(projectId, "no-trigger-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "manual trigger",
                new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                triggerLabels: null);
        }

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);

        // Spec requirement: manually launched sessions carry no
        // trigger metadata — neither key may appear at all (we
        // distinguish "absent" from "empty string").
        Assert.Null(record!.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerEventId));
        Assert.Null(record.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerRuleId));

        var labels = record.Session.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.DoesNotContain(labels, kv => kv.Key.StartsWith("mohist.io/trigger/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Launch_WithEmptyTriggerLabels_ProducesNoTriggerMetadataLabels()
    {
        var projectId = await CreateProjectAsync("launcher-empty-trigger");
        var agent = await CreateAgentAsync(projectId, "empty-trigger-agent");

        AgentLaunchResult result;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();
            result = await launcher.LaunchAsync(
                agent,
                prompt: "empty trigger labels",
                new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                triggerLabels: new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var record = await LoadSessionByIdAsync(result.SessionId);
        Assert.NotNull(record);
        Assert.Null(record!.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerEventId));
        Assert.Null(record.Session.Metadata.Label(GenericAgentSessionMetadata.TriggerRuleId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public async Task Launch_WithBlankPrompt_ThrowsArgumentException_WithoutAnySideEffects(string prompt)
    {
        var projectId = await CreateProjectAsync("launcher-blank-prompt");
        var agent = await CreateAgentAsync(projectId, "blank-prompt-agent");

        var sessionsBefore = await CountSessionsAsync(projectId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            launcher.LaunchAsync(
                agent,
                prompt,
                new AgentLaunchContext(ProjectId: projectId, WorkspacePath: null),
                triggerLabels: null));

        var sessionsAfter = await CountSessionsAsync(projectId);
        Assert.Equal(sessionsBefore, sessionsAfter);
    }

    [Fact]
    public async Task Launch_WithNullAgent_ThrowsArgumentNullException()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            launcher.LaunchAsync(
                agent: null!,
                prompt: "any prompt",
                new AgentLaunchContext(ProjectId: "any", WorkspacePath: null),
                triggerLabels: null));
    }

    [Fact]
    public async Task Launch_WithNullContext_ThrowsArgumentNullException()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IAgentLauncher>();

        var dummyAgent = new AgentInfo(
            Id: "agent_dummy",
            ProjectId: "proj_dummy",
            Name: "dummy",
            Description: "",
            Instructions: "",
            AgentConfig: null,
            Skills: Array.Empty<string>(),
            MaxConcurrentRuns: null,
            Status: AgentStatus.Active,
            CreatedAt: "2026-06-30T00:00:00Z",
            UpdatedAt: "2026-06-30T00:00:00Z");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            launcher.LaunchAsync(
                dummyAgent,
                prompt: "any prompt",
                context: null!,
                triggerLabels: null));
    }

    private async Task<int> CountSessionsAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            });
        return records.Count;
    }

    private static string TriggerJobKey(string projectId, string eventId, string subscriptionId)
    {
        var identity = $"{projectId}\n{eventId}\n{subscriptionId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"agent-job-trigger-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private async Task<AgentSessionRecord?> LoadSessionByIdAsync(string sessionId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var query = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
        var records = await query.ListByIdsAsync(new[] { sessionId });
        return records.FirstOrDefault();
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateProject '{name}' failed: {(int)response.StatusCode} {body}");
        }
        var bodyElement = await response.Content.ReadFromJsonAsync<JsonElement>();
        return bodyElement.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    private async Task<AgentInfo> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { type = "opencode" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = body.GetProperty("data").GetProperty("id").GetString()!;

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var agent = await querier.GetByIdAsync(projectId, agentId);
        Assert.NotNull(agent);
        return agent!;
    }
}
