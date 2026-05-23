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
            task = new { uses = "mohist/agent" }
        }));

        Assert.Equal("loaded", result.Status);
        using var document = JsonDocument.Parse(result.Output!);
        var task = document.RootElement.GetProperty("tasks")[0];
        Assert.Equal("T-001", task.GetProperty("id").GetString());
        Assert.Equal("mohist/agent", task.GetProperty("uses").GetString());
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
