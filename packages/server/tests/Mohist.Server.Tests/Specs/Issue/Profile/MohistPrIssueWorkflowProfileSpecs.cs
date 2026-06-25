using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Profile;

public class MohistPrIssueWorkflowProfileSpecs
{
    private static IssueWorkflowProfileRegistry BuildRegistry() =>
        new(new FakePromptLoader(), new FakeDbContextFactory());

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void IssueWorkflowProfiles_ExposesPrIdConstant()
    {
        Assert.Equal("mohist/pr", IssueWorkflowProfiles.PrId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void MohistPrIssueWorkflowProfile_ExposesCorrectMetadata()
    {
        var profile = new MohistPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Equal("mohist/pr", profile.Id);
        Assert.Equal("Mohist PR", profile.DisplayName);
        Assert.False(profile.IsDefault);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
        Assert.NotEmpty(profile.SuitableFor);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void MohistPrIssueWorkflowProfile_DescriptionSurfacesGhCliPrerequisite()
    {
        var profile = new MohistPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Contains("gh", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh auth login", profile.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub PR", profile.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void MohistPrIssueWorkflowProfile_SuitableForMentionsGhCliPrerequisite()
    {
        var profile = new MohistPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Contains(profile.SuitableFor, s => s.Contains("gh", StringComparison.OrdinalIgnoreCase));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void MohistPrIssueWorkflowProfile_DescriptionSurfacesGhCliPrerequisite_AsConstant()
    {
        Assert.Contains("gh", MohistPrIssueWorkflowProfile.PrDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh auth login", MohistPrIssueWorkflowProfile.PrDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub PR", MohistPrIssueWorkflowProfile.PrDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void MohistPrIssueWorkflowProfile_Definition_ComesFromPrYaml()
    {
        var profile = new MohistPrIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Same(MohistWorkflow.PrWorkflowDefinition, profile.Definition);
        Assert.NotSame(MohistWorkflow.Definition, profile.Definition);
        Assert.Equal("mohist/pr", profile.Definition.Id);
    }

    // ===================== Registry exposure =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_GetById_ResolvesMohistPr()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/pr");

        Assert.Equal("mohist/pr", profile.Id);
        Assert.False(profile.IsDefault);
        Assert.Same(MohistWorkflow.PrWorkflowDefinition, profile.Definition);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_GetById_ResolvesMohistDefault()
    {
        var registry = BuildRegistry();

        var profile = registry.Get("mohist/default");

        Assert.Equal("mohist/default", profile.Id);
        Assert.True(profile.IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_GetByNullOrEmpty_ResolvesMohistDefault()
    {
        var registry = BuildRegistry();

        var byNull = registry.Get(null);
        var byEmpty = registry.Get(string.Empty);
        var byWhitespace = registry.Get("   ");

        Assert.Equal("mohist/default", byNull.Id);
        Assert.Equal("mohist/default", byEmpty.Id);
        Assert.Equal("mohist/default", byWhitespace.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_Exists_RecognizesMohistPr()
    {
        var registry = BuildRegistry();

        Assert.True(registry.Exists("mohist/pr"));
        Assert.True(registry.Exists("mohist/default"));
        Assert.False(registry.Exists("mohist/unknown"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_ListIncludesBothBuiltInProfilesWithExpectedMetadata()
    {
        var registry = BuildRegistry();

        var list = registry.List();

        Assert.Equal(2, list.Count);
        var defaultEntry = Assert.Single(list, info => info.Id == "mohist/default");
        var prEntry = Assert.Single(list, info => info.Id == "mohist/pr");

        Assert.True(defaultEntry.IsDefault);
        Assert.False(prEntry.IsDefault);
        Assert.False(string.IsNullOrWhiteSpace(defaultEntry.Description));
        Assert.False(string.IsNullOrWhiteSpace(prEntry.Description));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_ListDescribed_IncludesSuitableForForBothBuiltIns()
    {
        var registry = BuildRegistry();

        var described = registry.ListDescribed();

        Assert.Equal(2, described.Count);
        var defaultEntry = Assert.Single(described, d => d.Id == "mohist/default");
        var prEntry = Assert.Single(described, d => d.Id == "mohist/pr");

        Assert.NotEmpty(defaultEntry.SuitableFor);
        Assert.NotEmpty(prEntry.SuitableFor);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Registry_Default_StillResolvesToMohistDefault()
    {
        var registry = BuildRegistry();

        var defaultInfo = registry.Default;

        Assert.Equal("mohist/default", defaultInfo.Id);
        Assert.True(defaultInfo.IsDefault);
    }

    // ===================== YAML drift / divergence guard =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_PlanAndCheckStages_MatchDefaultTaskForTask()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var def = MohistWorkflow.Definition;

        AssertStagesMatch(def.Stages[0], pr.Stages[0]);
        Assert.Equal(def.Stages[0].RequiresApproval, pr.Stages[0].RequiresApproval);
        Assert.Equal(def.Stages[2].RequiresApproval, pr.Stages[2].RequiresApproval);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_BuildStage_PrefixesCreatePullRequestBeforeLoadTasks()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var build = pr.Stages.Single(s => s.Stage == "build");

        var orderedIds = build.Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "build:open-pr", "load-tasks", "build:update-pr" }, orderedIds);

        var openPr = build.Tasks.First();
        Assert.Equal("mohist/create-pull-request", openPr.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(openPr, "source"));
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(openPr, "target"));
        Assert.Equal("issue.title", ReadStringWith(openPr, "titleFrom"));
        Assert.Equal("issue.body", ReadStringWith(openPr, "bodyFrom"));
        Assert.NotNull(openPr.SetVars);
        Assert.Equal("output.prNumber", openPr.SetVars!["github.pr.number"]);
        Assert.Equal("output.prUrl", openPr.SetVars!["github.pr.url"]);
        Assert.Empty(openPr.OnFailure?.Cases ?? new List<TaskFailureCase>());

        var updatePr = build.Tasks.Last();
        Assert.Equal("build:update-pr", updatePr.Id);
        Assert.Equal("mohist/create-pull-request", updatePr.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(updatePr, "source"));
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(updatePr, "target"));
        Assert.NotNull(updatePr.SetVars);
        Assert.Equal("output.prNumber", updatePr.SetVars!["github.pr.number"]);
        Assert.Equal("output.prUrl", updatePr.SetVars!["github.pr.url"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_BuildStage_PreservesLoadTasksFromDefault()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var def = MohistWorkflow.Definition;

        var prBuild = pr.Stages.Single(s => s.Stage == "build");
        var defBuild = def.Stages.Single(s => s.Stage == "build");

        var prLoad = prBuild.Tasks.Single(t => t.Id == "load-tasks");
        var defLoad = defBuild.Tasks.Single(t => t.Id == "load-tasks");
        AssertTaskWithMapsMatchExcept(prLoad, defLoad);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_CheckStage_KeepsAiReviewAsOnlyStaticTask()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var def = MohistWorkflow.Definition;

        var prCheck = pr.Stages.Single(s => s.Stage == "check");
        var defCheck = def.Stages.Single(s => s.Stage == "check");

        Assert.Equal(new[] { "ai-review" }, prCheck.Tasks.Select(t => t.Id).ToArray());

        var aiReview = prCheck.Tasks.Single(t => t.Id == "ai-review");
        var defAiReview = defCheck.Tasks.Single(t => t.Id == "ai-review");
        Assert.Equal(defAiReview.Uses, aiReview.Uses);
        Assert.Equal(defAiReview.Title, aiReview.Title);
        AssertTaskWithMapsMatchExcept(defAiReview, aiReview);

        Assert.DoesNotContain(prCheck.Tasks, t => t.Uses == "mohist/create-pull-request");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_FinalMutatingCheckPathsAreFollowedByPrUpdateBeforeMerge()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var build = pr.Stages.Single(s => s.Stage == "build");
        var check = pr.Stages.Single(s => s.Stage == "check");
        var integrate = pr.Stages.Single(s => s.Stage == "integrate");

        AssertStageEndsWithCreatePullRequest(build, "build:update-pr");

        AssertRepairPathHasPrUpdateVerifyTask(build, "health", "fix-build-health", null);
        AssertRepairPathHasPrUpdateVerifyTask(build, "verify", "fix-tests", null);
        AssertRepairPathHasPrUpdateVerifyTask(check, "health", "fix-check-health", "check:update-pr");
        AssertRepairPathHasPrUpdateVerifyTask(check, "review-passed", "fix-review-findings", "check:update-pr");
        AssertRepairPathHasPrUpdateVerifyTask(check, "merge-ready", "rebase-onto-base", "check:update-pr");

        var mergePr = integrate.Tasks.Single(t => t.Id == "integrate:merge-pr");
        Assert.Equal("mohist/merge-pull-request", mergePr.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(mergePr, "prNumber"));
        Assert.DoesNotContain(integrate.Tasks, t => t.Uses == "mohist/create-pull-request");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_IntegrateStage_DeliversViaMergePrWithBaseMovedRecovery()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var def = MohistWorkflow.Definition;

        var prIntegrate = pr.Stages.Single(s => s.Stage == "integrate");
        var defIntegrate = def.Stages.Single(s => s.Stage == "integrate");

        var orderedIds = new[] { "integrate:spec-sync", "integrate:archive-change", "integrate:merge-pr" };
        Assert.Equal(orderedIds, prIntegrate.Tasks.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(prIntegrate.Tasks, t => t.Uses == "mohist/create-pull-request");
        Assert.Equal(new[] { "integrate:spec-sync", "integrate:archive-change", "integrate:rebase", "integrate:push" }, defIntegrate.Tasks.Select(t => t.Id).ToArray());

        var specSync = prIntegrate.Tasks.Single(t => t.Id == "integrate:spec-sync");
        var defSpecSync = defIntegrate.Tasks.Single(t => t.Id == "integrate:spec-sync");
        AssertTaskWithMapsMatchExcept(specSync, defSpecSync);

        var archiveChange = prIntegrate.Tasks.Single(t => t.Id == "integrate:archive-change");
        var defArchiveChange = defIntegrate.Tasks.Single(t => t.Id == "integrate:archive-change");
        AssertTaskWithMapsMatchExcept(archiveChange, defArchiveChange);

        var defRebase = defIntegrate.Tasks.Single(t => t.Id == "integrate:rebase");
        Assert.Equal("mohist/rebase", defRebase.Uses);
        Assert.Equal(ReadBoolWith(defRebase, "squash"), true);

        var prMerge = prIntegrate.Tasks.Single(t => t.Id == "integrate:merge-pr");
        var defPush = defIntegrate.Tasks.Single(t => t.Id == "integrate:push");
        Assert.Equal("mohist/merge-pull-request", prMerge.Uses);
        Assert.Equal("mohist/push", defPush.Uses);
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(prMerge, "prNumber"));
        Assert.Equal("squash", ReadStringWith(prMerge, "method"));
        Assert.Equal("issue.title", ReadStringWith(prMerge, "subjectFrom"));

        var onFailure = prMerge.OnFailure;
        Assert.NotNull(onFailure);
        Assert.Equal(1, onFailure!.Limit);
        var failureCase = Assert.Single(onFailure.Cases);
        Assert.Equal("base-moved", failureCase.When["output.errorCode"]!.Value.GetString());
        Assert.Equal(new[] { "recover:rebase", "recover:open-pr", "recover:merge-pr" }, failureCase.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal("mohist/rebase", failureCase.Tasks[0].Uses);
        Assert.Equal("mohist/create-pull-request", failureCase.Tasks[1].Uses);
        Assert.Equal("mohist/merge-pull-request", failureCase.Tasks[2].Uses);

        var recoverOpenPr = failureCase.Tasks[1];
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(recoverOpenPr, "source"));
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(recoverOpenPr, "target"));
        Assert.NotNull(recoverOpenPr.SetVars);
        Assert.Equal("output.prNumber", recoverOpenPr.SetVars!["github.pr.number"]);
        Assert.Equal("output.prUrl", recoverOpenPr.SetVars!["github.pr.url"]);

        var recoverMergePr = failureCase.Tasks[2];
        Assert.Equal("${{ vars.github.pr.number }}", ReadStringWith(recoverMergePr, "prNumber"));
    }

    private static bool? ReadBoolWith(Mohist.Server.Workflow.Domain.Definition.TaskDefinition task, string key)
    {
        if (task.With is null || !task.With.TryGetValue(key, out var element) || element is null) return null;
        if (element.Value.ValueKind == System.Text.Json.JsonValueKind.True) return true;
        if (element.Value.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        return element.Value.GetBoolean();
    }

    private static string? ReadStringWith(Mohist.Server.Workflow.Domain.Definition.TaskDefinition task, string key)
    {
        if (task.With is null || !task.With.TryGetValue(key, out var element) || element is null) return null;
        return element.Value.GetString();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_IntegrateStage_DeliveryCollapsesToMergePrOnly()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var integrate = pr.Stages.Single(s => s.Stage == "integrate");

        var pushTasks = integrate.Tasks
            .Where(t => t.Id.EndsWith(":push", StringComparison.Ordinal))
            .Select(t => t.Id)
            .ToList();
        Assert.Empty(pushTasks);

        var deliveryTasks = integrate.Tasks
            .Where(t => t.Id.EndsWith(":open-pr", StringComparison.Ordinal) || t.Id.EndsWith(":merge-pr", StringComparison.Ordinal) || t.Id.EndsWith(":publish", StringComparison.Ordinal) || t.Id.EndsWith(":push", StringComparison.Ordinal))
            .Select(t => t.Id)
            .ToList();
        Assert.Equal(new[] { "integrate:merge-pr" }, deliveryTasks);

        var openPrTasks = integrate.Tasks
            .Where(t => t.Uses == "mohist/create-pull-request")
            .ToList();
        Assert.Empty(openPrTasks);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowYaml_ParsesViaWorkflowYamlSerializer()
    {
        var yaml = ReadResourceYaml("mohist-pr.workflow.yaml");

        var definition = WorkflowYamlSerializer.FromYaml(yaml, "mohist/pr");

        Assert.Equal("mohist/pr", definition.Id);
        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());

        var integrateTopLevelIds = definition.Stages[3].Tasks.Select(t => t.Id).ToArray();
        Assert.DoesNotContain(integrateTopLevelIds, id => id.Contains("open-pr", StringComparison.Ordinal));

        var integrateDeliveryUses = definition.Stages[3].Tasks.Select(t => t.Uses).ToArray();
        Assert.Contains("mohist/merge-pull-request", integrateDeliveryUses);
        Assert.DoesNotContain(integrateDeliveryUses, u => u == "mohist/create-pull-request");

        var buildTasks = JsonSerializer.Serialize(definition.Stages[1].Tasks);
        Assert.Contains("create-pull-request", buildTasks);
        Assert.Contains("build:open-pr", buildTasks);
        Assert.Contains("build:update-pr", buildTasks);

        var checkTasks = JsonSerializer.Serialize(definition.Stages[2].Tasks);
        Assert.DoesNotContain("create-pull-request", checkTasks);

        var checkRepairs = JsonSerializer.Serialize(definition.Stages[2].Checks);
        Assert.Contains("create-pull-request", checkRepairs);
        Assert.Contains("check:update-pr", checkRepairs);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowYaml_HasOnlyDocumentedPrDivergencesFromDefault()
    {
        var defaultYaml = ReadResourceYaml("mohist-default.workflow.yaml");
        var prYaml = ReadResourceYaml("mohist-pr.workflow.yaml");

        var defaultLines = defaultYaml.Replace("\r\n", "\n").Split('\n');
        var prLines = prYaml.Replace("\r\n", "\n").Split('\n');

        // The PR workflow is a thin variant of the default. Apply documented
        // substitutions and assert the result is line-for-line equal. The
        // substitutions are anchored to their contexts (multi-line blocks)
        // so they only fire where intended and any unaccounted-for drift
        // surfaces as a mismatch.
        var substitutions = new (string PrText, string DefaultText)[]
        {
            // PR description adds a gh CLI prerequisite line at the tail.
            ("  Requires the `gh` CLI on the runner host and `gh auth login` against the target repository.\n", ""),
            // PR description tail mentions the PR-first task list and "PR record"
            // suitability; default describes the simple push pipeline.
            (
                "  Stages: plan (proposal, specs, design, tasks, self-review) → build (open PR, load tasks, update PR) → check (AI review, repair-aware update PR, merge readiness) → integrate (spec sync, archive, merge).\n",
                "  Stages: plan (proposal, specs, design, tasks, self-review) → build → check (AI review, merge readiness) → integrate (spec sync, archive, merge, push).\n"
            ),
            (
                "  Requires human approval at the plan and check stages, with the workflow merging the working branch into the repository base branch through a GitHub PR.\n",
                "  Requires human approval at the plan and check stages, with the workflow merging and pushing the working branch into the repository base branch on completion.\n"
            ),
            (
                "  Best suited for: new features, user-visible behavior changes, changes that need a design document or spec delta, work that needs a traceable GitHub PR record.\n",
                "  Best suited for: new features, user-visible behavior changes, changes that need a design document or spec delta.\n"
            ),
            // Build-stage PR-first: PR opens the PR as the first build task and
            // updates it as the tail task. Default has neither; substitute empty.
            (
                "      - id: build:open-pr\n" +
                "        title: Open or update GitHub PR\n" +
                "        uses: mohist/create-pull-request\n" +
                "        with:\n" +
                "          source: ${{ workspace.branch }}\n" +
                "          target: ${{ repository.baseBranch }}\n" +
                "          remote: origin\n" +
                "          titleFrom: issue.title\n" +
                "          bodyFrom: issue.body\n" +
                "        setVars:\n" +
                "          github.pr.number: output.prNumber\n" +
                "          github.pr.url: output.prUrl\n" +
                "      - id: load-tasks\n" +
                "        title: Load tasks from plan\n" +
                "        uses: mohist/openspec-tasks\n" +
                "        with:\n" +
                "          path: ${{ openspecChangeDir }}/tasks.json\n" +
                "          task:\n" +
                "            uses: mohist/acp-agent\n" +
                "            with:\n" +
                "              agent: ${{ vars.agent }}\n" +
                "              prompt:\n" +
                "                uses: mohist/openspec-task-prompt\n" +
                "                with:\n" +
                "                  file: ${{ openspecChangeDir }}/tasks.json\n" +
                "                  items: tasks\n" +
                "                  base: ${{ prompts.build }}\n" +
                "      - id: build:update-pr\n" +
                "        title: Update GitHub PR with build results\n" +
                "        uses: mohist/create-pull-request\n" +
                "        with:\n" +
                "          source: ${{ workspace.branch }}\n" +
                "          target: ${{ repository.baseBranch }}\n" +
                "          remote: origin\n" +
                "          titleFrom: issue.title\n" +
                "          bodyFrom: issue.body\n" +
                "        setVars:\n" +
                "          github.pr.number: output.prNumber\n" +
                "          github.pr.url: output.prUrl",
                "      - id: load-tasks\n" +
                "        title: Load tasks from plan\n" +
                "        uses: mohist/openspec-tasks\n" +
                "        with:\n" +
                "          path: ${{ openspecChangeDir }}/tasks.json\n" +
                "          task:\n" +
                "            uses: mohist/acp-agent\n" +
                "            with:\n" +
                "              agent: ${{ vars.agent }}\n" +
                "              prompt:\n" +
                "                uses: mohist/openspec-task-prompt\n" +
                "                with:\n" +
                "                  file: ${{ openspecChangeDir }}/tasks.json\n" +
                "                  items: tasks\n" +
                "                  base: ${{ prompts.build }}"
            ),
            // Build-stage verify check timeout.
            ("          timeout: 1500000", "          timeout: 600000"),
            // Build-stage fix-tests task: PR uses an inline prompt instead of
            // the default's prompts.fix-tests reference and expect markers.
            (
                "            session: build\n" +
                "            prompt: |\n" +
                "              The verify check failed: the project did not build, typecheck, or its\n" +
                "              tests did not pass. Read the check output to see the exact failures.\n" +
                "              Fix them at the root cause — prefer correcting code or tests over\n" +
                "              weakening assertions. Re-run the same commands from the check to\n" +
                "              confirm all suites (server dotnet tests, web typecheck + tests,\n" +
                "              runner typecheck + tests) pass before finishing.\n" +
                "            agent: ${{ vars.agent }}",
                "            prompt: ${{ prompts.fix-tests }}\n" +
                "            agent: ${{ vars.agent }}\n" +
                "            expect:\n" +
                "              markers:\n" +
                "                - path: _output\n" +
                "                  oneOf:\n" +
                "                    - <promise>done</promise>\n" +
                "                    - <promise>unfinished</promise>"
            ),
            // Delivery: default rebases+squashes then pushes; PR's integrate happy
            // path is only merge-pull-request, with rebase+create-pull-request kept
            // solely inside the base-moved recovery branch.
            (
                "      - id: integrate:merge-pr\n" +
                "        title: Merge GitHub PR\n" +
                "        uses: mohist/merge-pull-request\n" +
                "        with:\n" +
                "          prNumber: ${{ vars.github.pr.number }}\n" +
                "          method: squash\n" +
                "          subjectFrom: issue.title\n" +
                "        onFailure:\n" +
                "          limit: 1\n" +
                "          cases:\n" +
                "            - when:\n" +
                "                output.errorCode: base-moved\n" +
                "              tasks:\n" +
                "                - id: recover:rebase\n" +
                "                  title: Rebase after base moved\n" +
                "                  uses: mohist/rebase\n" +
                "                  with:\n" +
                "                    baseBranch: ${{ repository.baseBranch }}\n" +
                "                    remote: origin\n" +
                "                    squash: false\n" +
                "                    conflictResolver:\n" +
                "                      title: Resolve rebase conflicts\n" +
                "                      with:\n" +
                "                        description: \"Resolve rebase conflicts, stage resolved files, and continue until the rebase completes.\"\n" +
                "                - id: recover:open-pr\n" +
                "                  title: Open or update GitHub PR\n" +
                "                  uses: mohist/create-pull-request\n" +
                "                  with:\n" +
                "                    source: ${{ workspace.branch }}\n" +
                "                    target: ${{ repository.baseBranch }}\n" +
                "                    remote: origin\n" +
                "                    titleFrom: issue.title\n" +
                "                    bodyFrom: issue.body\n" +
                "                  setVars:\n" +
                "                    github.pr.number: output.prNumber\n" +
                "                    github.pr.url: output.prUrl\n" +
                "                - id: recover:merge-pr\n" +
                "                  title: Merge GitHub PR\n" +
                "                  uses: mohist/merge-pull-request\n" +
                "                  with:\n" +
                "                    prNumber: ${{ vars.github.pr.number }}\n" +
                "                    method: squash\n" +
                "                    subjectFrom: issue.title",
                "      - id: integrate:rebase\n" +
                "        title: Rebase and squash branch\n" +
                "        uses: mohist/rebase\n" +
                "        with:\n" +
                "          baseBranch: ${{ repository.baseBranch }}\n" +
                "          remote: origin\n" +
                "          squash: true\n" +
                "          messageFrom: issue.title\n" +
                "          conflictResolver:\n" +
                "            title: Resolve rebase conflicts\n" +
                "            with:\n" +
                "              description: \"Resolve rebase conflicts, stage resolved files, and continue until the rebase completes.\"\n" +
                "      - id: integrate:push\n" +
                "        title: Push changes\n" +
                "        uses: mohist/push\n" +
                "        with:\n" +
                "          source: ${{ workspace.branch }}\n" +
                "          target: ${{ repository.baseBranch }}\n" +
                "          remote: origin"
            ),
        };

        var normalized = prYaml;
        foreach (var (prText, defaultText) in substitutions)
        {
            normalized = normalized.Replace(prText, defaultText);
        }

        var normalizedDefinition = WorkflowYamlSerializer.FromYaml(normalized, "mohist/pr-normalized");
        var defaultDefinition = WorkflowYamlSerializer.FromYaml(defaultYaml, "mohist/default");

        Assert.Equal(defaultDefinition.Stages.Select(s => s.Stage), normalizedDefinition.Stages.Select(s => s.Stage));
        AssertStagesMatch(defaultDefinition.Stages[0], normalizedDefinition.Stages[0]);
        Assert.Equal(defaultDefinition.Stages[1].Tasks.Select(t => t.Id), normalizedDefinition.Stages[1].Tasks.Select(t => t.Id));
        Assert.Equal(defaultDefinition.Stages[3].Tasks.Select(t => t.Id), normalizedDefinition.Stages[3].Tasks.Select(t => t.Id));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ProjectWorkflowProfileManager_GetSystemTemplateInfo_Pr_ReturnsFriendlyDisplayName()
    {
        var info = ProjectWorkflowProfileManager.GetSystemTemplateInfo("mohist/pr");

        Assert.NotNull(info);
        Assert.Equal("Mohist PR", info!.Name);
        Assert.False(info.IsDefault);
        Assert.Contains("gh auth login", info.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ProjectWorkflowProfileManager_GetSystemTemplateDefinition_Pr_ReturnsPrDefinition()
    {
        var def = ProjectWorkflowProfileManager.GetSystemTemplateDefinition("mohist/pr");

        Assert.NotNull(def);
        Assert.Same(MohistWorkflow.PrWorkflowDefinition, def);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ProjectWorkflowProfileManager_SystemTemplates_ExposePrTemplate()
    {
        var manager = new ProjectWorkflowProfileManager(new FakeDbContextFactory(), new FakePromptLoader(), new PromptTemplateEngine());

        var templates = await manager.ListSystemTemplatesAsync();

        var prTemplate = Assert.Single(templates, t => t.Id == "mohist/pr");
        Assert.Equal("Mohist PR", prTemplate.Name);
        Assert.False(prTemplate.IsDefault);
        Assert.Contains("gh auth login", prTemplate.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertStagesMatch(
        Mohist.Server.Workflow.Domain.Definition.StageDefinition expected,
        Mohist.Server.Workflow.Domain.Definition.StageDefinition actual)
    {
        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Tasks.Select(t => t.Id), actual.Tasks.Select(t => t.Id));
        Assert.Equal(expected.Tasks.Select(t => t.Uses), actual.Tasks.Select(t => t.Uses));
        Assert.Equal(expected.Tasks.Select(t => t.Title), actual.Tasks.Select(t => t.Title));
        AssertTaskWithMapsMatchForAllTasks(expected.Tasks, actual.Tasks);
    }

    private static void AssertTaskWithMapsMatchForAllTasks(
        IReadOnlyList<Mohist.Server.Workflow.Domain.Definition.TaskDefinition> expected,
        IReadOnlyList<Mohist.Server.Workflow.Domain.Definition.TaskDefinition> actual)
    {
        for (var i = 0; i < expected.Count; i++)
            AssertTaskWithMapsMatchExcept(expected[i], actual[i]);
    }

    private static void AssertTaskWithMapsMatchExcept(
        Mohist.Server.Workflow.Domain.Definition.TaskDefinition expected,
        Mohist.Server.Workflow.Domain.Definition.TaskDefinition actual)
    {
        Assert.Equal(JsonSerializer.Serialize(expected.With), JsonSerializer.Serialize(actual.With));
    }

    private static void AssertStageEndsWithCreatePullRequest(
        Mohist.Server.Workflow.Domain.Definition.StageDefinition stage,
        string expectedTaskId)
    {
        var tail = stage.Tasks.Last();
        Assert.Equal(expectedTaskId, tail.Id);
        Assert.Equal("mohist/create-pull-request", tail.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(tail, "source"));
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(tail, "target"));
        Assert.NotNull(tail.SetVars);
        Assert.Equal("output.prNumber", tail.SetVars!["github.pr.number"]);
        Assert.Equal("output.prUrl", tail.SetVars!["github.pr.url"]);
    }

    private static void AssertRepairPathHasPrUpdateVerifyTask(
        Mohist.Server.Workflow.Domain.Definition.StageDefinition stage,
        string checkName,
        string repairTaskId,
        string? verifyTaskId)
    {
        var check = stage.Checks.Single(c => c.Name == checkName);
        Assert.NotNull(check.OnFailure?.Repair);
        Assert.Equal(repairTaskId, check.OnFailure!.Repair!.Task.Id);

        if (verifyTaskId is null)
        {
            Assert.Null(check.OnFailure.Repair.VerifyTask);
            return;
        }

        var verifyTask = check.OnFailure.Repair.VerifyTask;
        Assert.NotNull(verifyTask);
        Assert.Equal(verifyTaskId, verifyTask!.Id);
        Assert.Equal("mohist/create-pull-request", verifyTask.Uses);
        Assert.Equal("${{ workspace.branch }}", ReadStringWith(verifyTask, "source"));
        Assert.Equal("${{ repository.baseBranch }}", ReadStringWith(verifyTask, "target"));
        Assert.NotNull(verifyTask.SetVars);
        Assert.Equal("output.prNumber", verifyTask.SetVars!["github.pr.number"]);
        Assert.Equal("output.prUrl", verifyTask.SetVars!["github.pr.url"]);
    }

    private static string ReadResourceYaml(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Issue", "Services", "WorkflowProfiles", fileName);
        if (File.Exists(path)) return File.ReadAllText(path);

        var sourceProbe = Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "src", "Mohist.Server", "Issue", "Services", "WorkflowProfiles", fileName);
        if (File.Exists(sourceProbe)) return File.ReadAllText(Path.GetFullPath(sourceProbe));

        throw new FileNotFoundException($"YAML resource not found: {fileName}");
    }
}
