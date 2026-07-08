using CloudNative.CloudEvents;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Mohist.Server.Infrastructure.Events;

public class ConnectionSubscriptionRegistryProjectIdTests
{
    [Fact]
    public void RegisterConnection_InitialisesProjectIdAsNull()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public void SetProjectId_RoundTripsThroughTryGetProjectId()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        registry.SetProjectId("conn-A", "proj-alpha");

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Equal("proj-alpha", actual);
    }

    [Fact]
    public void SetProjectId_EmptyString_NormalisesToNull()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.SetProjectId("conn-A", "proj-alpha");

        registry.SetProjectId("conn-A", string.Empty);

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public void SetProjectId_Whitespace_NormalisesToNull()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        registry.SetProjectId("conn-A", "   ");

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public void SetProjectId_NullExplicitly_StoresNull()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.SetProjectId("conn-A", "proj-alpha");

        registry.SetProjectId("conn-A", null);

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public void SetProjectId_Reinvoke_ReplacesAffinity()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.SetProjectId("conn-A", "proj-alpha");

        registry.SetProjectId("conn-A", "proj-beta");

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Equal("proj-beta", actual);
    }

    [Fact]
    public void TryGetProjectId_UnregisteredConnection_ReturnsFalse()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        // No connection "conn-B" was ever registered — the
        // dispatcher's gating rule treats this as "no declared
        // project" and falls back to type-only matching.
        Assert.False(registry.TryGetProjectId("conn-B", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public void UnregisterConnection_RemovesProjectId()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.SetProjectId("conn-A", "proj-alpha");

        registry.UnregisterConnection("conn-A");

        Assert.False(registry.TryGetProjectId("conn-A", out _));
        Assert.DoesNotContain("conn-A", registry.ConnectionIds);
    }
}