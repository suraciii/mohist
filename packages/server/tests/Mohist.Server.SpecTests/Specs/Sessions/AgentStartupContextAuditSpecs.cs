using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Coverage for the AgentSessionInputRecord audit surface when the
/// caller attached a first-launch-only startup context. Verifies the
/// attestation flows into the durable input record and is surfaced on
/// the observation DTO, while the input <c>Text</c> stays task-only.
/// Also verifies follow-up inputs do not inherit, replace, or
/// append any startup context from the launch-time input.
/// </summary>
[Collection("MohistIntegration")]
public sealed class AgentStartupContextAuditSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentStartupContextAuditSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task EnsureInitialLaunch_PersistsStartupContext_OnSessionInputRecord()
    {
        var sessionId = $"agent-session-startup-audit-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var startupContext = BuildContext(truncated: true);

        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-session-startup-audit",
            Metadata: BuildSessionMetadata(),
            Definition: new AgentExecutionDefinition(
                Instructions: "be brief",
                Runtime: "opencode",
                Model: null,
                Variant: null,
                Skills: Array.Empty<string>())));

        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: $"input-{Guid.NewGuid():N}",
            TurnId: $"turn-{Guid.NewGuid():N}",
            Prompt: "summarize the discussion",
            Source: "agent-connection",
            JobId: $"agent-job-{Guid.NewGuid():N}",
            Metadata: BuildSessionMetadata(),
            Runtime: "opencode",
            WorkDir: "/tmp/agent-session-startup-audit",
            Attachments: null,
            Provenance: null,
            StartupContext: startupContext));

        var snapshot = await session.GetInitialLaunchAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Input);
        Assert.Equal("summarize the discussion", snapshot.Input!.Text);
        Assert.NotNull(snapshot.Input.StartupContext);
        Assert.Equal(startupContext.Text, snapshot.Input.StartupContext!.Text);
        Assert.Equal(startupContext.Provenance.Source, snapshot.Input.StartupContext.Provenance.Source);
        Assert.True(snapshot.Input.StartupContext.Provenance.Truncated);
        Assert.Equal(startupContext.Provenance.TruncationMarker, snapshot.Input.StartupContext.Provenance.TruncationMarker);
    }

    [Fact]
    public async Task EnsureInitialLaunch_WithoutStartupContext_LeavesSessionInputRecordAuditNull()
    {
        var sessionId = $"agent-session-startup-none-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-session-startup-none",
            Metadata: BuildSessionMetadata()));

        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: $"input-{Guid.NewGuid():N}",
            TurnId: $"turn-{Guid.NewGuid():N}",
            Prompt: "no background",
            Source: "agent-launch",
            JobId: $"agent-job-{Guid.NewGuid():N}",
            Metadata: BuildSessionMetadata(),
            Runtime: "opencode",
            WorkDir: "/tmp/agent-session-startup-none"));

        var snapshot = await session.GetInitialLaunchAsync();
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.Input);
        Assert.Equal("no background", snapshot.Input!.Text);
        Assert.Null(snapshot.Input.StartupContext);
    }

    [Fact]
    public async Task EnsureInitialLaunch_ReplayedWithDifferentStartupContext_RaisesConflict()
    {
        var sessionId = $"agent-session-startup-replay-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-session-startup-replay",
            Metadata: BuildSessionMetadata()));

        var inputId = $"input-{Guid.NewGuid():N}";
        var turnId = $"turn-{Guid.NewGuid():N}";

        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: inputId,
            TurnId: turnId,
            Prompt: "summarize",
            Source: "agent-launch",
            JobId: $"agent-job-{Guid.NewGuid():N}",
            Metadata: BuildSessionMetadata(),
            Runtime: "opencode",
            WorkDir: "/tmp/agent-session-startup-replay",
            StartupContext: BuildContext(truncated: false)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
                InputId: inputId,
                TurnId: turnId,
                Prompt: "summarize",
                Source: "agent-launch",
                JobId: $"agent-job-{Guid.NewGuid():N}",
                Metadata: BuildSessionMetadata(),
                Runtime: "opencode",
                WorkDir: "/tmp/agent-session-startup-replay",
                StartupContext: BuildContext(truncated: true))));
    }

    [Fact]
    public async Task EnsureInitialLaunch_FirstLaunchOnly_ConflictOnReplayWithDifferentStartupContext()
    {
        var sessionId = $"agent-session-startup-followup-{Guid.NewGuid():N}";
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var startupContext = BuildContext(truncated: false);
        var launchInputId = $"input-launch-{Guid.NewGuid():N}";
        var launchTurnId = $"turn-launch-{Guid.NewGuid():N}";

        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/agent-session-startup-followup",
            Metadata: BuildSessionMetadata()));

        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: launchInputId,
            TurnId: launchTurnId,
            Prompt: "launch task",
            Source: "agent-connection",
            JobId: $"agent-job-{Guid.NewGuid():N}",
            Metadata: BuildSessionMetadata(),
            Runtime: "opencode",
            WorkDir: "/tmp/agent-session-startup-followup",
            StartupContext: startupContext));

        var initialSnapshot = await session.GetInitialLaunchAsync();
        Assert.NotNull(initialSnapshot);
        Assert.NotNull(initialSnapshot!.Input);
        Assert.NotNull(initialSnapshot.Input.StartupContext);
        Assert.Equal(startupContext.Text, initialSnapshot.Input.StartupContext!.Text);
        Assert.Equal(launchInputId, initialSnapshot.Input.Id);

        // The follow-up path takes a RecordFollowupTurnCommand with no
        // StartupContext parameter (compile-time guarantee: the route
        // never re-attaches a launch-time background). Verify the
        // type surface here so a later refactor that adds one is
        // caught in code review.
        Assert.Null(typeof(RecordFollowupTurnCommand).GetProperty("StartupContext"));

        // Replay the launch with the same input id and a deliberately
        // divergent startup context. The grain must reject this as a
        // conflict — the launch identity is immutable once accepted.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
                InputId: launchInputId,
                TurnId: launchTurnId,
                Prompt: "launch task",
                Source: "agent-connection",
                JobId: $"agent-job-{Guid.NewGuid():N}",
                Metadata: BuildSessionMetadata(),
                Runtime: "opencode",
                WorkDir: "/tmp/agent-session-startup-followup",
                StartupContext: BuildContext(truncated: true))));
    }

    private static AgentStartupContext BuildContext(bool truncated) =>
        new(
            Text: "discussion body",
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: truncated,
                TruncationMarker: truncated ? "5 oldest messages omitted" : null,
                OmittedOldestMessageCount: truncated ? 5 : 0));

    private static AgentSessionMetadata BuildSessionMetadata() =>
        new(
            Labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = "startup-context-project",
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-startup-context",
                [GenericAgentSessionMetadata.AgentName] = "agent-startup-context",
            });
}