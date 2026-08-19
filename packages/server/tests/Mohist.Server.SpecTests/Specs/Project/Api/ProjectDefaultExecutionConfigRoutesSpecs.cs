using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Specs.Agent.Api;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Api;

/// <summary>
/// Storage contract for the Project default execution configuration
/// (issue-560 T-001): one replace-on-set default, validation at
/// configuration time, and the read surface exposed through the Project
/// read so Web and CLI can branch without a second endpoint.
/// </summary>
public sealed class ProjectDefaultExecutionConfigRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public ProjectDefaultExecutionConfigRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task GetProject_ReportsNoDefaultExecutionConfig_WhenUnset()
    {
        var projectId = await CreateProjectAsync("default-exec-read-unset");

        using var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        Assert.True(data.TryGetProperty("defaultExecutionConfig", out var configured));
        Assert.Equal(JsonValueKind.Null, configured.ValueKind);
    }

    [Fact]
    public async Task PutDefault_StoresAndReportsTheDefault()
    {
        var projectId = await CreateProjectAsync("default-exec-put");

        using var response = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime = "pi", model = "openai/gpt-5.6", variant = "high" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertProjectReportsDefaultAsync(projectId, "pi", "openai/gpt-5.6", "high");
    }

    [Fact]
    public async Task PutDefault_ReplacesThePriorDefault()
    {
        var projectId = await CreateProjectAsync("default-exec-replace");
        await PutDefaultAsync(projectId, "pi", "openai/gpt-5.6", "high");

        await PutDefaultAsync(projectId, "opencode", "anthropic/sonnet-4.6", null);

        await AssertProjectReportsDefaultAsync(projectId, "opencode", "anthropic/sonnet-4.6", null);
    }

    [Fact]
    public async Task PatchDefault_ReplacesThePriorDefault_WithTheSameClosedFieldSet()
    {
        var projectId = await CreateProjectAsync("default-exec-patch");
        await PutDefaultAsync(projectId, "pi", "openai/gpt-5.6", "high");

        using var response = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime = "opencode", model = "anthropic/sonnet-4.6" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertProjectReportsDefaultAsync(projectId, "opencode", "anthropic/sonnet-4.6", null);
    }

    [Theory]
    [InlineData("fast", "openai/gpt-5.6")]
    [InlineData("pi", "gpt")]
    public async Task PutDefault_WithInvalidDefault_IsRejectedAndLeavesThePriorDefaultUntouched(
        string runtime,
        string model)
    {
        var projectId = await CreateProjectAsync("default-exec-invalid");
        await PutDefaultAsync(projectId, "pi", "openai/gpt-5.6", "high");

        using var response = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime, model });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_default_execution_config", body.GetProperty("code").GetString());

        await AssertProjectReportsDefaultAsync(projectId, "pi", "openai/gpt-5.6", "high");
    }

    [Fact]
    public async Task PutDefault_WithoutModel_IsRejected()
    {
        var projectId = await CreateProjectAsync("default-exec-missing-model");

        using var response = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime = "pi" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProjectReportsDefaultAsync(projectId, null, null, null);
    }

    [Fact]
    public async Task PutDefault_WithUndeclaredField_IsRejectedBeforeAnyState()
    {
        var projectId = await CreateProjectAsync("default-exec-closed-set");

        using var response = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            new { runtime = "pi", model = "openai/gpt-5.6", instructions = "not accepted" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unsupported_field", body.GetProperty("code").GetString());
        Assert.Contains("instructions", body.GetProperty("error").GetString());

        await AssertProjectReportsDefaultAsync(projectId, null, null, null);
    }

    private async Task PutDefaultAsync(
        string projectId,
        string runtime,
        string model,
        string? variant)
    {
        using var response = await _fixture.Client.PutAsJsonAsync(
            $"/api/projects/{projectId}/default-execution-config",
            (object)(variant is null
                ? new { runtime, model }
                : new { runtime, model, variant }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AssertProjectReportsDefaultAsync(
        string projectId,
        string? runtime,
        string? model,
        string? variant)
    {
        using var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        if (runtime is null)
        {
            Assert.True(
                !data.TryGetProperty("defaultExecutionConfig", out var unset)
                    || unset.ValueKind == JsonValueKind.Null,
                "the project reports no defaultExecutionConfig");
            return;
        }

        var configured = data.GetProperty("defaultExecutionConfig");
        Assert.Equal(runtime, configured.GetProperty("runtime").GetString());
        Assert.Equal(model, configured.GetProperty("model").GetString());
        if (variant is null)
        {
            Assert.False(
                configured.TryGetProperty("variant", out var variantElement)
                    && variantElement.ValueKind != JsonValueKind.Null,
                "the stored default carries no variant");
        }
        else
        {
            Assert.Equal(variant, configured.GetProperty("variant").GetString());
        }
    }
}
