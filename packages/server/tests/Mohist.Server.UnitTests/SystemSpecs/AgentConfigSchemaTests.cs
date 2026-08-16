using System.Text.Json;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class AgentConfigSchemaTests
{
    [Fact]
    public void Validate_Null_IsValid_NonObject_IsRejected()
    {
        Assert.Null(AgentConfigSchema.Validate(null));
        Assert.Null(AgentConfigSchema.Validate(JsonDocument.Parse("null").RootElement));
        Assert.Contains("JSON object or null", AgentConfigSchema.Validate(JsonDocument.Parse("\"foo\"").RootElement));
        Assert.Contains("JSON object or null", AgentConfigSchema.Validate(JsonDocument.Parse("[1,2,3]").RootElement));
    }

    [Fact]
    public void Validate_EmptyObject_ReturnsNull()
    {
        Assert.Null(AgentConfigSchema.Validate(JsonDocument.Parse("{}").RootElement));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("livenessQuietThresholdMs")]
    [InlineData("probeTimeoutMs")]
    [InlineData("sessionStartTimeoutMs")]
    [InlineData("compaction")]
    public void Validate_ForbiddenKey_ReturnsActionableError(string key)
    {
        var element = JsonDocument.Parse($"{{\"{key}\": \"value\"}}").RootElement;
        var error = AgentConfigSchema.Validate(element);

        Assert.NotNull(error);
        Assert.Contains($"agentConfig.{key}", error);
    }

    [Fact]
    public void Validate_ModelAndVariantAccepted()
    {
        var element = JsonDocument.Parse("""{"model":"openai/gpt-5.5","variant":"high"}""").RootElement;
        Assert.Null(AgentConfigSchema.Validate(element));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void Validate_CanonicalReasoningEffortAccepted(string effort)
    {
        var element = JsonDocument.Parse($$"""{"model":"openai/gpt-5.5","reasoningEffort":"{{effort}}"}""").RootElement;
        Assert.Null(AgentConfigSchema.Validate(element));
    }

    [Theory]
    [InlineData("highest")]
    [InlineData(" High")]
    public void Validate_NonCanonicalReasoningEffortIsRejected(string effort)
    {
        var element = JsonDocument.Parse($$"""{"reasoningEffort":"{{effort}}"}""").RootElement;
        var error = AgentConfigSchema.Validate(element);

        Assert.NotNull(error);
        Assert.Contains("agentConfig.reasoningEffort", error);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void Validate_RuntimeAccepted(string runtime)
    {
        var element = JsonDocument.Parse($$"""{"model":"openai/gpt-5.5","runtime":"{{runtime}}"}""").RootElement;
        Assert.Null(AgentConfigSchema.Validate(element));
    }

    [Fact]
    public void Validate_RuntimeAbsent_IsValid()
    {
        var element = JsonDocument.Parse("""{"model":"openai/gpt-5.5"}""").RootElement;
        Assert.Null(AgentConfigSchema.Validate(element));
    }

    [Fact]
    public void Validate_RuntimeUnknown_ReturnsActionableError()
    {
        var element = JsonDocument.Parse("""{"runtime":"unknown"}""").RootElement;
        var error = AgentConfigSchema.Validate(element);
        Assert.NotNull(error);
        Assert.Contains("agentConfig.runtime", error);
        Assert.Contains("opencode", error);
        Assert.Contains("pi", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_RuntimeBlank_ReturnsActionableError(string runtime)
    {
        var element = JsonDocument.Parse($$"""{"runtime":"{{runtime}}"}""").RootElement;
        var error = AgentConfigSchema.Validate(element);

        Assert.NotNull(error);
        Assert.Contains("agentConfig.runtime", error);
    }

    [Fact]
    public void Validate_RuntimeNotString_ReturnsActionableError()
    {
        var element = JsonDocument.Parse("""{"runtime":42}""").RootElement;
        var error = AgentConfigSchema.Validate(element);
        Assert.NotNull(error);
        Assert.Contains("agentConfig.runtime", error);
    }

    [Fact]
    public void Validate_ModelNotString_ReturnsActionableError()
    {
        var element = JsonDocument.Parse("""{"model":42}""").RootElement;
        var error = AgentConfigSchema.Validate(element);

        Assert.Contains("agentConfig.model", error);
        Assert.Contains("string", error);
    }

    [Fact]
    public void Validate_ModelNull_IsValid()
    {
        Assert.Null(AgentConfigSchema.Validate(JsonDocument.Parse("""{"model":null}""").RootElement));
    }

    [Fact]
    public void Validate_RuntimeNull_IsValid()
    {
        var element = JsonDocument.Parse("""{"runtime":null}""").RootElement;
        Assert.Null(AgentConfigSchema.Validate(element));
    }

    [Fact]
    public void Validate_MixedAcceptedAndForbidden_ReportsFirstForbidden()
    {
        var element = JsonDocument.Parse("""{"model":"m","type":"opencode"}""").RootElement;
        var error = AgentConfigSchema.Validate(element);
        Assert.NotNull(error);
        Assert.Contains("agentConfig.type", error);
    }

    [Fact]
    public void Project_StripsEverythingExceptExecutionConfiguration()
    {
        var element = JsonDocument.Parse("""
            {"type":"opencode","model":"openai/gpt-5.5","reasoningEffort":"high","variant":"balanced","livenessQuietThresholdMs":1200000,"probeTimeoutMs":30000}
            """).RootElement;
        var projected = AgentConfigSchema.Project(element);
        Assert.NotNull(projected);
        Assert.Equal(3, projected!.Count);
        Assert.Equal("openai/gpt-5.5", projected["model"]?.ToString());
        Assert.Equal("high", projected["reasoningEffort"]?.ToString());
        Assert.Equal("balanced", projected["variant"]?.ToString());
    }

    [Fact]
    public void Project_KeepsRuntimeAlongsideModelAndVariant()
    {
        var element = JsonDocument.Parse("""
            {"model":"openai/gpt-5.5","variant":"high","runtime":"pi"}
            """).RootElement;
        var projected = AgentConfigSchema.Project(element);
        Assert.NotNull(projected);
        Assert.Equal(3, projected!.Count);
        Assert.Equal("openai/gpt-5.5", projected["model"]?.ToString());
        Assert.Equal("high", projected["variant"]?.ToString());
        Assert.Equal("pi", projected["runtime"]?.ToString());
    }

    [Fact]
    public void Project_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(AgentConfigSchema.Project(null));
        Assert.Null(AgentConfigSchema.Project(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void Project_OnlyAllowedKeys_ReturnsSubset()
    {
        var element = JsonDocument.Parse("""{"model":"openai/gpt-5.5","temperature":0.5}""").RootElement;
        var projected = AgentConfigSchema.Project(element);
        Assert.NotNull(projected);
        Assert.Single(projected!);
        Assert.Equal("openai/gpt-5.5", projected["model"]?.ToString());
    }

    [Fact]
    public void Filter_Dictionary_StripsLegacyKeys()
    {
        var input = new Dictionary<string, object?>
        {
            ["model"] = "openai/gpt-5.5",
            ["type"] = "opencode",
            ["livenessQuietThresholdMs"] = 1200000,
        };
        var filtered = AgentConfigSchema.Filter(input);
        Assert.NotNull(filtered);
        Assert.Single(filtered!);
        Assert.Equal("openai/gpt-5.5", filtered["model"]?.ToString());
    }

    [Fact]
    public void Filter_Dictionary_AllLegacyKeys_ReturnsNull()
    {
        var input = new Dictionary<string, object?>
        {
            ["type"] = "opencode",
            ["livenessQuietThresholdMs"] = 1200000,
        };
        var filtered = AgentConfigSchema.Filter(input);
        Assert.Null(filtered);
    }

    [Fact]
    public void Filter_Dictionary_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(AgentConfigSchema.Filter(null));
        Assert.Null(AgentConfigSchema.Filter(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Filter_Dictionary_DropsRuntimeAlongsideOtherIssueOnlyKeys()
    {
        var input = new Dictionary<string, object?>
        {
            ["model"] = "openai/gpt-5.5",
            ["variant"] = "high",
            ["runtime"] = "pi",
            ["type"] = "opencode",
        };
        var filtered = AgentConfigSchema.Filter(input);
        Assert.NotNull(filtered);
        Assert.Equal(2, filtered!.Count);
        Assert.Equal("openai/gpt-5.5", filtered["model"]?.ToString());
        Assert.Equal("high", filtered["variant"]?.ToString());
        Assert.DoesNotContain("runtime", filtered.Keys);
    }

    [Fact]
    public void Filter_Dictionary_RuntimeOnlyIsDropped()
    {
        var input = new Dictionary<string, object?>
        {
            ["runtime"] = "pi",
            ["type"] = "opencode",
        };
        var filtered = AgentConfigSchema.Filter(input);
        Assert.Null(filtered);
    }

    [Fact]
    public void ValidateIssue_ReasoningEffortIsAllowed()
    {
        var error = AgentConfigSchema.ValidateIssue(JsonDocument.Parse("""{"reasoningEffort":"high"}""").RootElement);

        Assert.Null(error);
    }
}
