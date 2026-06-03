using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class VariableBundleSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = VariableBundle.JsonOptions;

    [Fact]
    public void Empty_HasNullVarsAndStages()
    {
        Assert.Null(VariableBundle.Empty.Vars);
        Assert.Null(VariableBundle.Empty.Stages);
    }

    [Fact]
    public void Set_ReturnsInputBundle()
    {
        var original = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { agent = new { model = "gpt-4o" } })));
        Assert.Same(original, VariableBundle.Set(original));
    }

    [Fact]
    public void Patch_NullBase_ReturnsOverlay()
    {
        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { a = 1 })));

        var result = VariableBundle.Patch(null, overlay);

        Assert.Same(overlay, result);
    }

    [Fact]
    public void Patch_NullOverlay_ReturnsBase()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { a = 1 })));

        var result = VariableBundle.Patch(@base, null);

        Assert.Same(@base, result);
    }

    [Fact]
    public void Patch_VarsDeepMerge_OverlayOverridesBase()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new
                {
                    type = "opencode",
                    model = "sonnet-4",
                    timeout = 300
                }
            })));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "gpt-4o" }
            })));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal(300, agent.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public void Patch_StagesMerge_PerStageDeepMerge()
    {
        var @base = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new { agent = new { model = "sonnet-4" }, timeout = 300 })))
            });

        var overlay = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new { agent = new { model = "gpt-4o" } }))),
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new { flag = true })))
            });

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Stages);
        Assert.Equal(2, result.Stages.Count);

        // plan stage: merged
        var planVars = result.Stages["plan"].Vars;
        Assert.NotNull(planVars);
        using var planDoc = JsonDocument.Parse(planVars.Value.GetRawText());
        Assert.Equal("gpt-4o", planDoc.RootElement.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal(300, planDoc.RootElement.GetProperty("timeout").GetInt32());

        // build stage: new from overlay
        var buildVars = result.Stages["build"].Vars;
        Assert.NotNull(buildVars);
        using var buildDoc = JsonDocument.Parse(buildVars.Value.GetRawText());
        Assert.True(buildDoc.RootElement.GetProperty("flag").GetBoolean());
    }

    [Fact]
    public void Patch_StagesCaseInsensitive()
    {
        var @base = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plan"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new { a = 1 })))
            });

        var overlay = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new { b = 2 })))
            });

        var result = VariableBundle.Patch(@base, overlay);

        // plan and Plan should be the same stage
        Assert.NotNull(result.Stages);
        Assert.Single(result.Stages);
        Assert.True(result.Stages.ContainsKey("PLAN"));
    }

    [Fact]
    public void MergeAll_MultipleLayers_LaterOverridesEarlier()
    {
        var layer1 = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                a = 1,
                obj = new { x = 1 }
            })));
        var layer2 = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                a = 2,
                b = 2
            })));
        var layer3 = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                obj = new { y = 3 }
            })));

        var result = VariableBundle.MergeAll(null, layer1, null, layer2, layer3);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("a").GetInt32());
        Assert.Equal(2, root.GetProperty("b").GetInt32());
        Assert.Equal(1, root.GetProperty("obj").GetProperty("x").GetInt32());
        Assert.Equal(3, root.GetProperty("obj").GetProperty("y").GetInt32());
    }

    [Fact]
    public void DeepMerge_NonObjectBase_ReplacedByOverlay()
    {
        var @base = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize("string"));
        var overlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { a = 1 }));

        var result = VariableBundle.DeepMerge(@base, overlay);

        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Object, result.Value.ValueKind);
        Assert.Equal(1, result.Value.GetProperty("a").GetInt32());
    }

    [Fact]
    public void DeepMerge_NonObjectOverlay_ReplacesBase()
    {
        var @base = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { a = 1 }));
        var overlay = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize("string"));

        var result = VariableBundle.DeepMerge(@base, overlay);

        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.String, result.Value.ValueKind);
        Assert.Equal("string", result.Value.GetString());
    }

    [Fact]
    public void FromJson_EmptyString_ReturnsEmpty()
    {
        var result = VariableBundle.FromJson("");

        Assert.Same(VariableBundle.Empty, result);
    }

    [Fact]
    public void FromJson_Null_ReturnsEmpty()
    {
        var result = VariableBundle.FromJson(null);

        Assert.Same(VariableBundle.Empty, result);
    }

    [Fact]
    public void FromJson_MalformedJson_ReturnsEmpty()
    {
        var result = VariableBundle.FromJson("not json at all");

        Assert.Same(VariableBundle.Empty, result);
    }

    [Fact]
    public void RoundTrip_Json_Serialization_Works()
    {
        var original = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { type = "opencode", model = "sonnet-4" }
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new { timeout = 600 })))
            });

        var json = original.ToJson();
        var deserialized = VariableBundle.FromJson(json);

        Assert.NotNull(deserialized.Vars);
        Assert.NotNull(deserialized.Stages);
        Assert.Single(deserialized.Stages);
    }

    [Fact]
    public void Patch_AllNull_ReturnsEmpty_NoThrow()
    {
        var result = VariableBundle.Patch(null, null);

        Assert.Same(VariableBundle.Empty, result);
    }

    [Fact]
    public void StageVariables_Copy_CreatesIndependentClone()
    {
        var vars = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { a = 1 }));
        var original = new StageVariables(vars);

        var copy = original.Copy();

        Assert.NotSame(original, copy);
        Assert.Equal(original.Vars?.GetRawText(), copy.Vars?.GetRawText());
    }

    [Fact]
    public void StageVariables_Empty_WhenNoVars()
    {
        Assert.True(new StageVariables().IsEmpty);
        Assert.False(new StageVariables(JsonSerializer.Deserialize<JsonElement>("{}")).IsEmpty);
    }
}
