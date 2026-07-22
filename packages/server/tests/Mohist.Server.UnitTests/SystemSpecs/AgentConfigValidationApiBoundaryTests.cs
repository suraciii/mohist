using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Issue.Services;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Covers the #410 T-002 + issue-452 T-001 API-boundary validation:
/// <see cref="AgentConfigSchema"/> is the single source of truth for
/// which keys an <c>agentConfig</c> body may carry.
/// <see cref="IssueModelMetadata.ValidateAgentConfig"/> is the
/// issue-route-facing helper that delegates to the schema. The
/// acceptance criteria reject <c>type</c>, <c>livenessQuietThresholdMs</c>,
/// <c>probeTimeoutMs</c>, <c>sessionStartTimeoutMs</c>, and
/// <c>compaction</c> with an actionable validation error so legacy
/// ACP/liveness keys never reach persistence; the same gate now also
/// accepts <c>runtime</c> as a backend dimension (issue-452 D1).
/// </summary>
public class AgentConfigValidationApiBoundaryTests
{
    [Theory]
    [InlineData("type")]
    [InlineData("livenessQuietThresholdMs")]
    [InlineData("probeTimeoutMs")]
    [InlineData("sessionStartTimeoutMs")]
    [InlineData("compaction")]
    public void IssueModelMetadata_RejectsForbiddenKey_OnIssueCreate(string forbiddenKey)
    {
        var raw = JsonDocument.Parse($"{{\"{forbiddenKey}\":\"value\"}}").RootElement;

        var error = IssueModelMetadata.ValidateAgentConfig(raw);

        Assert.NotNull(error);
        Assert.Contains($"agentConfig.{forbiddenKey}", error);
        Assert.Contains("not allowed", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime", error);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("livenessQuietThresholdMs")]
    [InlineData("probeTimeoutMs")]
    [InlineData("sessionStartTimeoutMs")]
    [InlineData("compaction")]
    public void IssueModelMetadata_RejectsForbiddenKey_OnIssuePatch(string forbiddenKey)
    {
        // The route layer reads the raw patch body and invokes
        // IssueModelMetadata.ValidateAgentConfig; both create and patch
        // paths share the same gating helper.
        var raw = JsonDocument.Parse($"{{\"agentConfig\":{{\"{forbiddenKey}\":\"value\"}}}}").RootElement;

        var nested = raw.GetProperty("agentConfig");
        var error = IssueModelMetadata.ValidateAgentConfig(nested);

        Assert.NotNull(error);
        Assert.Contains($"agentConfig.{forbiddenKey}", error);
    }

    [Fact]
    public void IssueModelMetadata_AcceptsModelAndVariantOnly()
    {
        var raw = JsonDocument.Parse("""{"model":"openai/gpt-5.6","variant":"high"}""").RootElement;

        Assert.Null(IssueModelMetadata.ValidateAgentConfig(raw));
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void IssueModelMetadata_AcceptsRuntime(string runtime)
    {
        var raw = JsonDocument.Parse($$"""{"model":"openai/gpt-5.6","variant":"high","runtime":"{{runtime}}"}""").RootElement;

        Assert.Null(IssueModelMetadata.ValidateAgentConfig(raw));
    }

    [Fact]
    public void IssueModelMetadata_RejectsUnknownRuntime()
    {
        var raw = JsonDocument.Parse("""{"model":"openai/gpt-5.6","runtime":"mystery"}""").RootElement;

        var error = IssueModelMetadata.ValidateAgentConfig(raw);

        Assert.NotNull(error);
        Assert.Contains("agentConfig.runtime", error);
        Assert.Contains("opencode", error);
        Assert.Contains("pi", error);
    }

    [Fact]
    public void IssueModelMetadata_AcceptsMixedAcceptedAndForbidden_ReportsFirstForbidden()
    {
        var raw = JsonDocument.Parse("""{"model":"openai/gpt-5.6","type":"opencode","variant":"high"}""").RootElement;

        var error = IssueModelMetadata.ValidateAgentConfig(raw);

        Assert.NotNull(error);
        Assert.Contains("agentConfig.type", error);
    }

    [Fact]
    public void AgentConfigSchema_Validate_RejectsUnknownKey()
    {
        // Converged surface is {model, variant, runtime}; anything else is rejected.
        var raw = JsonDocument.Parse("""{"model":"openai/gpt-5.6","temperature":0.5}""").RootElement;

        var error = AgentConfigSchema.Validate(raw);

        Assert.NotNull(error);
        Assert.Contains("temperature", error);
    }

    [Fact]
    public void AgentConfigSchema_Validate_NullOrNonObject_ReturnsNull()
    {
        Assert.Null(AgentConfigSchema.Validate(null));
        Assert.Null(AgentConfigSchema.Validate(JsonDocument.Parse("null").RootElement));
        Assert.Null(AgentConfigSchema.Validate(JsonDocument.Parse("\"foo\"").RootElement));
        Assert.Null(AgentConfigSchema.Validate(JsonDocument.Parse("[1,2,3]").RootElement));
    }
}
