using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.L0Tests.Agent.Api;

public sealed class AgentSubscriptionRoutePolicyTests
{
    [Fact]
    public void Normalize_TrimsRoutingFieldsBeforePersistenceAndReplayComparison()
    {
        var normalized = AgentSubscriptionRoutes.Normalize(new AgentSubscriptionCreateRequest(
            "  release  ",
            "  event.type == \"release\"  ",
            "  Summarize the release.  ",
            Continue: true));

        Assert.Equal("release", normalized.Name);
        Assert.Equal("event.type == \"release\"", normalized.Match);
        Assert.Equal("Summarize the release.", normalized.ResponsePrompt);
        Assert.True(normalized.Continue);
    }

    [Theory]
    [InlineData(AgentExecutabilityStates.NotConfigured, "unconfigured")]
    [InlineData(AgentExecutabilityStates.NotExecutable, "not_executable")]
    [InlineData(AgentExecutabilityStates.Unknown, "no_connection")]
    public void DeriveState_PreservesExecutabilityBeforeConnectionState(string executability, string expected)
    {
        var state = AgentSubscriptionRoutes.DeriveState(
            Agent(executability),
            [],
            []);

        Assert.Equal(expected, state);
    }

    [Fact]
    public void DeriveState_ReportsUnavailableConnectionWithoutCallingItEmpty()
    {
        var state = AgentSubscriptionRoutes.DeriveState(
            Agent(AgentExecutabilityStates.Unknown),
            [],
            [Connection(ConnectionHealthKind.Unhealthy)]);

        Assert.Equal("unavailable", state);
        Assert.Equal("unavailable", AgentSubscriptionRoutes.DeriveConnectionState(
            [Connection(ConnectionHealthKind.Unhealthy)]));
    }

    [Fact]
    public void DeriveState_ReportsEmptyForHealthyConnectionWithoutSubscriptions()
    {
        var state = AgentSubscriptionRoutes.DeriveState(
            Agent(AgentExecutabilityStates.Unknown),
            [],
            [Connection(ConnectionHealthKind.Healthy)]);

        Assert.Equal("empty", state);
        Assert.Equal("connected", AgentSubscriptionRoutes.DeriveConnectionState(
            [Connection(ConnectionHealthKind.Healthy)]));
    }

    [Fact]
    public void DeriveState_ReportsConfiguredForHealthyConnectionWithSubscription()
    {
        var state = AgentSubscriptionRoutes.DeriveState(
            Agent(AgentExecutabilityStates.Unknown),
            [Subscription()],
            [Connection(ConnectionHealthKind.Healthy)]);

        Assert.Equal("configured", state);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    public void GetBool_MapsOnlyBooleanOrNullValues(string json, bool? expected)
    {
        using var document = JsonDocument.Parse($"{{\"continue\":{json}}}");

        Assert.Equal(expected, AgentSubscriptionUpdateRequest.GetBool(document.RootElement, "continue"));
    }

    [Fact]
    public void GetBool_ReturnsNullWhenFieldIsAbsent()
    {
        using var document = JsonDocument.Parse("{}");

        Assert.Null(AgentSubscriptionUpdateRequest.GetBool(document.RootElement, "continue"));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    public void GetBool_RejectsNonBooleanValues(string json)
    {
        using var document = JsonDocument.Parse($"{{\"continue\":{json}}}");

        var error = Assert.Throws<JsonException>(() =>
            AgentSubscriptionUpdateRequest.GetBool(document.RootElement, "continue"));
        Assert.Contains("boolean or null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateRequestBinder_TracksPresenceAndPreservesBooleanValue()
    {
        var request = await BindAsync("{\"continue\":true}");

        Assert.True(request.Continue);
        Assert.Equal(["continue"], request.Fields);
        Assert.Null(request.Name);
        Assert.Null(request.Match);
        Assert.Null(request.ResponsePrompt);
    }

    [Fact]
    public async Task UpdateRequestBinder_MapsExplicitNullToPresentFalseReset()
    {
        var request = await BindAsync("{\"continue\":null}");

        Assert.Null(request.Continue);
        Assert.Contains("continue", request.Fields);
    }

    private static AgentInfo Agent(string executability) => new(
        "agent-1",
        "project-1",
        "agent",
        "description",
        "instructions",
        null,
        [],
        null,
        "active",
        "2026-01-01T00:00:00Z",
        "2026-01-01T00:00:00Z",
        new AgentExecutabilityResult(executability, [], null));

    private static AgentConnection Connection(string health) => new()
    {
        Id = "connection-1",
        ProjectId = "project-1",
        AgentId = "agent-1",
        SetupProgress = SetupProgressKind.Complete,
        DesiredState = DesiredStateKind.Enabled,
        ConnectionHealth = health,
    };

    private static AgentSubscriptionDto Subscription() => new(
        "rule-1",
        "project-1",
        "agent-1",
        "release",
        "event.type == \"release\"",
        "summarize",
        false,
        1,
        "active",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    private static async Task<AgentSubscriptionUpdateRequest> BindAsync(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await AgentSubscriptionUpdateRequest.BindAsync(context)
            ?? throw new InvalidOperationException("subscription binder returned null");
    }
}
