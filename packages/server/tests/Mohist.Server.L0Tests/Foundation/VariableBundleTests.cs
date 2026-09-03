using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.L0Tests.Foundation;

[Trait("level", "L0")]
public class VariableBundleTests
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
    public void Patch_NullBase_MergesOverlayIntoEmptyBundle()
    {
        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { a = 1 })));

        var result = VariableBundle.Patch(null, overlay);

        Assert.NotSame(overlay, result);
        Assert.NotNull(result.Vars);
        Assert.Equal(1, result.Vars.Value.GetProperty("a").GetInt32());
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
    public void Patch_VarsDeepMerge_NullOverlayPropertiesRemoveBaseKeys()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "minimax-coding-plan/MiniMax-M3",
                "variant": "max"
              }
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "model": null,
                "variant": null,
                "probeTimeoutMs": 120000
              }
            }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.False(agent.TryGetProperty("model", out _));
        Assert.False(agent.TryGetProperty("variant", out _));
        Assert.Equal(120000, agent.GetProperty("probeTimeoutMs").GetInt32());
    }

    [Fact]
    public void Patch_TopLevelNullOverlay_RemovesKeyFromMergedVars()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "foo": "old",
              "keep": "yes"
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            { "foo": null }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("foo", out _));
        Assert.Equal("yes", root.GetProperty("keep").GetString());
    }

    [Fact]
    public void Patch_OmittedOverlayKey_PreservesBaseValue()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "foo": "kept",
              "bar": 1
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            { "other": "added" }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;

        Assert.Equal("kept", root.GetProperty("foo").GetString());
        Assert.Equal(1, root.GetProperty("bar").GetInt32());
        Assert.Equal("added", root.GetProperty("other").GetString());
    }

    [Fact]
    public void Patch_NullOverlay_OnPreviouslyAbsentKey_IsNoOp()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "present": "yes"
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            { "absent": null }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("absent", out _));
        Assert.Equal("yes", root.GetProperty("present").GetString());
    }

    [Fact]
    public void Patch_EmptyBase_NullTopLevelOverlayKey_IsNoOp()
    {
        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            { "foo": null }
            """));

        var result = VariableBundle.Patch(VariableBundle.Empty, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.False(doc.RootElement.TryGetProperty("foo", out _));
    }

    [Fact]
    public void Patch_EmptyBase_NullNestedOverlayKey_IsNoOp()
    {
        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            { "agent": { "model": null } }
            """));

        var result = VariableBundle.Patch(VariableBundle.Empty, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.False(agent.TryGetProperty("model", out _));
    }

    [Fact]
    public void Patch_EmptyBase_NullStageOverlayKey_IsNoOp()
    {
        var overlay = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                { "baz": null }
                """))
            });

        var result = VariableBundle.Patch(VariableBundle.Empty, overlay);

        Assert.NotNull(result.Stages);
        var planVars = result.Stages!["plan"].Vars;
        Assert.NotNull(planVars);
        using var doc = JsonDocument.Parse(planVars.Value.GetRawText());
        Assert.False(doc.RootElement.TryGetProperty("baz", out _));
    }

    [Fact]
    public void Patch_StageVarsNullOverlay_RemovesKeyFromThatStage()
    {
        var @base = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                {
                  "baz": "old",
                  "keep": "yes"
                }
                """)),
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>("""
                { "untouched": true }
                """))
            });

        var overlay = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                { "baz": null }
                """))
            });

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Stages);
        var planVars = result.Stages!["plan"].Vars;
        Assert.NotNull(planVars);
        using var planDoc = JsonDocument.Parse(planVars!.Value.GetRawText());
        Assert.False(planDoc.RootElement.TryGetProperty("baz", out _));
        Assert.Equal("yes", planDoc.RootElement.GetProperty("keep").GetString());

        var buildVars = result.Stages!["build"].Vars;
        Assert.NotNull(buildVars);
        using var buildDoc = JsonDocument.Parse(buildVars!.Value.GetRawText());
        Assert.True(buildDoc.RootElement.GetProperty("untouched").GetBoolean());
    }

    [Fact]
    public void Patch_PresentOverlayValues_StillOverrideBase()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "foo": "old",
              "agent": { "model": "old-model", "type": "opencode" }
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "foo": "new",
              "agent": { "model": "new-model", "extra": 1 }
            }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;

        Assert.Equal("new", root.GetProperty("foo").GetString());
        var agent = root.GetProperty("agent");
        Assert.Equal("new-model", agent.GetProperty("model").GetString());
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal(1, agent.GetProperty("extra").GetInt32());
    }

    [Fact]
    public void ResolveStageVars_AfterNullClearOnStage_FallsBackToTopLevelWhenPresent()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>("""
            {
              "shared": "top",
              "agent": {
                "model": "minimax-coding-plan/MiniMax-M3",
                "type": "opencode"
              }
            }
            """),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                {
                  "shared": "plan",
                  "agent": {
                    "model": null,
                    "probeTimeoutMs": 120000
                  }
                }
                """))
            });

        var cleared = VariableBundle.Patch(bundle, new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                { "shared": null }
                """))
            }));

        var result = cleared.ResolveStageVars("plan");

        Assert.NotNull(result);
        Assert.Equal("top", result!.Value.GetProperty("shared").GetString());
        var agent = result.Value.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.False(agent.TryGetProperty("model", out _));
        Assert.Equal(120000, agent.GetProperty("probeTimeoutMs").GetInt32());
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
    public void ResolveStageVars_NullStageAgentModelRemovesKeyFromMergedResult()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "minimax-coding-plan/MiniMax-M3",
                "livenessQuietThresholdMs": 1200000
              }
            }
            """),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>("""
                {
                  "agent": {
                    "type": "opencode",
                    "model": null,
                    "probeTimeoutMs": 120000
                  }
                }
                """))
            });

        var result = bundle.ResolveStageVars("build");

        Assert.NotNull(result);
        var agent = result.Value.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.False(agent.TryGetProperty("model", out _));
        Assert.Equal(1200000, agent.GetProperty("livenessQuietThresholdMs").GetInt32());
        Assert.Equal(120000, agent.GetProperty("probeTimeoutMs").GetInt32());
    }

    [Fact]
    public void GetByKeyPath_ReturnsNestedValue()
    {
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
        {
            agent = new { model = "openai/gpt-5" },
        }));

        var result = VariableBundle.GetByKeyPath(root, "agent.model");

        Assert.Equal(JsonValueKind.String, result.ValueKind);
        Assert.Equal("openai/gpt-5", result.GetString());
    }

    [Fact]
    public void GetByKeyPath_MissingKey_ReturnsJsonNull()
    {
        var root = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
        {
            agent = new { model = "openai/gpt-5" },
        }));

        var result = VariableBundle.GetByKeyPath(root, "agent.variant");

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }

    [Fact]
    public void GetByKeyPath_NullValue_ReturnsJsonNull()
    {
        var root = JsonSerializer.Deserialize<JsonElement>("""{ "agent": { "model": null } }""");

        var result = VariableBundle.GetByKeyPath(root, "agent.model");

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
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
    public void ToJson_DoesNotExposeInternalStageClearBookkeeping()
    {
        var original = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>("""
            {
              "shared": "top",
              "agent": { "type": "opencode" }
            }
            """),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                { "agent": { "probeTimeoutMs": 120000 } }
                """))
            });

        var json = original.ToJson();

        Assert.DoesNotContain("StagesClearedKeys", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stagesClearedKeys", json, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Patch_AgentVariantNullOverlay_RemovesAgentVariantKey()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "openai/gpt-4.1",
                "variant": "high"
              }
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "openai/gpt-4.1",
                "variant": null
              }
            }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-4.1", agent.GetProperty("model").GetString());
        Assert.False(agent.TryGetProperty("variant", out _));
    }

    [Fact]
    public void Patch_AgentModelOverlay_OmittingVariant_PreservesAgentVariant()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "openai/gpt-4.1",
                "variant": "high"
              }
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "openai/gpt-5"
              }
            }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-5", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
    }

    [Fact]
    public void Patch_AgentVariantNullOverlay_OnBaseWithoutVariant_IsNoOp()
    {
        var @base = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "openai/gpt-4.1"
              }
            }
            """));

        var overlay = new VariableBundle(
            JsonSerializer.Deserialize<JsonElement>("""
            {
              "agent": {
                "type": "opencode",
                "model": "openai/gpt-4.1",
                "variant": null
              }
            }
            """));

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-4.1", agent.GetProperty("model").GetString());
        Assert.False(agent.TryGetProperty("variant", out _));
    }

    [Fact]
    public void Patch_AgentVariantNullOverlay_OnStageScopedBase_RemovesOnlyThatStageVariant()
    {
        var @base = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                {
                  "agent": {
                    "type": "opencode",
                    "model": "openai/gpt-4.1",
                    "variant": "max"
                  }
                }
                """)),
                ["check"] = new(JsonSerializer.Deserialize<JsonElement>("""
                {
                  "agent": {
                    "type": "opencode",
                    "model": "openai/gpt-5",
                    "variant": "high"
                  }
                }
                """))
            });

        var overlay = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.Deserialize<JsonElement>("""
                {
                  "agent": {
                    "type": "opencode",
                    "model": "anthropic/claude-sonnet-4-5",
                    "variant": null
                  }
                }
                """))
            });

        var result = VariableBundle.Patch(@base, overlay);

        Assert.NotNull(result.Stages);
        Assert.Equal(2, result.Stages!.Count);

        var planVars = result.Stages!["plan"].Vars;
        Assert.NotNull(planVars);
        using var planDoc = JsonDocument.Parse(planVars!.Value.GetRawText());
        var planAgent = planDoc.RootElement.GetProperty("agent");
        Assert.Equal("anthropic/claude-sonnet-4-5", planAgent.GetProperty("model").GetString());
        Assert.False(planAgent.TryGetProperty("variant", out _));

        var checkVars = result.Stages!["check"].Vars;
        Assert.NotNull(checkVars);
        using var checkDoc = JsonDocument.Parse(checkVars!.Value.GetRawText());
        var checkAgent = checkDoc.RootElement.GetProperty("agent");
        Assert.Equal("high", checkAgent.GetProperty("variant").GetString());
    }
}
