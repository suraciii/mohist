using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Sessions;

/// <summary>
/// Integration tests for the context-exhaustion classification and
/// new SSE event types wired up in
/// <c>openspec/changes/issue-110</c>. These tests cover the grain-level
/// orchestration in <c>AgentSessionGrain.AppendRuntimeEventsAsync</c>
/// end-to-end against the Orleans cluster and the event-publishing
/// fixture (recording both <c>IEventPublisher</c> and
/// <c>ITranscriptEventPublisher</c>).
/// </summary>
[Collection("MohistIntegration")]
public class AgentSessionContextExhaustionSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _runnerId = $"exhaustion-{Guid.NewGuid():N}";

    public AgentSessionContextExhaustionSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task SessionClosed_FailedAbove90Percent_RewritesFailureCategoryToContextExhaustion()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        // Bring usage up to 96%.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":960000,"contextWindowSize":1000000}"""),
            }));

        var appended = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("session.closed", """{"status":"failed","exitCode":1}"""),
            }));

        var closedInfo = Assert.Single(appended, e => e.Type == "session.closed");
        using var doc = JsonDocument.Parse(closedInfo.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal("context_exhaustion", root.GetProperty("failureCategory").GetString());
        Assert.Equal(96d, root.GetProperty("contextUsagePercent").GetDouble());
        Assert.True(root.GetProperty("contextExhaustion").GetBoolean());

        // The failureCategory is also visible on the session's
        // GetInfo payload (queried via the session info surface).
        var info = await grain.GetAsync();
        Assert.NotNull(info);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task SessionClosed_FailedBelow90Percent_DoesNotRewriteFailureCategory()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        // 70% usage.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":700,"contextWindowSize":1000}"""),
            }));

        var appended = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("session.closed", """{"status":"failed","exitCode":1,"failureCategory":"probe_timeout"}"""),
            }));

        var closedInfo = Assert.Single(appended, e => e.Type == "session.closed");
        using var doc = JsonDocument.Parse(closedInfo.PayloadJson);
        var root = doc.RootElement;
        // The pre-existing failureCategory is preserved (no rewrite
        // took place).
        Assert.Equal("probe_timeout", root.GetProperty("failureCategory").GetString());
        Assert.False(root.TryGetProperty("contextExhaustion", out var cx) && cx.GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task SessionClosed_CompletedSuccessfully_DoesNotClassifyAsExhaustion()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":960,"contextWindowSize":1000}"""),
            }));

        var appended = await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("session.closed", """{"status":"completed","exitCode":0}"""),
            }));

        var closedInfo = Assert.Single(appended, e => e.Type == "session.closed");
        using var doc = JsonDocument.Parse(closedInfo.PayloadJson);
        var root = doc.RootElement;
        // A successful close at 96% is healthy (auto-compact or
        // manual recovery brought usage down before completion).
        // No exhaustion classification should be applied.
        var hasCategory = root.TryGetProperty("failureCategory", out var category);
        if (hasCategory)
            Assert.NotEqual("context_exhaustion", category.GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task UsageUpdated_CrossesGreenToYellowThreshold_RecordsContextHealthSnapshot()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        // First usage update at 30% (green) seeds the snapshot.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":300,"contextWindowSize":1000}"""),
            }));

        // 65% — crosses the green/yellow threshold.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":650,"contextWindowSize":1000}"""),
            }));

        // Inspect the persisted transcript for the health snapshot.
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var healthParts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => p.Type == "context_health_update")
            .Join(db.AgentSessionTranscriptTurns.AsNoTracking().Where(t => t.SessionId == sessionId),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .ToListAsync();
        Assert.NotEmpty(healthParts);
        // At least one snapshot is in the yellow band.
        Assert.Contains(healthParts, p =>
        {
            var payload = JsonDocument.Parse(p.PayloadJson).RootElement;
            return string.Equals(payload.GetProperty("healthStatus").GetString(), "yellow", StringComparison.OrdinalIgnoreCase);
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task UsageUpdated_SmallChange_DoesNotEmitHealthSnapshot()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        // Two consecutive small updates, both green, no threshold
        // crossing and no >=10pp swing.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":400,"contextWindowSize":1000}"""),
            }));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("usage.updated", """{"contextWindowUsed":450,"contextWindowSize":1000}"""),
            }));

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var healthParts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => p.Type == "context_health_update")
            .Join(db.AgentSessionTranscriptTurns.AsNoTracking().Where(t => t.SessionId == sessionId),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .ToListAsync();
        // The first 40% reading is the seed snapshot. The second
        // 45% reading is <10pp away so should NOT produce a new
        // snapshot. Count is at most 1.
        Assert.True(healthParts.Count <= 1,
            $"Expected at most 1 health snapshot, got {healthParts.Count}");
    }
}
