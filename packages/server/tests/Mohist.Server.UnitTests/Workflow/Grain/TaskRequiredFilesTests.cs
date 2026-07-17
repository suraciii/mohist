using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public class TaskRequiredFilesTests
{
    [Fact]
    public void ExtractRequiredFiles_WithExpectFiles_ReturnsRequiredFileEntries()
    {
        var expect = new Dictionary<string, JsonElement?>
        {
            ["files"] = JsonSerializer.Deserialize<JsonElement>("""
                [{"path": "proposal.md", "markers": ["<promise>PASS</promise>"]}]
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(expect);

        Assert.Single(result);
        Assert.Equal("proposal.md", result[0].Path);
        Assert.Equal("task-expect", result[0].Source);
        Assert.True(result[0].CanFetchContent);
        Assert.Contains("<promise>PASS</promise>", result[0].Markers!);
    }

    [Fact]
    public void ExtractRequiredFiles_WithMultipleFiles_ReturnsAllEntries()
    {
        var expect = new Dictionary<string, JsonElement?>
        {
            ["files"] = JsonSerializer.Deserialize<JsonElement>("""
                [
                    {"path": "proposal.md"},
                    {"path": "design.md"},
                    {"path": "tasks.json"}
                ]
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(expect);

        Assert.Equal(3, result.Count);
        Assert.Equal("proposal.md", result[0].Path);
        Assert.Equal("design.md", result[1].Path);
        Assert.Equal("tasks.json", result[2].Path);
    }

    [Fact]
    public void ExtractRequiredFiles_WithNoExpect_ReturnsEmpty()
    {
        var expect = new Dictionary<string, JsonElement?>
        {
            ["session"] = JsonSerializer.Deserialize<JsonElement>("\"plan\""),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(expect);

        Assert.Empty(result);
    }

    [Fact]
    public void ExtractRequiredFiles_WithNullInput_ReturnsEmpty()
    {
        var result = TaskRunExtensions.ExtractRequiredFiles(null);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractRequiredFiles_WithEmptyPath_SkipsEntry()
    {
        var expect = new Dictionary<string, JsonElement?>
        {
            ["files"] = JsonSerializer.Deserialize<JsonElement>("""
                [{"path": ""}, {"path": "valid.md"}]
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(expect);

        Assert.Single(result);
        Assert.Equal("valid.md", result[0].Path);
    }

    [Fact]
    public void ExtractRequiredFiles_OutputMarkerPath_IsNotProjectedAsFile()
    {
        // Spec scenario: "_output is not projected as a file". The marker
        // path `_output` is a turn-text requirement; the required-files
        // projection MUST NOT expose it as a fetchable file path.
        var expect = new Dictionary<string, JsonElement?>
        {
            ["markers"] = JsonSerializer.Deserialize<JsonElement>("""
                [
                    {"path": "_output", "oneOf": ["<promise>done</promise>", "<promise>unfinished</promise>"]},
                    {"path": "review.md", "oneOf": ["<promise>PASS</promise>", "<promise>FAIL</promise>"]}
                ]
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(expect);

        Assert.Single(result);
        Assert.Equal("review.md", result[0].Path);
        Assert.True(result[0].CanFetchContent);
        Assert.DoesNotContain(result, r => r.Path == "_output");
    }

    [Fact]
    public void DeriveClassification_ForCoreAndMohistInternal_UsesOrchestration()
    {
        var classification = TaskRunExtensions.DeriveClassification("core/script", null);
        Assert.Equal(TaskClassification.Orchestration, classification);
    }

    [Fact]
    public void DeriveClassification_ForAgentTask_UsesUserFacing()
    {
        var classification = TaskRunExtensions.DeriveClassification("mohist/acp-agent", null);
        Assert.Equal(TaskClassification.UserFacing, classification);

        classification = TaskRunExtensions.DeriveClassification("anthropic/claude", null);
        Assert.Equal(TaskClassification.UserFacing, classification);
    }

    [Fact]
    public void TaskRun_WithRequiredFiles_ContainsMetadata()
    {
        var withDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"session": "plan"}
            """)!;
        var expectDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"files": [{"path": "proposal.md"}]}
            """)!;
        var requiredFiles = TaskRunExtensions.ExtractRequiredFiles(expectDict);

        var taskRun = new TaskRun
        {
            Id = "proposal.1",
            DefinitionId = "proposal",
            Attempt = 1,
            Title = "Generate proposal",
            Uses = "mohist/acp-agent",
            WithInput = withDict,
            ExpectInput = expectDict,
            Status = TaskRunStatus.Pending,
            RequiredFiles = requiredFiles,
            Classification = TaskRunExtensions.DeriveClassification("mohist/acp-agent", requiredFiles)
        };

        Assert.NotNull(taskRun.RequiredFiles);
        Assert.Single(taskRun.RequiredFiles);
        Assert.Equal("proposal.md", taskRun.RequiredFiles[0].Path);
        Assert.Equal("task-expect", taskRun.RequiredFiles[0].Source);
    }

    [Fact]
    public void TaskRun_StatusCanBeUpdated_WithoutRemovingRequiredFiles()
    {
        var withDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"session": "plan"}
            """)!;
        var expectDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"files": [{"path": "design.md"}]}
            """)!;
        var requiredFiles = TaskRunExtensions.ExtractRequiredFiles(expectDict);
        var taskRun = new TaskRun
        {
            Id = "design.1",
            DefinitionId = "design",
            Attempt = 1,
            Title = "Create design",
            Uses = "mohist/acp-agent",
            WithInput = withDict,
            ExpectInput = expectDict,
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

    [Fact]
    public void TaskRun_NoFileContentStored()
    {
        var withDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"session": "plan"}
            """)!;
        var expectDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"files": [{"path": "proposal.md"}]}
            """)!;
        var requiredFiles = TaskRunExtensions.ExtractRequiredFiles(expectDict);
        var taskRun = new TaskRun
        {
            Id = "proposal.1",
            DefinitionId = "proposal",
            Attempt = 1,
            Title = "Generate proposal",
            Uses = "mohist/acp-agent",
            WithInput = withDict,
            ExpectInput = expectDict,
            Status = TaskRunStatus.Pending,
            RequiredFiles = requiredFiles
        };

        var json = JsonSerializer.Serialize(taskRun);
        Assert.DoesNotContain("proposal content", json);
        Assert.DoesNotContain("# Introduction", json);
    }
}