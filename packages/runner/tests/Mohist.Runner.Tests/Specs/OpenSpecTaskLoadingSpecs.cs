using System.Text.Json;
using Mohist.Runner.Actions;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class OpenSpecTaskLoadingSpecs
{
    [Fact]
    public async Task ValidTasksJson_LoadsTasks()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "tasks.json"), """
        { "tasks": [{ "id": "T-001", "title": "Implement feature" }] }
        """);

        var result = await new OpenSpecTasksAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "load", "mohist/openspec-tasks", new
        {
            path = "tasks.json",
            task = new { uses = "mohist/coder-agent" }
        }));

        Assert.Equal("loaded", result.Status);
        using var document = JsonDocument.Parse(result.Output!);
        var task = document.RootElement.GetProperty("tasks")[0];
        Assert.Equal("T-001", task.GetProperty("id").GetString());
        Assert.Equal("mohist/coder-agent", task.GetProperty("uses").GetString());
    }

    [Fact]
    public async Task TaskLevelSchema_BecomesAgentInputContract()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "tasks.json"), """
        {
          "tasks": [
            {
              "id": "T-001",
              "title": "Implement feature",
              "description": "Add the feature flag service.",
              "acceptanceCriteria": ["service is registered", "tests pass"],
              "dependsOn": ["T-000"],
              "priority": "p1",
              "mode": "hitl",
              "type": "code",
              "output": { "files": ["src/FeatureFlags.cs"] },
              "uses": "custom/agent",
              "with": { "task": "override", "custom": "value" },
              "requireFiles": [{ "path": "src/FeatureFlags.cs" }],
              "requireMarkers": [{ "path": "openspec/changes/issue-1/tasks.json", "marker": "\"passes\": true" }]
            }
          ]
        }
        """);

        var result = await new OpenSpecTasksAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "load", "mohist/openspec-tasks", new
        {
            path = "tasks.json",
            task = new
            {
                uses = "mohist/coder-agent",
                with = new { stage = "build", task = "default" }
            }
        }));

        Assert.Equal("loaded", result.Status);
        using var document = JsonDocument.Parse(result.Output!);
        var task = document.RootElement.GetProperty("tasks")[0];
        Assert.Equal("custom/agent", task.GetProperty("uses").GetString());

        var with = task.GetProperty("with");
        Assert.Equal("build", with.GetProperty("stage").GetString());
        Assert.Equal("override", with.GetProperty("task").GetString());
        Assert.Equal("value", with.GetProperty("custom").GetString());
        Assert.Equal("Add the feature flag service.", with.GetProperty("description").GetString());
        Assert.Equal("tests pass", with.GetProperty("acceptanceCriteria")[1].GetString());
        Assert.Equal("T-000", with.GetProperty("dependsOn")[0].GetString());
        Assert.Equal("p1", with.GetProperty("priority").GetString());
        Assert.Equal("hitl", with.GetProperty("mode").GetString());
        Assert.Equal("code", with.GetProperty("type").GetString());
        Assert.Equal("src/FeatureFlags.cs", with.GetProperty("output").GetProperty("files")[0].GetString());
        Assert.Equal("src/FeatureFlags.cs", with.GetProperty("requireFiles")[0].GetProperty("path").GetString());
        Assert.Equal("\"passes\": true", with.GetProperty("requireMarkers")[0].GetProperty("marker").GetString());
    }

    [Fact]
    public async Task MissingTasksFile_Fails()
    {
        using var temp = new TempDir();

        var result = await new OpenSpecTasksAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "load", "mohist/openspec-tasks", new { path = "tasks.json" }));

        Assert.Equal("failure", result.Status);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task MissingTasksArray_Fails()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "tasks.json"), "{}");

        var result = await new OpenSpecTasksAction().ExecuteAsync(SpecHelpers.Context(temp.Path, "load", "mohist/openspec-tasks", new { path = "tasks.json" }));

        Assert.Equal("failure", result.Status);
        Assert.Contains("tasks array", result.Message);
    }
}
