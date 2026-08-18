using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

/// <summary>
/// Tests for the snapshot-aware stage resolver and the legacy aggregate
/// fallback. Snapshot-backed runs MUST resolve from
/// <c>BoundWorkflowDefinitionJson</c> and never from the current profile;
/// pre-snapshot runs MUST stay on the retained aggregate definition for
/// affected built-in profiles and MUST NOT synthesize lane state.
/// </summary>
public sealed class WorkflowBoundDefinitionResolverTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResolveStage_SnapshotBacked_ReturnsStageFromSnapshot()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("plan", new[] { new TaskDefinition("draft", "Draft", "spec/task") }, Array.Empty<CheckDefinition>()),
            new StageDefinition("build",
                VerificationLaneCatalog.LaneIds.Select(id => new TaskDefinition(id, id, "core/script")).ToList(),
                Array.Empty<CheckDefinition>()),
        });
        var run = CreateRun("mohist/local", WorkflowYamlSerializer.ToJson(definition));

        var result = WorkflowBoundDefinitionResolver.ResolveStage(run, "build");

        Assert.Equal(WorkflowBoundDefinitionResolver.BoundDefinitionSource.Snapshot, result.Source);
        Assert.True(result.IsLaneEnabled);
        Assert.Equal(VerificationLaneCatalog.LaneIds.Count, result.Stage.Tasks.Count);
    }

    [Fact]
    public void ResolveStage_NoSnapshot_LegacyProfile_ReturnsAggregateDefinition()
    {
        var run = CreateRun("mohist/local", definitionJson: null);

        var result = WorkflowBoundDefinitionResolver.ResolveStage(run, "build");

        Assert.Equal(WorkflowBoundDefinitionResolver.BoundDefinitionSource.LegacyAggregate, result.Source);
        Assert.False(result.IsLaneEnabled);
        // The aggregate verify task is the single legacy build task.
        var task = Assert.Single(result.Stage.Tasks);
        Assert.Equal("verify", task.Id);
        Assert.Equal("core/script", task.Uses);
    }

    [Fact]
    public void ResolveStage_NoSnapshot_LegacyProfile_PlanReturnsMissing()
    {
        // The retained legacy aggregate only contains the build stage; plan
        // is not part of the pre-issue-625 aggregate, so legacy-mode runs
        // cannot materialize plan from here.
        var run = CreateRun("mohist/local", definitionJson: null);

        var result = WorkflowBoundDefinitionResolver.ResolveStage(run, "plan");

        Assert.Equal(WorkflowBoundDefinitionResolver.BoundDefinitionSource.Missing, result.Source);
        Assert.False(result.IsLaneEnabled);
    }

    [Fact]
    public void ResolveStage_NoSnapshot_CustomProfile_ReturnsMissing()
    {
        var run = CreateRun("custom/profile", definitionJson: null);

        var result = WorkflowBoundDefinitionResolver.ResolveStage(run, "build");

        Assert.Equal(WorkflowBoundDefinitionResolver.BoundDefinitionSource.Missing, result.Source);
        Assert.False(result.IsLaneEnabled);
    }

    [Fact]
    public void ResolveStage_SnapshotContainsAggregateOnly_NotLaneEnabled()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", new[]
            {
                new TaskDefinition("verify", "Verify", "core/script"),
            }, Array.Empty<CheckDefinition>()),
        });
        var run = CreateRun("custom/profile", WorkflowYamlSerializer.ToJson(definition));

        var result = WorkflowBoundDefinitionResolver.ResolveStage(run, "build");

        Assert.Equal(WorkflowBoundDefinitionResolver.BoundDefinitionSource.Snapshot, result.Source);
        Assert.False(result.IsLaneEnabled);
    }

    [Fact]
    public void IsLaneEnabledBuildStage_TrueForSixCoreScriptTasksInOrder()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build",
                VerificationLaneCatalog.LaneIds.Select(id => new TaskDefinition(id, id, "core/script")).ToList(),
                Array.Empty<CheckDefinition>()),
        });

        Assert.True(WorkflowBoundDefinitionResolver.IsLaneEnabledBuildStage(definition));
    }

    [Fact]
    public void IsLaneEnabledBuildStage_FalseWhenLaneTaskUsesWrongAction()
    {
        var tasks = VerificationLaneCatalog.LaneIds
            .Select((id, i) => i == 0
                ? new TaskDefinition(id, id, "core/different")
                : new TaskDefinition(id, id, "core/script"))
            .ToList();
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", tasks, Array.Empty<CheckDefinition>()),
        });

        Assert.False(WorkflowBoundDefinitionResolver.IsLaneEnabledBuildStage(definition));
    }

    [Fact]
    public void IsLaneEnabledBuildStage_FalseWhenOrderDoesNotMatchCatalog()
    {
        var laneIds = VerificationLaneCatalog.LaneIds.ToList();
        var reordered = new[]
        {
            laneIds[1], laneIds[0], laneIds[2], laneIds[3], laneIds[4], laneIds[5],
        };
        var tasks = reordered.Select(id => new TaskDefinition(id, id, "core/script")).ToList();
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", tasks, Array.Empty<CheckDefinition>()),
        });

        Assert.False(WorkflowBoundDefinitionResolver.IsLaneEnabledBuildStage(definition));
    }

    [Fact]
    public void IsLaneEnabledBuildStage_FalseWhenSixTasksButWrongIds()
    {
        var tasks = new[]
        {
            new TaskDefinition("a", "A", "core/script"),
            new TaskDefinition("b", "B", "core/script"),
            new TaskDefinition("c", "C", "core/script"),
            new TaskDefinition("d", "D", "core/script"),
            new TaskDefinition("e", "E", "core/script"),
            new TaskDefinition("f", "F", "core/script"),
        };
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", tasks, Array.Empty<CheckDefinition>()),
        });

        Assert.False(WorkflowBoundDefinitionResolver.IsLaneEnabledBuildStage(definition));
    }

    [Fact]
    public void IsLaneEnabledBuildStage_FalseWhenExtraBuildTaskPresent()
    {
        var tasks = VerificationLaneCatalog.LaneIds
            .Select(id => new TaskDefinition(id, id, "core/script"))
            .Append(new TaskDefinition("extra", "Extra", "core/script"))
            .ToList();
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", tasks, Array.Empty<CheckDefinition>()),
        });

        Assert.False(WorkflowBoundDefinitionResolver.IsLaneEnabledBuildStage(definition));
    }

    [Fact]
    public void RetainedLegacyAggregate_AffectsBothBuiltInProfiles()
    {
        Assert.Equal("mohist/local", RetainedLegacyAggregate.LocalProfileId);
        Assert.Equal("mohist/github-pr", RetainedLegacyAggregate.GitHubPrProfileId);
        Assert.NotNull(RetainedLegacyAggregate.TryGetLegacyDefinition("mohist/local"));
        Assert.NotNull(RetainedLegacyAggregate.TryGetLegacyDefinition("mohist/github-pr"));
        Assert.Null(RetainedLegacyAggregate.TryGetLegacyDefinition("custom/profile"));
    }

    private static WorkflowRun CreateRun(string profileId, string? definitionJson) => new()
    {
        Id = "run-1",
        Metadata = new WorkflowRunMetadata("Issue 42", CreatedAt, ProjectId: "project-1", IssueNumber: 42),
        Status = WorkflowRunStatus.Running,
        CurrentStageId = "build",
        WorkflowProfileId = profileId,
        Stages = new List<StageRun>
        {
            new()
            {
                Id = "build",
                Attempt = 1,
                RequiresApproval = false,
                Status = StageRunStatus.Running,
            },
        },
        BoundWorkflowDefinitionJson = definitionJson,
    };
}