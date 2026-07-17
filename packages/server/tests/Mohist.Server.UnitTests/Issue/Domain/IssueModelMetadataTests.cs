using System.Text.Json;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

public class IssueModelMetadataTests
{
    private static IssueModelMetadata.FieldPatch<string> Absent =>
        IssueModelMetadata.FieldPatch<string>.Absent;
    private static IssueModelMetadata.FieldPatch<string> Clear =>
        IssueModelMetadata.FieldPatch<string>.Clear;
    private static IssueModelMetadata.FieldPatch<string> Set(string v) =>
        IssueModelMetadata.FieldPatch<string>.Set(v);

    private static IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>> MapAbsent =>
        IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Absent;
    private static IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>> MapClear =>
        IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Clear;
    private static IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>> MapSet(IReadOnlyDictionary<string, string> v) =>
        IssueModelMetadata.FieldPatch<IReadOnlyDictionary<string, string>>.Set(v);

    [Theory]
    [InlineData("anthropic/claude-opus-4-20250514")]
    [InlineData("openai/gpt-5.5")]
    [InlineData("zhipuai-coding-plan/glm-5.2")]
    [InlineData("a/b")]
    [InlineData("z.ai/foo-bar")]
    [InlineData("openrouter/vendor/family/model")]
    [InlineData("openai/org/team/model-name")]
    public void ValidateModel_AcceptsProviderSlashModelFormat(string model)
    {
        Assert.Null(IssueModelMetadata.ValidateModel(model));
    }

    [Theory]
    [InlineData("model-only")]
    [InlineData("/model")]
    [InlineData("provider/")]
    [InlineData("only-slash-no-name/")]
    [InlineData("/leading-slash")]
    [InlineData("with space/inside")]
    [InlineData("inside/with space")]
    [InlineData("/")]
    [InlineData("provider/ ")]
    public void ValidateModel_RejectsNonProviderSlashModelFormat(string model)
    {
        Assert.NotNull(IssueModelMetadata.ValidateModel(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateModel_AllowsNullOrEmptyAsClearSignal(string? model)
    {
        Assert.Null(IssueModelMetadata.ValidateModel(model));
    }

    [Fact]
    public void ApplyModelMetadata_SeedsAgentConfigOnEmptyBundle()
    {
        var patch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("anthropic/claude-opus-4-20250514"),
            ModelVariant: Set("high"),
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);

        var result = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, patch);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("anthropic/claude-opus-4-20250514", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void ApplyModelMetadata_ClearsVariantAtomicallyWhenModelCleared()
    {
        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("anthropic/claude-opus-4-20250514"),
            ModelVariant: Set("high"),
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var seeded = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, seedPatch);

        var clearPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Clear,
            ModelVariant: Clear,
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var cleared = IssueModelMetadata.ApplyModelMetadata(seeded, clearPatch);

        using var doc = JsonDocument.Parse(cleared.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.False(agent.TryGetProperty("model", out _));
        Assert.False(agent.TryGetProperty("variant", out _));
    }

    [Fact]
    public void ApplyModelMetadata_ClearsVariantOnlyWhenModelIsPresent()
    {
        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("anthropic/claude-opus-4-20250514"),
            ModelVariant: Set("high"),
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var seeded = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, seedPatch);

        // Model is Absent, variant is Clear — should drop the variant (model stays).
        var clearVariantPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Clear,
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var cleared = IssueModelMetadata.ApplyModelMetadata(seeded, clearVariantPatch);

        using var doc = JsonDocument.Parse(cleared.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.True(agent.TryGetProperty("model", out _));
        Assert.False(agent.TryGetProperty("variant", out _));
    }

    [Fact]
    public void ApplyModelMetadata_PreservesExistingAgentKeys()
    {
        var existingJson = """
        {
          "vars": { "agent": { "type": "opencode", "probeTimeoutMs": 30000 } },
          "stages": {}
        }
        """;
        var existing = VariableBundle.FromJson(existingJson);

        var patch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("openai/gpt-5.5"),
            ModelVariant: Set("low"),
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var updated = IssueModelMetadata.ApplyModelMetadata(existing, patch);

        using var doc = JsonDocument.Parse(updated.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal(30000, agent.GetProperty("probeTimeoutMs").GetInt32());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal("low", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void ApplyModelMetadata_ReplacesModelAndClearsStaleVariantOnModelChange()
    {
        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("anthropic/claude-opus-4-20250514"),
            ModelVariant: Set("high"),
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var seeded = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, seedPatch);

        // Switch model without an explicit variant — stale variant for the
        // prior model is cleared atomically (dependency invariant).
        var switchPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("openai/gpt-5.5"),
            ModelVariant: Absent,
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var switched = IssueModelMetadata.ApplyModelMetadata(seeded, switchPatch);

        using var doc = JsonDocument.Parse(switched.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.False(agent.TryGetProperty("variant", out _));
    }

    [Fact]
    public void ApplyModelMetadata_PerStageModelAndVariantRoundTrip()
    {
        var stageModels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "openai/gpt-5.5",
            ["build"] = "anthropic/claude-sonnet-4-20250514",
        };
        var stageVariants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "low",
            ["build"] = "max",
        };

        var patch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Absent,
            StageModels: MapSet(stageModels),
            StageModelVariants: MapSet(stageVariants));
        var result = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, patch);

        Assert.NotNull(result.Stages);
        Assert.Equal(2, result.Stages!.Count);

        using var planDoc = JsonDocument.Parse(result.Stages!["plan"].Vars!.Value.GetRawText());
        Assert.Equal("openai/gpt-5.5", planDoc.RootElement.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("low", planDoc.RootElement.GetProperty("agent").GetProperty("variant").GetString());

        using var buildDoc = JsonDocument.Parse(result.Stages!["build"].Vars!.Value.GetRawText());
        Assert.Equal("anthropic/claude-sonnet-4-20250514", buildDoc.RootElement.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("max", buildDoc.RootElement.GetProperty("agent").GetProperty("variant").GetString());
    }

    [Fact]
    public void ApplyModelMetadata_PerStageClearRemovesBoundVariant()
    {
        var seedStageModels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "openai/gpt-5.5",
        };
        var seedStageVariants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "low",
        };

        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Absent,
            StageModels: MapSet(seedStageModels),
            StageModelVariants: MapSet(seedStageVariants));
        var seeded = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, seedPatch);

        // Clear the per-stage model — bound variant follows.
        var clearStageModels = new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "" };
        var clearPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Absent,
            StageModels: MapSet(clearStageModels),
            StageModelVariants: MapAbsent);
        var cleared = IssueModelMetadata.ApplyModelMetadata(seeded, clearPatch);

        Assert.NotNull(cleared.Stages);
        Assert.True(cleared.Stages!.ContainsKey("plan"));
        using var planDoc = JsonDocument.Parse(cleared.Stages!["plan"].Vars!.Value.GetRawText());
        Assert.False(planDoc.RootElement.GetProperty("agent").TryGetProperty("model", out _));
        Assert.False(planDoc.RootElement.GetProperty("agent").TryGetProperty("variant", out _));
    }

    [Fact]
    public void ApplyModelMetadata_PreservesExistingPerStageAgentKeys()
    {
        var existingJson = """
        {
          "vars": {},
          "stages": {
            "plan": { "vars": { "agent": { "type": "opencode", "probeTimeoutMs": 5000 } } }
          }
        }
        """;
        var existing = VariableBundle.FromJson(existingJson);

        var stageModels = new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "openai/gpt-5.5" };
        var stageVariants = new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "low" };

        var patch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Absent,
            StageModels: MapSet(stageModels),
            StageModelVariants: MapSet(stageVariants));
        var updated = IssueModelMetadata.ApplyModelMetadata(existing, patch);

        Assert.NotNull(updated.Stages);
        using var planDoc = JsonDocument.Parse(updated.Stages!["plan"].Vars!.Value.GetRawText());
        var agent = planDoc.RootElement.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal(5000, agent.GetProperty("probeTimeoutMs").GetInt32());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal("low", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void ApplyModelMetadata_AbsentPatchIsNoop()
    {
        var existingJson = """
        {
          "vars": { "agent": { "model": "openai/gpt-5.5", "variant": "high" } },
          "stages": {}
        }
        """;
        var existing = VariableBundle.FromJson(existingJson);

        // All four fields Absent — should be a no-op.
        var patch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Absent,
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var result = IssueModelMetadata.ApplyModelMetadata(existing, patch);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void ApplyModelMetadata_ClearAllStageModelsClearsEveryStage()
    {
        var seedStageModels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "openai/gpt-5.5",
            ["build"] = "anthropic/claude-sonnet-4-20250514",
        };
        var seedStageVariants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plan"] = "low",
            ["build"] = "max",
        };
        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Absent,
            StageModels: MapSet(seedStageModels),
            StageModelVariants: MapSet(seedStageVariants));
        var seeded = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, seedPatch);

        // Send {"stageModels": null} (whole map clear) — every stage's
        // model + bound variant must be cleared.
        var clearAll = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Absent,
            StageModels: MapClear,
            StageModelVariants: MapAbsent);
        var cleared = IssueModelMetadata.ApplyModelMetadata(seeded, clearAll);

        Assert.NotNull(cleared.Stages);
        Assert.Equal(2, cleared.Stages!.Count);
        using var planDoc = JsonDocument.Parse(cleared.Stages!["plan"].Vars!.Value.GetRawText());
        Assert.False(planDoc.RootElement.GetProperty("agent").TryGetProperty("model", out _));
        Assert.False(planDoc.RootElement.GetProperty("agent").TryGetProperty("variant", out _));
        using var buildDoc = JsonDocument.Parse(cleared.Stages!["build"].Vars!.Value.GetRawText());
        Assert.False(buildDoc.RootElement.GetProperty("agent").TryGetProperty("model", out _));
        Assert.False(buildDoc.RootElement.GetProperty("agent").TryGetProperty("variant", out _));
    }

    [Fact]
    public void ApplyModelMetadata_VariantOnlySetAttachesToExistingModel()
    {
        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("openai/gpt-5.5"),
            ModelVariant: Absent,
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var seeded = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, seedPatch);

        var variantPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Absent,
            ModelVariant: Set("high"),
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var updated = IssueModelMetadata.ApplyModelMetadata(seeded, variantPatch);

        using var doc = JsonDocument.Parse(updated.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void ApplyModelMetadata_SameModelReSuppliedPreservesVariant()
    {
        var seedPatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("openai/gpt-5.5"),
            ModelVariant: Set("high"),
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var seeded = IssueModelMetadata.ApplyModelMetadata(VariableBundle.Empty, seedPatch);

        // Re-supply the same model with no variant patch — preserve.
        var rePatch = new IssueModelMetadata.ModelMetadataPatch(
            Model: Set("openai/gpt-5.5"),
            ModelVariant: Absent,
            StageModels: MapAbsent,
            StageModelVariants: MapAbsent);
        var result = IssueModelMetadata.ApplyModelMetadata(seeded, rePatch);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void Validate_RejectsInvalidModelFormat()
    {
        var error = IssueModelMetadata.Validate(
            model: "not-a-model",
            stageModels: null);

        Assert.NotNull(error);
        Assert.Contains("provider/model", error);
    }

    [Fact]
    public void Validate_RejectsInvalidPerStageModelFormat()
    {
        var error = IssueModelMetadata.Validate(
            model: null,
            stageModels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plan"] = "badmodel",
            });

        Assert.NotNull(error);
        Assert.Contains("stageModels.plan", error);
    }

    [Fact]
    public void Validate_AcceptsValidModelAndStageModels()
    {
        var error = IssueModelMetadata.Validate(
            model: "anthropic/claude-opus-4-20250514",
            stageModels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plan"] = "openai/gpt-5.5",
            });

        Assert.Null(error);
    }

    [Fact]
    public void Validate_AcceptsMultiSlashModelInTopLevelAndStageSelectors()
    {
        var error = IssueModelMetadata.Validate(
            model: "openrouter/vendor/family/model",
            stageModels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plan"] = "openrouter/vendor/family/model",
                ["build"] = "anthropic/claude-opus-4-20250514",
            });

        Assert.Null(error);
    }

    [Fact]
    public void Validate_RejectsInvalidMultiSlashStageModelFormat()
    {
        var error = IssueModelMetadata.Validate(
            model: null,
            stageModels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["build"] = "openrouter/",
            });

        Assert.NotNull(error);
        Assert.Contains("stageModels.build", error);
        Assert.Contains("provider/model", error);
    }
}
