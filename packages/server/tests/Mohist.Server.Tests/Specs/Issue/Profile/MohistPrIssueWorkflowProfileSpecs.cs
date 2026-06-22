using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Tests.Support;
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
    public void PrWorkflowDefinition_PlanBuildCheckStages_MatchDefaultTaskForTask()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var def = MohistWorkflow.Definition;

        AssertStagesMatch(def.Stages[0], pr.Stages[0]);
        AssertStagesMatch(def.Stages[1], pr.Stages[1]);
        AssertStagesMatch(def.Stages[2], pr.Stages[2]);

        Assert.Equal(def.Stages[0].RequiresApproval, pr.Stages[0].RequiresApproval);
        Assert.Equal(def.Stages[2].RequiresApproval, pr.Stages[2].RequiresApproval);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowDefinition_IntegrateStage_DiffersOnlyInPublishAction()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var def = MohistWorkflow.Definition;

        var prIntegrate = pr.Stages.Single(s => s.Stage == "integrate");
        var defIntegrate = def.Stages.Single(s => s.Stage == "integrate");

        var orderedIds = new[] { "integrate:spec-sync", "integrate:archive-change", "integrate:rebase", "integrate:publish" };
        Assert.Equal(orderedIds, prIntegrate.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "integrate:spec-sync", "integrate:archive-change", "integrate:rebase", "integrate:push" }, defIntegrate.Tasks.Select(t => t.Id).ToArray());

        var specSync = prIntegrate.Tasks.Single(t => t.Id == "integrate:spec-sync");
        var defSpecSync = defIntegrate.Tasks.Single(t => t.Id == "integrate:spec-sync");
        AssertTaskWithMapsMatchExcept(specSync, defSpecSync);

        var archiveChange = prIntegrate.Tasks.Single(t => t.Id == "integrate:archive-change");
        var defArchiveChange = defIntegrate.Tasks.Single(t => t.Id == "integrate:archive-change");
        AssertTaskWithMapsMatchExcept(archiveChange, defArchiveChange);

        var prRebase = prIntegrate.Tasks.Single(t => t.Id == "integrate:rebase");
        var defRebase = defIntegrate.Tasks.Single(t => t.Id == "integrate:rebase");
        Assert.Equal("mohist/rebase", prRebase.Uses);
        Assert.Equal("mohist/rebase", defRebase.Uses);
        Assert.Equal(ReadBoolWith(prRebase, "squash"), false);
        Assert.Equal(ReadBoolWith(defRebase, "squash"), true);
        Assert.False(HasWithKey(prRebase, "message"));
        Assert.True(HasWithKey(defRebase, "message"));

        var prPublish = prIntegrate.Tasks.Single(t => t.Id == "integrate:publish");
        var defPush = defIntegrate.Tasks.Single(t => t.Id == "integrate:push");
        Assert.Equal("mohist/publish-via-pr", prPublish.Uses);
        Assert.Equal("mohist/push", defPush.Uses);
        Assert.Equal(ReadStringWith(prPublish, "source"), ReadStringWith(defPush, "source"));
        Assert.Equal(ReadStringWith(prPublish, "target"), ReadStringWith(defPush, "target"));
        Assert.Equal(ReadStringWith(prPublish, "remote"), ReadStringWith(defPush, "remote"));
    }

    private static bool HasWithKey(Mohist.Server.Workflow.Domain.Definition.TaskDefinition task, string key)
    {
        return task.With != null && task.With.ContainsKey(key) && task.With[key] != null;
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
    public void PrWorkflowDefinition_IntegrateStage_PreservesSinglePushOwnerInvariant()
    {
        var pr = MohistWorkflow.PrWorkflowDefinition;
        var integrate = pr.Stages.Single(s => s.Stage == "integrate");

        var pushTasks = integrate.Tasks
            .Where(t => t.Id.EndsWith(":push", StringComparison.Ordinal))
            .Select(t => t.Id)
            .ToList();
        Assert.Empty(pushTasks);

        var deliveryTasks = integrate.Tasks
            .Where(t => t.Id.EndsWith(":publish", StringComparison.Ordinal) || t.Id.EndsWith(":push", StringComparison.Ordinal))
            .Select(t => t.Id)
            .ToList();
        Assert.Equal(new[] { "integrate:publish" }, deliveryTasks);
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
        Assert.Contains("publish-via-pr", JsonSerializer.Serialize(definition.Stages[3].Tasks));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PrWorkflowYaml_IsByteIdenticalToDefault_ModuloPublishAction()
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
            // Integrate-stage rebase: PR does not local-squash (squash happens at
            // GitHub merge time) and has no `message:` (subject is set by gh).
            (
                "        title: Rebase branch\n" +
                "        uses: mohist/rebase\n" +
                "        with:\n" +
                "          baseBranch: ${{ repository.baseBranch }}\n" +
                "          remote: origin\n" +
                "          squash: false\n" +
                "          conflictResolver:",
                "        title: Rebase and squash branch\n" +
                "        uses: mohist/rebase\n" +
                "        with:\n" +
                "          baseBranch: ${{ repository.baseBranch }}\n" +
                "          remote: origin\n" +
                "          squash: true\n" +
                "          message: \"Complete issue #${{ issue.number }}\"\n" +
                "          conflictResolver:"
            ),
            // Delivery: default uses mohist/push (FF); PR uses mohist/publish-via-pr.
            (
                "      - id: integrate:publish\n" +
                "        title: Publish changes\n" +
                "        uses: mohist/publish-via-pr\n" +
                "        with:\n" +
                "          source: ${{ workspace.branch }}\n" +
                "          target: ${{ repository.baseBranch }}\n" +
                "          remote: origin\n" +
                "          message: \"Complete issue #${{ issue.number }}\"",
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

        var normalizedLines = normalized.Replace("\r\n", "\n").TrimEnd().Split('\n');
        var defaultNormalized = defaultYaml.Replace("\r\n", "\n").TrimEnd().Split('\n');

        Assert.Equal(defaultNormalized.Length, normalizedLines.Length);
        for (var i = 0; i < defaultNormalized.Length; i++)
        {
            if (defaultNormalized[i] == normalizedLines[i]) continue;
            Assert.Fail($"Line {i + 1} differs after normalization:\n  default: {defaultNormalized[i]}\n  pr→default: {normalizedLines[i]}");
        }
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
