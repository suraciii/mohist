using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class TaskRequiredFilesSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractRequiredFiles_WithExpectFiles_ReturnsRequiredFileEntries()
    {
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["expect"] = JsonSerializer.Deserialize<JsonElement>("""
                {"files": [{"path": "proposal.md", "markers": ["<promise>PASS</promise>"]}]}
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        Assert.Single(result);
        Assert.Equal("proposal.md", result[0].Path);
        Assert.Equal("task-expect", result[0].Source);
        Assert.True(result[0].CanFetchContent);
        Assert.Contains("<promise>PASS</promise>", result[0].Markers!);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractRequiredFiles_WithMultipleFiles_ReturnsAllEntries()
    {
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["expect"] = JsonSerializer.Deserialize<JsonElement>("""
                {"files": [
                    {"path": "proposal.md"},
                    {"path": "design.md"},
                    {"path": "tasks.json"}
                ]}
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        Assert.Equal(3, result.Count);
        Assert.Equal("proposal.md", result[0].Path);
        Assert.Equal("design.md", result[1].Path);
        Assert.Equal("tasks.json", result[2].Path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractRequiredFiles_WithNoExpect_ReturnsEmpty()
    {
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["session"] = JsonSerializer.Deserialize<JsonElement>("\"plan\""),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        Assert.Empty(result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractRequiredFiles_WithNullInput_ReturnsEmpty()
    {
        var result = TaskRunExtensions.ExtractRequiredFiles(null);
        Assert.Empty(result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExtractRequiredFiles_WithEmptyPath_SkipsEntry()
    {
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["expect"] = JsonSerializer.Deserialize<JsonElement>("""
                {"files": [{"path": ""}, {"path": "valid.md"}]}
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        Assert.Single(result);
        Assert.Equal("valid.md", result[0].Path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DeriveClassification_ForCoreAndMohistInternal_UsesOrchestration()
    {
        var classification = TaskRunExtensions.DeriveClassification("core/script", null);
        Assert.Equal(TaskClassification.Orchestration, classification);

        classification = TaskRunExtensions.DeriveClassification("mohist/openspec-sync", null);
        Assert.Equal(TaskClassification.Orchestration, classification);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DeriveClassification_ForAgentTask_UsesUserFacing()
    {
        var classification = TaskRunExtensions.DeriveClassification("mohist/acp-agent", null);
        Assert.Equal(TaskClassification.UserFacing, classification);

        classification = TaskRunExtensions.DeriveClassification("anthropic/claude", null);
        Assert.Equal(TaskClassification.UserFacing, classification);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskRun_WithRequiredFiles_ContainsMetadata()
    {
        var withDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"expect": {"files": [{"path": "proposal.md"}]}}
            """)!;
        var requiredFiles = TaskRunExtensions.ExtractRequiredFiles(withDict);

        var taskRun = new TaskRun
        {
            Id = "proposal.1",
            DefinitionId = "proposal",
            Attempt = 1,
            Title = "Generate proposal",
            Uses = "mohist/acp-agent",
            WithInput = withDict,
            Status = TaskRunStatus.Pending,
            RequiredFiles = requiredFiles,
            Classification = TaskRunExtensions.DeriveClassification("mohist/acp-agent", requiredFiles)
        };

        Assert.NotNull(taskRun.RequiredFiles);
        Assert.Single(taskRun.RequiredFiles);
        Assert.Equal("proposal.md", taskRun.RequiredFiles[0].Path);
        Assert.Equal("task-expect", taskRun.RequiredFiles[0].Source);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskRun_StatusCanBeUpdated_WithoutRemovingRequiredFiles()
    {
        var withDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"expect": {"files": [{"path": "design.md"}]}}
            """)!;
        var requiredFiles = TaskRunExtensions.ExtractRequiredFiles(withDict);
        var taskRun = new TaskRun
        {
            Id = "design.1",
            DefinitionId = "design",
            Attempt = 1,
            Title = "Create design",
            Uses = "mohist/acp-agent",
            WithInput = withDict,
            Status = TaskRunStatus.Pending,
            RequiredFiles = requiredFiles
        };

        Assert.NotNull(taskRun.RequiredFiles);
        Assert.Single(taskRun.RequiredFiles);

        taskRun.Status = TaskRunStatus.Completed;
        Assert.Equal(TaskRunStatus.Completed, taskRun.Status);
        Assert.NotNull(taskRun.RequiredFiles);
        Assert.Equal("design.md", taskRun.RequiredFiles[0].Path);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskRun_NoFileContentStored()
    {
        var withDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"expect": {"files": [{"path": "proposal.md"}]}}
            """)!;
        var requiredFiles = TaskRunExtensions.ExtractRequiredFiles(withDict);
        var taskRun = new TaskRun
        {
            Id = "proposal.1",
            DefinitionId = "proposal",
            Attempt = 1,
            Title = "Generate proposal",
            Uses = "mohist/acp-agent",
            WithInput = withDict,
            Status = TaskRunStatus.Pending,
            RequiredFiles = requiredFiles
        };

        var json = JsonSerializer.Serialize(taskRun);
        Assert.DoesNotContain("proposal content", json);
        Assert.DoesNotContain("# Introduction", json);
    }
}