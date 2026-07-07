using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("MohistIntegration")]
public class RunnerHeartbeatConnectionApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerHeartbeatConnectionApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private RunnerConnectionTracker Tracker => _fixture.Services.GetRequiredService<RunnerConnectionTracker>();

    private async Task RegisterRunnerAsync(string runnerId)
    {
        var response = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "heartbeat-convergence-host",
        });
        response.EnsureSuccessStatusCode();
    }

    private async Task PostHeartbeatAsync(string runnerId, object? body)
    {
        HttpContent? content = body is null
            ? null
            : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/heartbeat", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_WithConnectionId_RegistersTrackerEntry()
    {
        var runnerId = $"runner-hb-converge-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);

        try
        {
            await PostHeartbeatAsync(runnerId, new { connectionId = "C-1" });

            Assert.Equal("C-1", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_WithEmptyBody_LeavesExistingTrackerEntryUnchanged()
    {
        var runnerId = $"runner-hb-empty-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);
        Tracker.Register(runnerId, "preset-conn");

        try
        {
            using var emptyBody = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            emptyBody.Headers.ContentLength = 0;
            using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/heartbeat", emptyBody);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("preset-conn", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_WithoutConnectionIdField_LeavesExistingTrackerEntryUnchanged()
    {
        var runnerId = $"runner-hb-no-field-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);
        Tracker.Register(runnerId, "preset-conn");

        try
        {
            await PostHeartbeatAsync(runnerId, new
            {
                capabilities = new[] { "spec/*" },
            });

            Assert.Equal("preset-conn", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_WithExplicitEmptyConnectionId_LeavesExistingTrackerEntryUnchanged()
    {
        var runnerId = $"runner-hb-empty-conn-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);
        Tracker.Register(runnerId, "preset-conn");

        try
        {
            await PostHeartbeatAsync(runnerId, new { connectionId = "" });

            Assert.Equal("preset-conn", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_WithExplicitNullConnectionId_LeavesExistingTrackerEntryUnchanged()
    {
        var runnerId = $"runner-hb-null-conn-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);
        Tracker.Register(runnerId, "preset-conn");

        try
        {
            await PostHeartbeatAsync(runnerId, new { connectionId = (string?)null });

            Assert.Equal("preset-conn", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_WithWhitespaceConnectionId_LeavesExistingTrackerEntryUnchanged()
    {
        var runnerId = $"runner-hb-ws-conn-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);
        Tracker.Register(runnerId, "preset-conn");

        try
        {
            await PostHeartbeatAsync(runnerId, new { connectionId = "   " });

            Assert.Equal("preset-conn", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_AfterDisconnectErasedEntry_RepopulatesTracker()
    {
        var runnerId = $"runner-hb-repopulate-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);
        Tracker.Register(runnerId, "stale-conn");

        // Simulate the SignalR OnDisconnectedAsync path erasing the entry.
        Tracker.Unregister(runnerId);
        Assert.Null(Tracker.GetConnectionId(runnerId));

        try
        {
            await PostHeartbeatAsync(runnerId, new { connectionId = "C-fresh" });

            Assert.Equal("C-fresh", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Heartbeat_SubsequentConnectionIds_OverwriteTrackerEntry()
    {
        var runnerId = $"runner-hb-overwrite-{Guid.NewGuid():N}";
        await RegisterRunnerAsync(runnerId);

        try
        {
            await PostHeartbeatAsync(runnerId, new { connectionId = "C-1" });
            Assert.Equal("C-1", Tracker.GetConnectionId(runnerId));

            await PostHeartbeatAsync(runnerId, new { connectionId = "C-2" });
            Assert.Equal("C-2", Tracker.GetConnectionId(runnerId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}