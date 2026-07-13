using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using static Mohist.Server.UnitTests.Issue.Profile.MohistGithubPrWorkflowDefinitionTestSupport;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistGithubPrWorkflowStagesTests
{
    [Fact]
    public void GithubPrWorkflowDefinition_StagesFollowPlanBuildCheckIntegrateOrder()
    {
        var definition = MohistWorkflow.GithubPrWorkflowDefinition;

        Assert.Equal(new[] { "plan", "build", "check", "integrate" }, definition.Stages.Select(s => s.Stage).ToArray());
        Assert.Equal("mohist/github-pr", definition.Id);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_PlanStage_OpensDraftPrAsLastTask()
    {
        var plan = MohistWorkflow.GithubPrWorkflowDefinition.Stages.Single(s => s.Stage == "plan");

        var orderedIds = plan.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(
            new[] { "workspace-prepare", "proposal", "specs", "design", "tasks", "self-review", "open-draft-pr" },
            orderedIds);

        var openDraftPr = plan.Tasks.Last();
        Assert.Equal("open-draft-pr", openDraftPr.Id);
        Assert.Equal("mohist/create-github-pr", openDraftPr.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(openDraftPr, "source"));
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(openDraftPr, "target"));
        Assert.Equal("origin", ReadStringWith(openDraftPr, "remote"));
        Assert.Equal(true, ReadBoolWith(openDraftPr, "draft"));
        Assert.Equal("issue.title", ReadStringWith(openDraftPr, "titleFrom"));
        Assert.Equal("issue.body", ReadStringWith(openDraftPr, "bodyFrom"));
        Assert.NotNull(openDraftPr.SetVars);
        Assert.Equal("output.prNumber", openDraftPr.SetVars!["github.pr.number"]);
        Assert.Equal("output.prUrl", openDraftPr.SetVars!["github.pr.url"]);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_PlanStage_SelfReviewUsesFailIfMarkerAndRetrySelf()
    {
        var plan = MohistWorkflow.GithubPrWorkflowDefinition.Stages.Single(s => s.Stage == "plan");

        var selfReview = plan.Tasks.Single(t => t.Id == "self-review");
        Assert.Equal("mohist/acp-agent", selfReview.Uses);
        Assert.NotNull(selfReview.With);
        var expect = GetMap(selfReview.With!, "expect");
        Assert.NotNull(expect);
        var markers = GetList(expect!, "markers");
        var marker = Assert.Single(markers!.Select(NormalizeToMap));
        var oneOfTexts = ExtractOneOfTexts(marker);
        Assert.Contains("<promise>PASS</promise>", oneOfTexts);
        Assert.Contains("<promise>FAIL</promise>", oneOfTexts);

        var recovery = selfReview.Recovery;
        Assert.NotNull(recovery);
        Assert.Equal(2, recovery!.Budget);
        var handler = Assert.Single(recovery.Handlers);
        Assert.True(handler.RetrySelf);
        var fixPlanReview = Assert.Single(handler.Tasks);
        Assert.Equal("recover:fix-plan-review", fixPlanReview.Id);
        Assert.Equal("mohist/acp-agent", fixPlanReview.Uses);
        Assert.Equal("${{ prompts.fix-plan-review }}", fixPlanReview.With!["prompt"]!.Value.GetString());
    }

    [Fact]
    public void GithubPrWorkflowDefinition_PlanStage_HasSingleOpenspecArtifactsCheck()
    {
        var plan = MohistWorkflow.GithubPrWorkflowDefinition.Stages.Single(s => s.Stage == "plan");

        Assert.Single(plan.Checks);
        var check = plan.Checks.Single();
        Assert.Equal("plan-artifacts", check.Name);
        Assert.Equal("mohist/openspec-artifacts", check.Uses);
        Assert.Equal("${{ openspecChangeDir }}", ReadStringWith(check, "changeDir"));

        var names = plan.Checks.Select(c => c.Name).ToArray();
        Assert.DoesNotContain(names, n => n == "proposal-complete");
        Assert.DoesNotContain(names, n => n == "specs-complete");
        Assert.DoesNotContain(names, n => n == "design-complete");
        Assert.DoesNotContain(names, n => n == "tasks-valid");
        Assert.DoesNotContain(names, n => n == "self-review-passed");
    }

    [Fact]
    public void LocalWorkflowDefinition_PlanStage_HasSingleOpenspecArtifactsCheck()
    {
        var plan = MohistWorkflow.Definition.Stages.Single(s => s.Stage == "plan");

        var checkNames = plan.Checks.Select(c => c.Name).ToArray();
        Assert.Equal(new[] { "plan-artifacts", "health" }, checkNames);

        var planArtifacts = plan.Checks.Single(c => c.Name == "plan-artifacts");
        Assert.Equal("mohist/openspec-artifacts", planArtifacts.Uses);
        Assert.Equal("${{ openspecChangeDir }}", ReadStringWith(planArtifacts, "changeDir"));

        Assert.DoesNotContain(plan.Checks, c => c.Name == "proposal-complete");
        Assert.DoesNotContain(plan.Checks, c => c.Name == "specs-complete");
        Assert.DoesNotContain(plan.Checks, c => c.Name == "design-complete");
        Assert.DoesNotContain(plan.Checks, c => c.Name == "tasks-valid");
        Assert.DoesNotContain(plan.Checks, c => c.Name == "self-review-passed");

        var selfReviewTask = plan.Tasks.Single(t => t.Id == "self-review");
        Assert.NotNull(selfReviewTask.Recovery);
        Assert.Equal("promise=FAIL", Assert.Single(selfReviewTask.Recovery!.Handlers).When);

        var health = plan.Checks.Single(c => c.Name == "health");
        Assert.Equal("core/script", health.Uses);
        Assert.Equal("git diff --check", ReadStringWith(health, "run"));
    }

    [Fact]
    public void GithubPrWorkflowDefinition_BuildStage_PreservesLoadTasksAndVerify()
    {
        var def = MohistWorkflow.Definition;
        var pr = MohistWorkflow.GithubPrWorkflowDefinition;

        var prBuild = pr.Stages.Single(s => s.Stage == "build");
        var defBuild = def.Stages.Single(s => s.Stage == "build");

        Assert.Equal(new[] { "workspace-prepare", "load-tasks", "verify" }, prBuild.Tasks.Select(t => t.Id).ToArray());
        var prLoad = prBuild.Tasks.Single(t => t.Id == "load-tasks");
        var defLoad = defBuild.Tasks.Single(t => t.Id == "load-tasks");
        AssertTaskWithMapsMatchExcept(prLoad, defLoad);

        Assert.Empty(prBuild.Checks);
        var verify = prBuild.Tasks.Single(t => t.Id == "verify");
        Assert.Equal("core/script", verify.Uses);
        Assert.Equal("${{ vars.ci.verify }}", ReadStringWith(verify, "run"));
        Assert.NotNull(verify.Recovery);
        Assert.True(verify.Recovery!.Budget >= 2);
        var handler = Assert.Single(verify.Recovery.Handlers);
        Assert.Equal("errorCode=script-failed", handler.When);
        Assert.True(handler.RetrySelf);
        Assert.Equal("recover:fix-tests", Assert.Single(handler.Tasks).Id);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_CheckStage_HasExactlyAiReviewPushMarkPrReadyAndGithubPrStatusCheck()
    {
        var check = MohistWorkflow.GithubPrWorkflowDefinition.Stages.Single(s => s.Stage == "check");

        var orderedIds = check.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "workspace-prepare", "ai-review", "push", "mark-pr-ready" }, orderedIds);

        var aiReview = check.Tasks.Single(t => t.Id == "ai-review");
        Assert.Equal("mohist/acp-agent", aiReview.Uses);
        Assert.NotNull(aiReview.With);
        var expect = GetMap(aiReview.With!, "expect");
        Assert.NotNull(expect);
        var markers = GetList(expect!, "markers");
        var marker = Assert.Single(markers!.Select(NormalizeToMap));
        var oneOfTexts = ExtractOneOfTexts(marker);
        Assert.Contains("<promise>PASS</promise>", oneOfTexts);
        Assert.Contains("<promise>FAIL</promise>", oneOfTexts);
        var recovery = aiReview.Recovery;
        Assert.NotNull(recovery);
        var handler = Assert.Single(recovery!.Handlers);
        Assert.True(handler.RetrySelf);
        Assert.Equal("promise=FAIL", handler.When);
        var fixReviewFindings = Assert.Single(handler.Tasks);
        Assert.Equal("recover:fix-review-findings", fixReviewFindings.Id);
        Assert.Equal("mohist/acp-agent", fixReviewFindings.Uses);
        Assert.Equal("${{ prompts.auto-fix }}", fixReviewFindings.With!["prompt"]!.Value.GetString());

        var push = check.Tasks.Single(t => t.Id == "push");
        Assert.Equal("mohist/push", push.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(push, "source"));
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(push, "target"));
        Assert.Equal("origin", ReadStringWith(push, "remote"));
        Assert.Equal(true, ReadBoolWith(push, "forceWithLease"));

        var markPrReady = check.Tasks.Single(t => t.Id == "mark-pr-ready");
        Assert.Equal("mohist/mark-github-pr-ready", markPrReady.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(markPrReady, "prNumber"));

        Assert.Single(check.Checks);
        var status = check.Checks.Single();
        Assert.Equal("github-pr-status", status.Name);
        Assert.Equal("mohist/github-pr-status", status.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(status, "prNumber"));
        Assert.Null(ReadStringWith(status, "expect"));

        var names = check.Checks.Select(c => c.Name).ToArray();
        Assert.DoesNotContain(names, n => n == "health");
        Assert.DoesNotContain(names, n => n == "review-passed");
        Assert.DoesNotContain(names, n => n == "merge-ready");
    }

    [Fact]
    public void GithubPrWorkflowDefinition_IntegrateStage_DeliversViaArchivePushMergePr()
    {
        var integrate = MohistWorkflow.GithubPrWorkflowDefinition.Stages.Single(s => s.Stage == "integrate");

        var orderedIds = integrate.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "workspace-prepare", "archive-change", "push", "merge-pr" }, orderedIds);

        Assert.Equal("sequential", integrate.LockBehavior);
        Assert.Contains("project-integration", integrate.Resources!);

        var archiveChange = integrate.Tasks.Single(t => t.Id == "archive-change");
        Assert.Equal("mohist/archive-change", archiveChange.Uses);
        Assert.Equal("${{ openspecChangeDir }}", ReadStringWith(archiveChange, "changeDir"));

        var push = integrate.Tasks.Single(t => t.Id == "push");
        Assert.Equal("mohist/push", push.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(push, "source"));
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(push, "target"));
        Assert.Equal(true, ReadBoolWith(push, "forceWithLease"));

        var mergePr = integrate.Tasks.Single(t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(mergePr, "prNumber"));
        Assert.Equal("squash", ReadStringWith(mergePr, "method"));

        Assert.Single(integrate.Checks);
        var mergeVerified = integrate.Checks.Single();
        Assert.Equal("merge-verified", mergeVerified.Name);
        Assert.Equal("mohist/github-pr-status", mergeVerified.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(mergeVerified, "prNumber"));
        Assert.Equal("merged", ReadStringWith(mergeVerified, "expect"));
    }
}
