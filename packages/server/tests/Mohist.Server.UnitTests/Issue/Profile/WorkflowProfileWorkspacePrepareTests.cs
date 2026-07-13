using Mohist.Server.Issue.Services.WorkflowProfiles;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class WorkflowProfileWorkspacePrepareTests
{
    [Theory]
    [InlineData(IssueWorkflowProfiles.LocalId)]
    [InlineData(IssueWorkflowProfiles.GithubPrId)]
    public void WorkflowDefinition_EachStageStartsWithWorkspacePrepare_AndNeverRecoversThroughIt(string profileId)
    {
        var definition = MohistWorkflow.LoadDefinitionForProfile(profileId);

        foreach (var stage in definition.Stages)
        {
            Assert.Equal("workspace-prepare", stage.Tasks.First().Id);
            Assert.Equal(1, stage.Tasks.Count(task => task.Id == "workspace-prepare"));

            var recoveryTasks = stage.Tasks
                .Where(task => task.Recovery is not null)
                .SelectMany(task => task.Recovery!.Handlers)
                .SelectMany(handler => handler.Tasks);

            Assert.DoesNotContain(recoveryTasks, task => task.Id == "workspace-prepare");
        }
    }
}
