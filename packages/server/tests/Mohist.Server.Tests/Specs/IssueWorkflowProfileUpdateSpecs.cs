using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Infrastructure;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class IssueWorkflowProfileUpdateSpecs
{
    private static WorkflowDefinition SampleDefinition(string id = "test-workflow") => new(
        id,
        Stages:
        [
            new StageDefinition("plan", [new TaskDefinition("t1", "Task 1")], []),
            new StageDefinition("build", [new TaskDefinition("t2", "Task 2")], []),
        ]);

    [Fact]
    public void CopyFrom_SetsUpdateModeToReference()
    {
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            SampleDefinition());

        Assert.Equal(WorkflowProfileUpdateMode.Reference, profile.UpdateMode);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, profile.SourceProfileId);
    }

    [Fact]
    public void SwitchTo_SetsUpdateModeToReference_AndUpdatesSourceProfileId()
    {
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            SampleDefinition());

        var newDef = SampleDefinition("new-workflow");
        profile.SwitchTo("new-profile", newDef);

        Assert.Equal(WorkflowProfileUpdateMode.Reference, profile.UpdateMode);
        Assert.Equal("new-profile", profile.SourceProfileId);
        Assert.Equal("new-workflow", profile.Definition.Id);
    }

    [Fact]
    public void ApplyCustomDefinition_SetsUpdateModeToCustom_AndPreservesSourceProfileId()
    {
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            SampleDefinition());

        var customDef = new WorkflowDefinition(
            "custom-workflow",
            Stages: [new StageDefinition("custom-stage", [new TaskDefinition("ct1", "Custom Task")], [])]);
        profile.ApplyCustomDefinition(IssueWorkflowProfiles.DefaultId, customDef);

        Assert.Equal(WorkflowProfileUpdateMode.Custom, profile.UpdateMode);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, profile.SourceProfileId);
        Assert.Equal("custom-workflow", profile.Definition.Id);
        Assert.Single(profile.Definition.Stages);
        Assert.Equal("custom-stage", profile.Definition.Stages[0].Stage);
    }

    [Fact]
    public void ApplyCustomDefinition_DeepCopiesDefinition()
    {
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            SampleDefinition());

        var customDef = SampleDefinition("custom");
        profile.ApplyCustomDefinition(IssueWorkflowProfiles.DefaultId, customDef);

        Assert.NotSame(customDef, profile.Definition);
    }

    [Fact]
    public void SerializationRoundtrip_PreservesUpdateModeReference()
    {
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            SampleDefinition());

        var json = IssueWorkflowProfileSnapshot.Serialize(profile);
        var deserialized = IssueWorkflowProfileSnapshot.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(WorkflowProfileUpdateMode.Reference, deserialized!.UpdateMode);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, deserialized.SourceProfileId);
    }

    [Fact]
    public void SerializationRoundtrip_PreservesUpdateModeCustom()
    {
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            SampleDefinition());
        profile.ApplyCustomDefinition(IssueWorkflowProfiles.DefaultId, SampleDefinition("custom"));

        var json = IssueWorkflowProfileSnapshot.Serialize(profile);
        var deserialized = IssueWorkflowProfileSnapshot.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(WorkflowProfileUpdateMode.Custom, deserialized!.UpdateMode);
        Assert.Equal(IssueWorkflowProfiles.DefaultId, deserialized.SourceProfileId);
    }

    [Fact]
    public void BackwardsCompatible_Deserialization_DefaultsToReference()
    {
        var json = """{"SourceProfileId":"mohist/default","Definition":{"Id":"w","Stages":[]}}""";
        var deserialized = IssueWorkflowProfileSnapshot.Deserialize(json);

        Assert.NotNull(deserialized);
        Assert.Equal(WorkflowProfileUpdateMode.Reference, deserialized!.UpdateMode);
    }

    [Fact]
    public void CustomYaml_ParsesAndApplies()
    {
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            SampleDefinition());

        var yaml = """
            id: custom-from-yaml
            stages:
              - stage: plan
                tasks:
                  - id: yaml-task
                    title: YAML Task
                checks: []
            """;
        var parsed = WorkflowYamlSerializer.FromYaml(yaml, "from-yaml");
        profile.ApplyCustomDefinition(IssueWorkflowProfiles.DefaultId, parsed);

        Assert.Equal(WorkflowProfileUpdateMode.Custom, profile.UpdateMode);
        Assert.Equal("custom-from-yaml", profile.Definition.Id);
        Assert.Single(profile.Definition.Stages);
        Assert.Single(profile.Definition.Stages[0].Tasks);
        Assert.Equal("yaml-task", profile.Definition.Stages[0].Tasks[0].Id);
    }
}
