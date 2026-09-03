using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Workflow;

[Trait("level", "L1")]
public sealed class IssueWorkspaceConvergenceSpecs : WorkflowGrainSpecs
{
    public IssueWorkspaceConvergenceSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task IssueWorkflow_PlanLoadTasksAndBuildScriptShareNamedWorkspace()
    {
        await ClearGlobalRunnerRegistryAsync();

        var workflow = await CreateWorkflowAsync($"issue-workspace-{Guid.NewGuid():N}");
        var projectId = TestProjectId(_workflowId!);
        await Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"project-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "app",
                GitUrl = "https://github.com/example/app.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        _runnerId = await RegisterRunnerAsync();

        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(projectId, 1, null, WorkflowProfileCatalog.LocalId),
            new WorkflowStartSnapshot(
                new WorkflowRepositoryContext("app", "https://github.com/example/app.git", "main"),
                new WorkspaceIdentity("/workspaces/issue-1", "issue-1"),
                "true"));
        Assert.Equal(
            WorkflowAssignmentStatus.Assigned,
            (await workflow.AssignWorkerAsync(_runnerId)).Status);

        var planAgentSeen = false;
        var loadTasksSeen = false;
        var buildScriptSeen = false;

        for (var step = 0; step < 80; step++)
        {
            var run = await LoadRunAsync(_workflowId!);
            if (run.Status == WorkflowRunStatus.Completed)
                break;
            if (run.CurrentStage().Status == StageRunStatus.AwaitingApproval)
            {
                await workflow.ApproveAsync();
                continue;
            }

            var (work, runnerId) = await PollWorkAnyAsync();
            AssertSharedIssueWorkspace(work);

            if (work.OwnerKind == WorkDispatchOwnerKinds.AgentJob)
            {
                if (work.Stage == "plan")
                    planAgentSeen = true;

                await ReportAsync(runnerId, work, "completed");
                continue;
            }

            if (work.Stage == "build"
                && work.WorkId.StartsWith("load-tasks.", StringComparison.Ordinal))
            {
                Assert.Equal("mohist/task-list", work.Uses);
                var with = ParseJson(work.With);
                Assert.Equal("PLANS/tasks.json", with.GetProperty("path").GetString());
                loadTasksSeen = true;

                await ReportAsync(runnerId, work, new WorkResult(
                    "completed",
                    AddTasks:
                    [
                        new RuntimeTaskInput(
                            "build-task",
                            "Build the planned change",
                            "mohist/agent",
                            JsonSerializer.SerializeToElement(new
                            {
                                name = "mohist/builder",
                                prompt = "Build the planned change.",
                            })),
                    ]));
                continue;
            }

            if (work.Stage == "build"
                && work.WorkType == "task"
                && work.Uses == "core/script"
                && work.WorkId.StartsWith("build-health.", StringComparison.Ordinal))
            {
                var with = ParseJson(work.With);
                Assert.Equal("REPOS/${{ repository.name }}", with.GetProperty("working-directory").GetString());
                buildScriptSeen = true;
            }

            if (work.WorkType == "checks")
            {
                var current = (await LoadRunAsync(_workflowId!)).CurrentStage();
                await ReportChecksPassAsync(runnerId, work, current.Checks.Select(check => check.Name).ToArray());
            }
            else
            {
                await ReportAsync(runnerId, work, "completed");
            }
        }

        var completed = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
        Assert.True(planAgentSeen);
        Assert.True(loadTasksSeen);
        Assert.True(buildScriptSeen);
    }

    private static void AssertSharedIssueWorkspace(WorkDispatch work)
    {
        Assert.NotNull(work.Variables);
        var payload = ParseJson(work.Variables);
        var workspace = payload.GetProperty("workspace");
        Assert.Equal("issue-1", workspace.GetProperty("name").GetString());
        Assert.Equal("app", payload.GetProperty("repository").GetProperty("name").GetString());
    }

    private static JsonElement ParseJson(string? json)
    {
        Assert.False(string.IsNullOrWhiteSpace(json));
        return JsonDocument.Parse(json!).RootElement.Clone();
    }
}
