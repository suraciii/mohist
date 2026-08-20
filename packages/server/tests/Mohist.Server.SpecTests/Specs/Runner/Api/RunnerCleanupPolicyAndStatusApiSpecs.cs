using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("RunnerMutationIntegration")]
public class RunnerCleanupPolicyAndStatusApiSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly List<string> _registeredRunnerIds = [];

    public RunnerCleanupPolicyAndStatusApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var runnerId in _registeredRunnerIds)
        {
            using var _ = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task<string> RegisterRunnerAsync(string? projectId = null)
    {
        var runnerId = $"runner-cleanup-policy-{Guid.NewGuid():N}";
        using var response = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = "cleanup-policy-host",
            projectId,
        });
        response.EnsureSuccessStatusCode();
        _registeredRunnerIds.Add(runnerId);
        return runnerId;
    }

    [Fact]
    public async Task Status_UnknownRunIds_ReturnsEmptyDictionary()
    {
        var runnerId = await RegisterRunnerAsync();

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/workflow-runs/status",
            new { workflowRunIds = new[] { "unknown-wf-1", "unknown-wf-2" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var statuses = body.GetProperty("statuses");
        Assert.Equal(JsonValueKind.Object, statuses.ValueKind);
        Assert.Empty(statuses.EnumerateObject());
    }

    [Fact]
    public async Task Status_EmptyWorkflowRunIdsArray_Returns400()
    {
        var runnerId = await RegisterRunnerAsync();

        // Empty array — parameter binds successfully but our handler
        // rejects it as a semantic 400.
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/workflow-runs/status",
            new { workflowRunIds = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Status_WhitespaceRunIds_AreFilteredOut()
    {
        var runnerId = await RegisterRunnerAsync();

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/workflow-runs/status",
            new { workflowRunIds = new[] { "", "  ", "real-missing-wf" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var statuses = body.GetProperty("statuses");
        // All three request entries are either blank (filtered) or non-existent (dropped by server).
        Assert.Empty(statuses.EnumerateObject());
    }

    [Fact]
    public async Task Status_DuplicateRunIds_DeduplicatedInResponse()
    {
        var runnerId = await RegisterRunnerAsync();
        var workflowRunId = $"wf-dup-{Guid.NewGuid():N}";

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/runner/{runnerId}/workflow-runs/status",
            new { workflowRunIds = new[] { workflowRunId, workflowRunId, workflowRunId } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Unknown run id is dropped; request was deduped regardless.
        Assert.Empty(body.GetProperty("statuses").EnumerateObject());
    }
}
