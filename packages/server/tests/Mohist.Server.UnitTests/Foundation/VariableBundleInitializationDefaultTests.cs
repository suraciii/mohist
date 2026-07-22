using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

/// <summary>
/// issue-474 T-002: Variables resolve only from Project, Issue, and
/// WorkflowRun resources. Marked initialization defaults sit at the
/// bottom of the resource precedence stack and are cleared by an
/// explicit write. These tests cover the VariableBundle mechanics
/// directly so resolution regressions surface without spinning up a
/// full SQLite-backed fixture.
/// </summary>
public class VariableBundleInitializationDefaultTests
{
    [Fact]
    public void ResolveStageVars_MarkedDefaultSitsBelowExplicitValues()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "/issue/archive",
            })),
            DefaultVars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "",
            })));

        var resolved = bundle.ResolveStageVars(null);

        Assert.NotNull(resolved);
        Assert.Equal("/issue/archive", resolved!.Value.GetProperty("archive").GetString());
    }

    [Fact]
    public void ResolveStageVars_MarkedDefaultWinsWhenNothingElseSuppliesKey()
    {
        var bundle = new VariableBundle(
            DefaultVars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "",
            })));

        var resolved = bundle.ResolveStageVars(null);

        Assert.NotNull(resolved);
        Assert.Equal(string.Empty, resolved!.Value.GetProperty("archive").GetString());
    }

    [Fact]
    public void ResolveStageVars_SelectedStageOverlayBeatsDefault()
    {
        var bundle = new VariableBundle(
            DefaultVars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "",
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    archive = "/stage/archive",
                }))),
            });

        var resolved = bundle.ResolveStageVars("build");

        Assert.NotNull(resolved);
        Assert.Equal("/stage/archive", resolved!.Value.GetProperty("archive").GetString());
    }

    [Fact]
    public void ClearDefaultsCoveredByExplicit_RemovesMatchingTopLevelDefaultKeys()
    {
        var explicitBundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "/explicit",
            })));
        var defaultsBundle = new VariableBundle(
            DefaultVars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "",
                unrelated = "kept",
            })));

        var cleared = defaultsBundle.ClearDefaultsCoveredByExplicit(explicitBundle);

        Assert.NotNull(cleared.DefaultVars);
        Assert.False(cleared.DefaultVars!.Value.TryGetProperty("archive", out _));
        Assert.Equal("kept", cleared.DefaultVars!.Value.GetProperty("unrelated").GetString());
    }
}
