using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public class WorkflowTaskInputExpansionTests
{
    [Fact]
    public void ExpandTaskWith_NullTaskWith_ReturnsNull()
    {
        var result = WorkflowProfileManager.ExpandTaskWith(VariableBundle.Empty, null);

        Assert.Null(result);
    }

    [Fact]
    public void ExpandTaskWith_ResolvesWholeTemplateStringToJsonValue()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { type = "opencode", model = "sonnet-4" }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["agent"] = JsonSerializer.SerializeToElement("${{ agent }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Object, result["agent"]!.Value.ValueKind);
        Assert.Equal("opencode", result["agent"]!.Value.GetProperty("type").GetString());
        Assert.Equal("sonnet-4", result["agent"]!.Value.GetProperty("model").GetString());
    }

    [Fact]
    public void ExpandTaskWith_PlainObjectValueStaysAsIs()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "gpt-4o", timeoutMs = 300000 }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["agent"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { type = "opencode", timeoutMs = 600000 })),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result["agent"]));
        Assert.Equal("opencode", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(600000, doc.RootElement.GetProperty("timeoutMs").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("model", out _), "vars must not deep-merge into plain with values");
    }

    [Fact]
    public void ExpandTaskWith_PreservesPlainValues()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { other = 1 })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["count"] = JsonSerializer.SerializeToElement(42),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("task-1", result!["name"]!.Value.GetString());
        Assert.Equal(42, result!["count"]!.Value.GetInt32());
    }

    [Fact]
    public void ExpandTaskWith_ResolvesNestedWholeTemplatePath()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                config = new { deep = new { value = "found-it" } }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["x"] = JsonSerializer.SerializeToElement("${{ config.deep.value }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("found-it", result!["x"]!.Value.GetString());
    }

    [Fact]
    public void ExpandTaskWith_ResolvesVarsPrefixedNestedWholeTemplatePath()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                github = new { pr = new { number = 42, url = "https://github.com/example/repo/pull/42" } }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["prNumber"] = JsonSerializer.SerializeToElement("${{ vars.github.pr.number }}"),
            ["prUrl"] = JsonSerializer.SerializeToElement("${{ vars.github.pr.url }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal(42, result!["prNumber"]!.Value.GetInt32());
        Assert.Equal("https://github.com/example/repo/pull/42", result!["prUrl"]!.Value.GetString());
    }

    [Fact]
    public void ExpandTaskWith_PreservesUnresolvedWholeTemplateString()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { other = 1 })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["agent"] = JsonSerializer.SerializeToElement("${{ missing.agent }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("${{ missing.agent }}", result!["agent"]!.Value.GetString());
    }

    // =================================================================
    // Narrow API tests — LoadStageSpecsAsync / LoadStructureAsync /
    // LoadApprovalConfigAsync (design D6 — profileManager encapsulates
    // the template selection cascade so the grain never holds a
    // WorkflowDefinition).
    // =================================================================

}
