using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.IntegrationSpecs.Support;
using Xunit;

namespace Mohist.Server.IntegrationSpecs.Specs.Sessions;

[Collection("IntegrationSessions")]
public class AgentSessionGrainRecoverySpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _runnerId = $"grain-recovery-{Guid.NewGuid():N}";

    public AgentSessionGrainRecoverySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CompactAsync_OpenedSession_RebindsToNewSessionAndPersistsCompaction()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        var opened = await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(NewAgentSessionId: "acp-compacted", Summary: "Compact summary"));

        Assert.Equal("acp-compacted", result.AgentSessionId);
        Assert.Equal("compact", result.Operation);
        Assert.True(result.WasCompacted);

        var state = await grain.GetAsync();
        Assert.NotNull(state);
        Assert.Equal("acp-compacted", state!.AgentSessionId);
        Assert.Equal(opened.Id, state.Id);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => p.Type == "compaction")
            .Join(db.AgentSessionTranscriptTurns.AsNoTracking().Where(t => t.SessionId == state.Id),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .ToListAsync();
        Assert.NotEmpty(parts);
        var payload = JsonDocument.Parse(parts.First().PayloadJson).RootElement;
        Assert.Equal("summary", payload.GetProperty("strategy").GetString());
        Assert.Equal("Compact summary", payload.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task CompactAsync_WithoutSummary_GeneratesSummaryFromTranscript()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(NewAgentSessionId: "acp-after"));

        Assert.Equal("acp-after", result.AgentSessionId);
        Assert.True(result.WasCompacted);

        var state = await grain.GetAsync();
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var compaction = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => p.Type == "compaction")
            .Join(db.AgentSessionTranscriptTurns.AsNoTracking().Where(t => t.SessionId == state!.Id),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .FirstAsync();
        var payload = JsonDocument.Parse(compaction.PayloadJson).RootElement;
        Assert.True(payload.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task CompactAsync_ActiveSession_Throws()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            AgentSessionId: "acp-active",
            Model: "opencode/test",
            WorkDir: "/work"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("session.liveness", "{\"status\":\"probing\"}")
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand(NewAgentSessionId: "acp-new")));
        Assert.Contains("active", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetAsync_OpenedSession_RebindsToNewSessionWithoutSummary()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        var result = await grain.ResetAsync(new ResetAgentSessionCommand(NewAgentSessionId: "acp-reset"));

        Assert.Equal("acp-reset", result.AgentSessionId);
        Assert.Equal("reset", result.Operation);
        Assert.False(result.WasCompacted);

        var state = await grain.GetAsync();
        Assert.NotNull(state);
        Assert.Equal("acp-reset", state!.AgentSessionId);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => p.Type == "compaction")
            .Join(db.AgentSessionTranscriptTurns.AsNoTracking().Where(t => t.SessionId == state.Id),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .ToListAsync();
        Assert.NotEmpty(parts);
        var payload = JsonDocument.Parse(parts.First().PayloadJson).RootElement;
        Assert.Equal("reset", payload.GetProperty("strategy").GetString());
        Assert.False(payload.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task ResetAsync_ActiveSession_Throws()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            AgentSessionId: "acp-active-reset",
            Model: "opencode/test",
            WorkDir: "/work"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            RuntimeEvents: new[]
            {
                new AgentSessionRuntimeEventInput("session.liveness", "{\"status\":\"probing\"}")
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.ResetAsync(new ResetAgentSessionCommand(NewAgentSessionId: "acp-reset")));
        Assert.Contains("active", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompactAsync_NonexistentSession_Throws()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand(NewAgentSessionId: "acp-fresh")));
    }

    [Fact]
    public async Task ResetAsync_NonexistentSession_Throws()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.ResetAsync(new ResetAgentSessionCommand(NewAgentSessionId: "acp-fresh")));
    }

    [Fact]
    public async Task CompactAsync_ReturnsUpdatedContextWindowMetricsOnResult()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(
            NewAgentSessionId: "acp-after",
            Summary: "## summary"));

        Assert.True(result.ContextWindowSize.HasValue || result.ContextWindowSize is null);
        Assert.True(result.ContextWindowUsed.HasValue || result.ContextWindowUsed is null);
        Assert.True(result.ContextUsagePercent.HasValue || result.ContextUsagePercent is null);
    }

    [Fact]
    public async Task ResetAsync_ReturnsUpdatedContextWindowMetricsOnResult()
    {
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(Guid.NewGuid().ToString("N"));
        await grain.OpenAsync(new OpenAgentSessionCommand(_runnerId, "opencode", WorkDir: "/work"));

        var result = await grain.ResetAsync(new ResetAgentSessionCommand(NewAgentSessionId: "acp-reset-new"));

        Assert.True(result.ContextWindowSize.HasValue || result.ContextWindowSize is null);
        Assert.True(result.ContextWindowUsed.HasValue || result.ContextWindowUsed is null);
        Assert.True(result.ContextUsagePercent.HasValue || result.ContextUsagePercent is null);
    }
}
