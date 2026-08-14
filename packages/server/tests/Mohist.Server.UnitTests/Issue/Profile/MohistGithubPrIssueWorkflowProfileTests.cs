using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistGithubPrIssueWorkflowProfileTests
{
    private static WorkflowDefinition GithubPrDefinition => WorkflowProfileCatalog.GithubPrWorkflowDefinition;

    private static IssueWorkflowProfileRegistry BuildRegistry() =>
        new();

    [Fact]
    public void IssueWorkflowProfiles_ExposesGithubPrIdConstant()
    {
        Assert.Equal("mohist/github-pr", IssueWorkflowProfiles.GithubPrId);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_ExposesCorrectMetadata()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Equal("mohist/github-pr", profile.Id);
        Assert.Equal("Mohist GitHub PR", profile.Name);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_DescriptionSurfacesGhCliPrerequisite()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Contains("gh", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh auth login", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub PR", profile.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_DescriptionReadsFromGithubPrYaml()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Equal(WorkflowProfileCatalog.GithubPrProfileAsset.Description, profile.Description);
        Assert.EndsWith("`gh` CLI on the runner host and `gh auth login` against the target repository.", profile.Description);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_Definition_ComesFromGithubPrYaml()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Same(GithubPrDefinition, profile.Definition);
        Assert.NotSame(WorkflowProfileCatalog.Definition, profile.Definition);
        Assert.Equal("mohist/github-pr", profile.Id);
    }

    [Fact]
    public void Registry_GetById_ResolvesMohistGithubPr()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/github-pr");

        Assert.Equal("mohist/github-pr", profile.Id);
        Assert.Same(GithubPrDefinition, profile.Definition);
    }

    [Fact]
    public void Registry_GetById_ResolvesMohistLocal()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/local");

        Assert.Equal("mohist/local", profile.Id);
        Assert.NotNull(profile.Definition);
    }

    [Fact]
    public void Registry_GetByNullOrEmpty_ResolvesMohistLocal()
    {
        var registry = BuildRegistry();

        var byNull = registry.Get(null);
        var byEmpty = registry.Get(string.Empty);
        var byWhitespace = registry.Get("   ");

        Assert.Equal("mohist/local", byNull.Id);
        Assert.Equal("mohist/local", byEmpty.Id);
        Assert.Equal("mohist/local", byWhitespace.Id);
    }

    [Fact]
    public void Registry_Exists_RecognizesMohistGithubPr()
    {
        var registry = BuildRegistry();

        Assert.True(registry.Exists("mohist/github-pr"));
        Assert.True(registry.Exists("mohist/local"));
        Assert.False(registry.Exists("mohist/pr"));
        Assert.False(registry.Exists("mohist/unknown"));
    }

    [Fact]
    public void Registry_ListIncludesBothBuiltInProfilesWithExpectedMetadata()
    {
        var registry = BuildRegistry();

        var list = registry.List();

        Assert.Equal(2, list.Count);
        var defaultEntry = Assert.Single(list, info => info.Id == "mohist/local");
        var prEntry = Assert.Single(list, info => info.Id == "mohist/github-pr");

        Assert.True(defaultEntry.IsDefault);
        Assert.False(prEntry.IsDefault);
        Assert.False(string.IsNullOrWhiteSpace(defaultEntry.Description));
        Assert.False(string.IsNullOrWhiteSpace(prEntry.Description));
        Assert.DoesNotContain(list, info => info.Id == "mohist/pr");
    }

    [Fact]
    public void Registry_ListDescribed_ExposesDescriptionForBothBuiltIns()
    {
        var registry = BuildRegistry();

        var described = registry.ListDescribed();

        Assert.Equal(2, described.Count);
        var defaultEntry = Assert.Single(described, d => d.Id == "mohist/local");
        var prEntry = Assert.Single(described, d => d.Id == "mohist/github-pr");

        Assert.False(string.IsNullOrWhiteSpace(defaultEntry.Description));
        Assert.False(string.IsNullOrWhiteSpace(prEntry.Description));
        Assert.Equal(WorkflowProfileCatalog.Profile.Description, defaultEntry.Description);
        Assert.Equal(WorkflowProfileCatalog.GithubPrProfileAsset.Description, prEntry.Description);
    }

    [Fact]
    public void Registry_Default_StillResolvesToMohistLocal()
    {
        var registry = BuildRegistry();

        var defaultInfo = registry.Default;

        Assert.Equal("mohist/local", defaultInfo.Id);
        Assert.True(defaultInfo.IsDefault);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_StagesFollowPlanBuildCheckIntegrateOrder()
    {
        var definition = GithubPrDefinition;

        Assert.Equal(new[] { "plan", "build", "check", "integrate" }, definition.Stages.Select(s => s.Stage).ToArray());
    }

    [Fact]
    public void GithubPrWorkflowDefinition_PlanStage_OpensDraftPrAsLastTask()
    {
        var plan = GithubPrDefinition.Stages.Single(s => s.Stage == "plan");

        var orderedIds = plan.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(
            new[] { "workspace-prepare", "proposal", "specs", "design", "tasks", "self-review", "push", "open-draft-pr" },
            orderedIds);

        var openDraftPr = plan.Tasks.Last();
        Assert.Equal("open-draft-pr", openDraftPr.Id);
        Assert.Equal("mohist/create-github-pr", openDraftPr.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(openDraftPr, "source"));
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(openDraftPr, "target"));
        Assert.Equal(true, ReadBoolWith(openDraftPr, "draft"));
        Assert.Equal("issue.title", ReadStringWith(openDraftPr, "titleFrom"));
        Assert.Equal("issue.body", ReadStringWith(openDraftPr, "bodyFrom"));
        Assert.NotNull(openDraftPr.SetVars);
        Assert.Equal("output.prNumber", openDraftPr.SetVars!["github.pr.number"]);
        Assert.Equal("output.prUrl", openDraftPr.SetVars!["github.pr.url"]);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_ApprovalFeedbackPublishesBeforeChecksResume()
    {
        var feedback = GithubPrDefinition.Approval!.Feedback!;
        var tasks = feedback.Tasks!;

        Assert.Equal(new[] { "apply-feedback", "publish-feedback" }, tasks.Select(task => task.Id).ToArray());
        var publish = tasks[1];
        Assert.Equal("mohist/push", publish.Uses);
        Assert.Equal("HEAD", ReadStringWith(publish, "source"));
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(publish, "target"));
        Assert.True(ReadBoolWith(publish, "force"));
    }

    [Fact]
    public void GithubPrWorkflowDefinition_PlanStage_SelfReviewUsesFailIfMarkerAndRetrySelf()
    {
        var plan = GithubPrDefinition.Stages.Single(s => s.Stage == "plan");

        var selfReview = plan.Tasks.Single(t => t.Id == "self-review");
        Assert.Equal("mohist/opencode", selfReview.Uses);
        Assert.NotNull(selfReview.Expect);
        var markers = GetList(selfReview.Expect!, "markers");
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
        Assert.Equal("mohist/opencode", fixPlanReview.Uses);
        Assert.Equal("${{ prompts.fix-plan-review }}", fixPlanReview.With!["prompt"]!.Value.GetString());
    }

    [Fact]
    public void GithubPrWorkflowDefinition_PlanStage_HasSingleOpenspecArtifactsCheck()
    {
        var plan = GithubPrDefinition.Stages.Single(s => s.Stage == "plan");

        Assert.Single(plan.Checks);
        var check = plan.Checks.Single();
        Assert.Equal("plan-artifacts", check.Id);
        Assert.Equal("mohist/openspec-artifacts", check.Uses);
         Assert.Equal("openspec/changes/issue-${{ issue.number }}", ReadStringWith(check, "changeDir"));

        var names = plan.Checks.Select(c => c.Id).ToArray();
        Assert.DoesNotContain(names, n => n == "proposal-complete");
        Assert.DoesNotContain(names, n => n == "specs-complete");
        Assert.DoesNotContain(names, n => n == "design-complete");
        Assert.DoesNotContain(names, n => n == "tasks-valid");
        Assert.DoesNotContain(names, n => n == "self-review-passed");
    }

    [Fact]
    public void LocalWorkflowDefinition_PlanStage_HasSingleOpenspecArtifactsCheck()
    {
        var plan = WorkflowProfileCatalog.Definition.Stages.Single(s => s.Stage == "plan");

        var checkNames = plan.Checks.Select(c => c.Id).ToArray();
        Assert.Equal(new[] { "plan-artifacts", "health" }, checkNames);

        var planArtifacts = plan.Checks.Single(c => c.Id == "plan-artifacts");
        Assert.Equal("mohist/openspec-artifacts", planArtifacts.Uses);
         Assert.Equal("openspec/changes/issue-${{ issue.number }}", ReadStringWith(planArtifacts, "changeDir"));

        Assert.DoesNotContain(plan.Checks, c => c.Id == "proposal-complete");
        Assert.DoesNotContain(plan.Checks, c => c.Id == "specs-complete");
        Assert.DoesNotContain(plan.Checks, c => c.Id == "design-complete");
        Assert.DoesNotContain(plan.Checks, c => c.Id == "tasks-valid");
        Assert.DoesNotContain(plan.Checks, c => c.Id == "self-review-passed");

        var selfReviewTask = plan.Tasks.Single(t => t.Id == "self-review");
        Assert.NotNull(selfReviewTask.Recovery);
        Assert.Equal("output.promise=FAIL", Assert.Single(selfReviewTask.Recovery!.Handlers).When);

        var health = plan.Checks.Single(c => c.Id == "health");
        Assert.Equal("core/script", health.Uses);
        Assert.Equal("git diff --check", ReadStringWith(health, "run"));
    }

    [Fact]
    public void GithubPrWorkflowDefinition_BuildStage_PreservesLoadTasksAndVerify()
    {
        var def = WorkflowProfileCatalog.Definition;
        var pr = GithubPrDefinition;

        var prBuild = pr.Stages.Single(s => s.Stage == "build");
        var defBuild = def.Stages.Single(s => s.Stage == "build");

        Assert.Equal(new[] { "workspace-prepare", "load-tasks", "verify", "push" }, prBuild.Tasks.Select(t => t.Id).ToArray());
        var prLoad = prBuild.Tasks.Single(t => t.Id == "load-tasks");
        var defLoad = defBuild.Tasks.Single(t => t.Id == "load-tasks");
        AssertTaskWithMapsMatchExcept(prLoad, defLoad);

        Assert.Empty(prBuild.Checks);
        var verify = prBuild.Tasks.Single(t => t.Id == "verify");
        Assert.Equal("core/script", verify.Uses);
        Assert.Equal("${{ vars.ci.verify }}", ReadStringWith(verify, "run"));
        Assert.Equal(300000, verify.With!["timeout"]!.Value.GetInt32());
        Assert.Equal(300000, defBuild.Tasks.Single(t => t.Id == "verify").With!["timeout"]!.Value.GetInt32());
        Assert.NotNull(verify.Recovery);
        Assert.True(verify.Recovery!.Budget >= 2);
        var handler = Assert.Single(verify.Recovery.Handlers);
        Assert.Null(handler.When);
        Assert.True(handler.RetrySelf);
        Assert.Equal("recover:fix-ci", Assert.Single(handler.Tasks).Id);
        Assert.Equal("HEAD", ReadStringWith(prBuild.Tasks.Single(t => t.Id == "push"), "source"));
    }

    [Fact]
    public void GithubPrWorkflowDefinition_CheckStage_SynchronizesAfterReviewBeforePublishingAndVerifying()
    {
        var check = GithubPrDefinition.Stages.Single(s => s.Stage == "check");

        var orderedIds = check.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "workspace-prepare", "ai-review", "rebase", "push", "mark-pr-ready", "verify-pr-checks" }, orderedIds);

        var aiReview = check.Tasks.Single(t => t.Id == "ai-review");
        Assert.Equal("mohist/opencode", aiReview.Uses);
        Assert.NotNull(aiReview.Expect);
        var markers = GetList(aiReview.Expect!, "markers");
        var marker = Assert.Single(markers!.Select(NormalizeToMap));
        var oneOfTexts = ExtractOneOfTexts(marker);
        Assert.Contains("<promise>PASS</promise>", oneOfTexts);
        Assert.Contains("<promise>FAIL</promise>", oneOfTexts);
        var recovery = aiReview.Recovery;
        Assert.NotNull(recovery);
        var handler = Assert.Single(recovery!.Handlers);
        Assert.True(handler.RetrySelf);
        Assert.Equal("output.promise=FAIL", handler.When);
        var fixReviewFindings = Assert.Single(handler.Tasks);
        Assert.Equal("recover:fix-review-findings", fixReviewFindings.Id);
        Assert.Equal("mohist/opencode", fixReviewFindings.Uses);
        Assert.Equal("${{ prompts.auto-fix }}", fixReviewFindings.With!["prompt"]!.Value.GetString());

        var rebase = check.Tasks.Single(t => t.Id == "rebase");
        Assert.Equal("mohist/rebase", rebase.Uses);
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(rebase, "baseBranch"));
        Assert.Equal("origin", ReadStringWith(rebase, "remote"));
        Assert.False(ReadBoolWith(rebase, "squash"));
        var rebaseRecovery = rebase.Recovery;
        Assert.NotNull(rebaseRecovery);
        var conflictHandler = Assert.Single(rebaseRecovery!.Handlers);
        Assert.Equal("error.code=conflict", conflictHandler.When);
        Assert.False(conflictHandler.RetrySelf);
        var resolveConflicts = Assert.Single(conflictHandler.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", resolveConflicts.Id);
        AssertAgentTask(resolveConflicts, "check", "${{ prompts.resolve-rebase-conflicts }}");

        var push = check.Tasks.Single(t => t.Id == "push");
        Assert.Equal("mohist/push", push.Uses);
        Assert.Equal("HEAD", ReadStringWith(push, "source"));
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(push, "target"));
        Assert.Equal("origin", ReadStringWith(push, "remote"));
        Assert.Equal(true, ReadBoolWith(push, "force"));

        var markPrReady = check.Tasks.Single(t => t.Id == "mark-pr-ready");
        Assert.Equal("mohist/mark-github-pr-ready", markPrReady.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(markPrReady, "prNumber"));

        var verifyPrChecks = check.Tasks.Single(t => t.Id == "verify-pr-checks");
        Assert.Equal("mohist/github-pr-checks", verifyPrChecks.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(verifyPrChecks, "prNumber"));
        var checksHandler = Assert.Single(verifyPrChecks.Recovery!.Handlers);
        Assert.Equal("error.code=pr-checks-failed", checksHandler.When);
        Assert.True(checksHandler.RetrySelf);
        Assert.Equal(new[] { "recover:fix-pr-checks", "recover:push" }, checksHandler.Tasks.Select(t => t.Id).ToArray());
        var fixPrChecks = checksHandler.Tasks.Single(t => t.Id == "recover:fix-pr-checks");
        AssertAgentTask(fixPrChecks, "check", "${{ prompts.fix-pr-checks }}");
        Assert.Single(check.Checks);
        var status = check.Checks.Single();
        Assert.Equal("github-pr-status", status.Id);
        Assert.Equal("mohist/github-pr-status", status.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(status, "prNumber"));
        Assert.Null(ReadStringWith(status, "expect"));

        Assert.DoesNotContain(handler.Tasks, task => task.Id == "rebase");
    }

    [Fact]
    public void GithubPrWorkflowDefinition_IntegrateStage_DeliversViaArchivePushMergePr()
    {
        var integrate = GithubPrDefinition.Stages.Single(s => s.Stage == "integrate");

        var orderedIds = integrate.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "workspace-prepare", "archive-change", "push", "merge-pr" }, orderedIds);

        Assert.Equal("sequential", integrate.LockBehavior);
        Assert.Contains("project-integration", integrate.Resources!);

        var archiveChange = integrate.Tasks.Single(t => t.Id == "archive-change");
        Assert.Equal("mohist/archive-change", archiveChange.Uses);
        Assert.Equal("openspec/changes/issue-${{ issue.number }}", ReadStringWith(archiveChange, "changeDir"));
        Assert.Null(ReadStringWith(archiveChange, "archiveHint"));

        var push = integrate.Tasks.Single(t => t.Id == "push");
        Assert.Equal("mohist/push", push.Uses);
        Assert.Equal("HEAD", ReadStringWith(push, "source"));
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(push, "target"));
        Assert.Equal(true, ReadBoolWith(push, "force"));

        var mergePr = integrate.Tasks.Single(t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(mergePr, "prNumber"));
        Assert.Equal("squash", ReadStringWith(mergePr, "method"));

        Assert.Single(integrate.Checks);
        var mergeVerified = integrate.Checks.Single();
        Assert.Equal("merge-verified", mergeVerified.Id);
        Assert.Equal("mohist/github-pr-status", mergeVerified.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(mergeVerified, "prNumber"));
        Assert.Equal("merged", ReadStringWith(mergeVerified, "expect"));
    }

    [Fact]
    public void GithubPrWorkflowDefinition_MergePrRecovery_DeclaresBaseMovedAndPrChecksFailedHandlers()
    {
        var mergePr = GithubPrDefinition
            .Stages.Single(s => s.Stage == "integrate")
            .Tasks.Single(t => t.Id == "merge-pr");

        var recovery = mergePr.Recovery;
        Assert.NotNull(recovery);
        Assert.Equal(2, recovery!.Budget);
        var handlers = recovery.Handlers.ToList();
        Assert.Equal(3, handlers.Count);

        var baseMoved = handlers.Single(h => h.When == "error.code=base-moved");
        Assert.True(baseMoved.RetrySelf);
        var baseMovedTasks = baseMoved.Tasks.ToList();
        Assert.Equal(new[] { "recover:rebase", "recover:push" }, baseMovedTasks.Select(t => t.Id).ToArray());

        var recoverRebase = baseMovedTasks.Single(t => t.Id == "recover:rebase");
        Assert.Equal("mohist/rebase", recoverRebase.Uses);
        var rebaseWith = recoverRebase.With!;
        Assert.Equal("${{ repository.baseBranch }}", rebaseWith["baseBranch"]!.Value.GetString());
        Assert.Equal("origin", rebaseWith["remote"]!.Value.GetString());
        Assert.False(rebaseWith["squash"]!.Value.GetBoolean());
        Assert.False(rebaseWith.ContainsKey("conflictMode"));

        var rebaseRecovery = recoverRebase.Recovery;
        Assert.NotNull(rebaseRecovery);
        Assert.Equal(2, rebaseRecovery!.Budget);
        var rebaseHandlers = rebaseRecovery.Handlers.ToList();
        Assert.Single(rebaseHandlers);
        var conflictHandler = rebaseHandlers.Single(h => h.When == "error.code=conflict");
        Assert.False(conflictHandler.RetrySelf);
        var conflictTasks = conflictHandler.Tasks.ToList();
        Assert.Single(conflictTasks);
        AssertAgentTask(conflictTasks.Single(), "integrate", "${{ prompts.resolve-rebase-conflicts }}");

        var recoverPushBaseMoved = baseMovedTasks.Single(t => t.Id == "recover:push");
        Assert.Equal("mohist/push", recoverPushBaseMoved.Uses);
        var pushWith = recoverPushBaseMoved.With!;
        Assert.Equal("HEAD", pushWith["source"]!.Value.GetString());
        Assert.Equal("${{ workspace.branch }}", pushWith["target"]!.Value.GetString());
        Assert.True(pushWith["force"]!.Value.GetBoolean());
        Assert.False(pushWith.ContainsKey("forceWithLease"));

        var prChecksFailed = handlers.Single(h => h.When == "error.code=pr-checks-failed");
        Assert.True(prChecksFailed.RetrySelf);
        var prChecksTasks = prChecksFailed.Tasks.ToList();
        Assert.Equal(new[] { "recover:fix-pr-checks", "recover:push" }, prChecksTasks.Select(t => t.Id).ToArray());
        AssertAgentTask(prChecksTasks.Single(t => t.Id == "recover:fix-pr-checks"), "integrate", "${{ prompts.fix-pr-checks }}");

        var recoverPushPrChecks = prChecksTasks.Single(t => t.Id == "recover:push");
        Assert.Equal("mohist/push", recoverPushPrChecks.Uses);
        Assert.Equal("HEAD", recoverPushPrChecks.With!["source"]!.Value.GetString());
        Assert.True(recoverPushPrChecks.With!["force"]!.Value.GetBoolean());

        var protectionConflict = handlers.Single(h => h.When == "error.code=protection-conflict");
        Assert.True(protectionConflict.RetrySelf);
        Assert.Empty(protectionConflict.Tasks);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_TaskIdsHaveNoStagePrefixesAndRecoveryUsesRecover()
    {
        var definition = GithubPrDefinition;

        var allTaskIds = definition.Stages
            .SelectMany(s => s.Tasks.Select(t => t.Id))
            .Concat(definition.Stages.SelectMany(s => s.Tasks
                .Where(t => t.Recovery is not null)
                .SelectMany(t => ExtractRecoveryTaskIds(t.Recovery!))))
            .ToList();

        Assert.NotEmpty(allTaskIds);
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("plan:", StringComparison.Ordinal), $"Task id '{id}' has plan: prefix"));
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("check:", StringComparison.Ordinal), $"Task id '{id}' has check: prefix"));
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("integrate:", StringComparison.Ordinal), $"Task id '{id}' has integrate: prefix"));
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("build:", StringComparison.Ordinal), $"Task id '{id}' has build: prefix"));

        var recoveryIds = allTaskIds.Where(id => id.StartsWith("recover:", StringComparison.Ordinal)).ToList();
        Assert.Contains("recover:fix-plan-review", recoveryIds);
        Assert.Contains("recover:fix-ci", recoveryIds);
        Assert.Contains("recover:fix-review-findings", recoveryIds);
        Assert.Contains("recover:rebase", recoveryIds);
        Assert.Contains("recover:push", recoveryIds);
        Assert.Contains("recover:fix-pr-checks", recoveryIds);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_AllTaskPromptsAreNamedReferences()
    {
        var definition = GithubPrDefinition;

        var inlinePromptTasks = new List<(string Stage, string Task, string Prompt)>();
        foreach (var stage in definition.Stages)
        {
            foreach (var task in CollectAllTasks(stage))
            {
                if (task.With is null) continue;
                if (!task.With.TryGetValue("prompt", out var promptElement) || promptElement is null) continue;
                var promptStr = promptElement.Value.ValueKind == JsonValueKind.String
                    ? promptElement.Value.GetString()
                    : JsonSerializer.Serialize(promptElement.Value);
                if (string.IsNullOrEmpty(promptStr)) continue;
                if (!promptStr.TrimStart().StartsWith("${{", StringComparison.Ordinal))
                {
                    inlinePromptTasks.Add((stage.Stage, task.Id, promptStr!));
                }
            }

            foreach (var recoveryTask in EnumerateRecoveryTaskElements(stage))
            {
                if (!recoveryTask.TryGetProperty("with", out var withEl)) continue;
                if (!withEl.TryGetProperty("prompt", out var promptEl)) continue;
                var promptStr = promptEl.ValueKind == JsonValueKind.String
                    ? promptEl.GetString()
                    : promptEl.GetRawText();
                if (string.IsNullOrEmpty(promptStr)) continue;
                if (!promptStr!.TrimStart().StartsWith("${{", StringComparison.Ordinal))
                {
                    inlinePromptTasks.Add((stage.Stage, recoveryTask.GetProperty("id").GetString() ?? "", promptStr));
                }
            }
        }

        Assert.Empty(inlinePromptTasks);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_DoesNotReferenceRemovedActions()
    {
        var definition = GithubPrDefinition;
        var integrateStage = definition.Stages.Single(s => s.Stage == "integrate");

        var usedActionIds = CollectAllTasks(integrateStage)
            .Select(t => t.Uses)
            .Where(u => !string.IsNullOrEmpty(u))
            .Concat(EnumerateRecoveryTaskElements(integrateStage)
                .Select(t => t.TryGetProperty("uses", out var usesEl) && usesEl.ValueKind == JsonValueKind.String ? usesEl.GetString() : null)
                .Where(u => !string.IsNullOrEmpty(u)))
            .ToList();

        Assert.DoesNotContain(usedActionIds, u => u == "mohist/create-pull-request");
        Assert.DoesNotContain(usedActionIds, u => u == "mohist/merge-pull-request");
        Assert.DoesNotContain(usedActionIds, u => u == "mohist/publish-via-pr");
    }

    [Fact]
    public void GithubPrWorkflowYaml_ParsesViaWorkflowProfileParser()
    {
        var yaml = ReadResourceYaml("mohist-github-pr.workflow.yaml");

        var definition = WorkflowProfileYamlParser.Parse(yaml, WorkflowProfileCatalog.GithubPrId).Definition;

        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());

        var planIds = definition.Stages[0].Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(
            new[] { "workspace-prepare", "proposal", "specs", "design", "tasks", "self-review", "push", "open-draft-pr" },
            planIds);
        foreach (var stage in definition.Stages)
        {
            var prepare = stage.Tasks.Single(t => t.Id == "workspace-prepare");
            Assert.Equal("${{ workspace.branch }}", prepare.With!["expectedBranch"]?.GetString());
        }
        Assert.Contains("mohist/create-github-pr", definition.Stages[0].Tasks.Select(t => t.Uses).ToArray());

        var checkTasks = JsonSerializer.Serialize(definition.Stages[2].Tasks);
        Assert.Contains("ai-review", checkTasks);
        Assert.Contains("push", checkTasks);
        Assert.Contains("mark-pr-ready", checkTasks);
        Assert.Contains("mohist/mark-github-pr-ready", checkTasks);
        Assert.Contains("mohist/push", checkTasks);
        Assert.DoesNotContain("create-pull-request", checkTasks);
        Assert.DoesNotContain("mohist/create-pull-request", checkTasks);

        var checkChecks = JsonSerializer.Serialize(definition.Stages[2].Checks);
        Assert.Contains("github-pr-status", checkChecks);
        Assert.DoesNotContain("merge-ready", checkChecks);

        var integrateIds = definition.Stages[3].Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "workspace-prepare", "archive-change", "push", "merge-pr" }, integrateIds);
        Assert.Contains("mohist/merge-github-pr", definition.Stages[3].Tasks.Select(t => t.Uses).ToArray());

        var integrateChecks = JsonSerializer.Serialize(definition.Stages[3].Checks);
        Assert.Contains("merge-verified", integrateChecks);
        Assert.Contains("merged", integrateChecks);

        var serializedAll = JsonSerializer.Serialize(definition);
        Assert.Contains("prompts.fix-plan-review", serializedAll);
        Assert.Contains("prompts.fix-pr-checks", serializedAll);
    }

    [Fact]
    public void GithubPrWorkflowYaml_MaterializedDefinitionRoundTripsViaWorkflowYamlSerializer()
    {
        var yaml = ReadResourceYaml("mohist-github-pr.workflow.yaml");

        var definition = WorkflowProfileYamlParser.Parse(yaml, WorkflowProfileCatalog.GithubPrId).Definition;
        var emitted = WorkflowYamlSerializer.ToYaml(definition);

        Assert.Contains("mohist/create-github-pr", emitted);
        Assert.Contains("mohist/github-pr-status", emitted);
        Assert.Contains("mohist/merge-github-pr", emitted);
        Assert.Contains("open-draft-pr", emitted);
        Assert.Contains("merge-verified", emitted);
        Assert.Contains("github-pr-status", emitted);
        Assert.Contains("when: error.code=conflict", emitted);
        Assert.Contains("id: recover:fix-ci", emitted);
        Assert.Contains("prompt: ${{ prompts.fix-ci }}", emitted);
        Assert.Contains("retrySelf: true", emitted);

        var reparsed = WorkflowYamlSerializer.FromYaml(emitted);
        Assert.Equal(definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.Equal(
            definition.Stages.SelectMany(s => s.Tasks).Select(t => t.Id),
            reparsed.Stages.SelectMany(s => s.Tasks).Select(t => t.Id));
    }

    private static void AssertAgentTask(TaskDefinition task, string session, string prompt) => Assert.Equal(("mohist/opencode", session, prompt, "${{ vars.agent }}"), (task.Uses, ReadStringWith(task, "session"), ReadStringWith(task, "prompt"), ReadStringWith(task, "options")));

    private static IEnumerable<Mohist.Workflow.Definition.TaskDefinition> CollectAllTasks(
        Mohist.Workflow.Definition.StageDefinition stage)
    {
        foreach (var task in stage.Tasks)
            foreach (var visited in CollectWithNested(task))
                yield return visited;
    }

    private static IEnumerable<Mohist.Workflow.Definition.TaskDefinition> CollectAllTasks(
        params Mohist.Workflow.Definition.StageDefinition[] stages)
    {
        foreach (var stage in stages)
            foreach (var task in CollectAllTasks(stage))
                yield return task;
    }

    private static IEnumerable<Mohist.Workflow.Definition.TaskDefinition> CollectWithNested(
        Mohist.Workflow.Definition.TaskDefinition task)
    {
        yield return task;
    }

    private static JsonElement GetRecovery(Dictionary<string, JsonElement?> with)
    {
        var element = with["recovery"] ?? throw new InvalidOperationException("task 'with' is missing 'recovery'");
        return element;
    }

    private static IEnumerable<string> ExtractRecoveryTaskIds(RecoveryDefinition recovery)
    {
        foreach (var handler in recovery.Handlers)
        {
            foreach (var task in handler.Tasks)
            {
                yield return task.Id;
            }
        }
    }

    private static IEnumerable<JsonElement> EnumerateRecoveryTaskElements(
        Mohist.Workflow.Definition.StageDefinition stage)
    {
        foreach (var task in stage.Tasks)
        {
            if (task.With is null) continue;
            if (!task.With.TryGetValue("recovery", out var recoveryEl) || recoveryEl is null) continue;
            var recovery = recoveryEl.Value;
            if (recovery.ValueKind != JsonValueKind.Object) continue;
            if (!recovery.TryGetProperty("handlers", out var handlers)) continue;
            foreach (var handler in handlers.EnumerateArray())
            {
                if (!handler.TryGetProperty("tasks", out var tasks)) continue;
                foreach (var t in tasks.EnumerateArray())
                    yield return t;
            }
        }
    }

    private static bool? ReadBoolWith(Mohist.Workflow.Definition.TaskDefinition task, string key)
    {
        if (task.With is null || !task.With.TryGetValue(key, out var element) || element is null) return null;
        if (element.Value.ValueKind == JsonValueKind.True) return true;
        if (element.Value.ValueKind == JsonValueKind.False) return false;
        if (element.Value.ValueKind == JsonValueKind.String)
        {
            var text = element.Value.GetString();
            if (text is null) return null;
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return element.Value.GetBoolean();
    }

    private static string? ReadStringWith(Mohist.Workflow.Definition.TaskDefinition task, string key)
    {
        if (task.With is null || !task.With.TryGetValue(key, out var element) || element is null) return null;
        return element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : element.Value.GetRawText();
    }

    private static string? ReadStringWith(Mohist.Workflow.Definition.CheckDefinition check, string key)
    {
        if (check.With is null || !check.With.TryGetValue(key, out var element) || element is null) return null;
        return element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : element.Value.GetRawText();
    }

    private static Dictionary<string, object?>? GetMap(Dictionary<string, JsonElement?> with, string key)
    {
        if (!with.TryGetValue(key, out var element) || element is null) return null;
        var json = element.Value.GetRawText();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
    }

    private static List<object?>? GetList(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            List<object?> list => list,
            JsonElement element when element.ValueKind == JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(element.GetRawText()),
            _ => null,
        };
    }

    private static List<object?>? GetList(Dictionary<string, JsonElement?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null) return null;
        return value.Value.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(value.Value.GetRawText()),
            _ => null,
        };
    }

    private static Dictionary<string, object?> NormalizeToMap(object? value) => value switch
    {
        Dictionary<string, object?> map => map,
        JsonElement element => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())
            ?? new Dictionary<string, object?>(),
        _ => new Dictionary<string, object?>(),
    };

    private static string[] ExtractOneOfTexts(Dictionary<string, object?> marker)
    {
        if (!marker.TryGetValue("oneOf", out var value) || value is null)
            return Array.Empty<string>();
        return value switch
        {
            IEnumerable<object?> enumerable => enumerable.Select(o => o?.ToString() ?? "").ToArray(),
            JsonElement element when element.ValueKind == JsonValueKind.Array => element.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                .ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    private static void AssertTaskWithMapsMatchExcept(
        Mohist.Workflow.Definition.TaskDefinition expected,
        Mohist.Workflow.Definition.TaskDefinition actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected.With), JsonSerializer.Serialize(actual.With));
    }

    private static string ReadResourceYaml(string fileName) =>
        Mohist.Server.Infrastructure.Resources.BuiltinTextResources
            .ReadWorkflowProfile(fileName);
}
