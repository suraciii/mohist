using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

/// <summary>
/// issue-474 T-002: Effective Workflow Variables resolve from Project,
/// Issue, and WorkflowRun resources only. Profile and Definition parsing
/// (including live stage reads) MUST NOT extract or merge embedded
/// Variables. The WorkflowRun resource additionally seeds
/// <c>vars.archive = ""</c> as a marked initialization default on creation
/// so built-in workflows can dispatch without explicit archive
/// configuration; an explicit Project/Issue/Run write (including
/// <c>setVars</c>) clears that marker and follows the standard
/// top-level / selected-stage precedence rules.
///
/// The fixture builds a fresh migrated SQLite database per spec and seeds
/// Project, Issue, WorkflowRun rows directly through
/// <see cref="MohistDbContext"/> to keep tests focused on resolution
/// behavior rather than dispatch plumbing. No real time or external
/// services are touched.
/// </summary>
public class WorkflowVariableResolutionDefaultsSpecs : WorkflowProfileManagerTestFactory
{
    [Fact]
    public async Task EffectiveVariables_IgnoreDefinitionVariables()
    {
        var runId = "wr_no_embed01";
        var templateJson = SerializeDefinition("no-embed-template");
        await SeedAllLayersAsync(
            "proj_no_embed", 1, runId,
            project: VariableBundle.Empty,
            issue: VariableBundle.Empty,
            issueTemplateJson: templateJson);

        var resolved = await Manager.ResolveLayeredVariablesAsync(runId);

        Assert.Null(resolved.Vars);
        Assert.Null(resolved.Stages);
        Assert.Null(resolved.DefaultVars);
        Assert.Null(resolved.DefaultStages);
    }

    [Fact]
    public async Task EffectiveVariables_PreserveScopeAndStagePrecedence()
    {
        var runId = "wr_scope_stage01";
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "project-model", variant = "project-variant" },
                shared = "project",
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    agent = new { variant = "project-stage-variant" },
                    stageShared = "project-stage",
                }))),
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "issue-model" },
                shared = "issue",
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    agent = new { model = "issue-stage-model" },
                }))),
            });
        var run = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                shared = "run",
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    stageShared = "run-stage",
                }))),
            });

        await SeedAllLayersAsync(
            "proj_scope_stage", 1, runId,
            project: project,
            issue: issue,
            runtime: run);

        var stageResult = await Manager.ResolveEffectiveVariablesAsync(runId, "build");
        var topResult = await Manager.ResolveEffectiveVariablesAsync(runId, null);

        using (var topDoc = JsonDocument.Parse(topResult.GetRawText()))
        {
            Assert.Equal("run", topDoc.RootElement.GetProperty("shared").GetString());
            var topAgent = topDoc.RootElement.GetProperty("agent");
            Assert.Equal("issue-model", topAgent.GetProperty("model").GetString());
            Assert.Equal("project-variant", topAgent.GetProperty("variant").GetString());
        }

        using (var stageDoc = JsonDocument.Parse(stageResult.GetRawText()))
        {
            var stageAgent = stageDoc.RootElement.GetProperty("agent");
            Assert.Equal("issue-stage-model", stageAgent.GetProperty("model").GetString());
            Assert.Equal("project-stage-variant", stageAgent.GetProperty("variant").GetString());
            Assert.Equal("run-stage", stageDoc.RootElement.GetProperty("stageShared").GetString());
            Assert.Equal("run", stageDoc.RootElement.GetProperty("shared").GetString());
        }
    }

    [Fact]
    public async Task EnsureArchiveDefault_SeedsMarkedDefaultOnlyWhenArchiveAbsent()
    {
        var runId = "wr_archive_seed01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var manager = new WorkflowRunProfileManager(dbFactory);

        await manager.EnsureArchiveDefaultAsync(runId);

        var explicitBundle = await manager.GetVariablesAsync(runId);
        var defaults = await manager.GetDefaultVariablesAsync(runId);

        Assert.True(defaults.HasDefaultContent);
        Assert.NotNull(defaults.DefaultVars);
        Assert.Equal(string.Empty, defaults.DefaultVars!.Value.GetProperty("archive").GetString());
        Assert.Null(explicitBundle.Vars);

        await manager.EnsureArchiveDefaultAsync(runId);
        await manager.EnsureArchiveDefaultAsync(runId);

        var defaultsAfter = await manager.GetDefaultVariablesAsync(runId);
        Assert.Equal(string.Empty, defaultsAfter.DefaultVars!.Value.GetProperty("archive").GetString());
        var explicitAfter = await manager.GetVariablesAsync(runId);
        Assert.Null(explicitAfter.Vars);
    }

    [Fact]
    public async Task EnsureArchiveDefault_DoesNotOverwriteExplicitArchiveValue()
    {
        var runId = "wr_archive_seed02";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var manager = new WorkflowRunProfileManager(dbFactory);

        await manager.SetVariablesAsync(
            runId,
            new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    archive = "/explicit/path",
                }))));

        await manager.EnsureArchiveDefaultAsync(runId);

        var explicitBundle = await manager.GetVariablesAsync(runId);
        var defaults = await manager.GetDefaultVariablesAsync(runId);
        Assert.Equal("/explicit/path", explicitBundle.Vars!.Value.GetProperty("archive").GetString());
        Assert.True(defaults.DefaultVars is null
            || !defaults.DefaultVars.Value.TryGetProperty("archive", out _));
    }

    [Fact]
    public async Task WorkflowRunProfile_EnforcesOptimisticConcurrencyOnOverlappingWrites()
    {
        // Issue-474 review: WorkflowRunProfileRow carries an ETag concurrency
        // token so a stale-snapshot write cannot clobber the latest row. Two
        // contexts both read ETag=1; the first save bumps the row to ETag=2;
        // the second save's WHERE ETag=1 then matches no row and raises
        // DbUpdateConcurrencyException. The ETag is bumped on both sides to
        // mirror what every real writer (WorkflowRunProfileManager) does, so
        // this isolates the concurrency check rather than the increment.
        var runId = "wr_etag_race01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var manager = new WorkflowRunProfileManager(dbFactory);
        await manager.EnsureArchiveDefaultAsync(runId);

        await using var readerA = new MohistDbContext(Database.Options);
        await using var readerB = new MohistDbContext(Database.Options);
        var rowA = await readerA.WorkflowRunProfiles.FirstAsync(x => x.WorkflowRunId == runId);
        var rowB = await readerB.WorkflowRunProfiles.FirstAsync(x => x.WorkflowRunId == runId);

        rowA.Variables = JsonSerializer.Serialize(new { archive = "/a/wins" });
        BumpETagInContext(readerA, rowA);
        await readerA.SaveChangesAsync();

        rowB.Variables = JsonSerializer.Serialize(new { archive = "/b/overwrites" });
        BumpETagInContext(readerB, rowB);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => readerB.SaveChangesAsync());
    }

    private static void BumpETagInContext(MohistDbContext db, WorkflowRunProfileRow row)
    {
        var etag = db.Entry(row).Property<long>("ETag");
        etag.CurrentValue = etag.OriginalValue + 1;
    }

    [Fact]
    public async Task ResolveEffectiveVariables_MarkedDefaultResolvesBelowExplicitProjectIssueAndStage()
    {
        var runId = "wr_default_below01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var runManager = new WorkflowRunProfileManager(dbFactory);
        await runManager.EnsureArchiveDefaultAsync(runId);

        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "/project/archive",
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    archive = "/project/build/archive",
                }))),
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "/issue/archive",
            })));
        await SeedAllLayersAsync(
            "proj_default_below", 1, runId,
            project: project,
            issue: issue);

        var topResult = await Manager.ResolveEffectiveVariablesAsync(runId, null);
        using (var topDoc = JsonDocument.Parse(topResult.GetRawText()))
        {
            Assert.Equal("/issue/archive", topDoc.RootElement.GetProperty("archive").GetString());
        }

        var stageResult = await Manager.ResolveEffectiveVariablesAsync(runId, "build");
        using (var stageDoc = JsonDocument.Parse(stageResult.GetRawText()))
        {
            Assert.Equal("/project/build/archive", stageDoc.RootElement.GetProperty("archive").GetString());
        }
    }

    [Fact]
    public async Task ResolveEffectiveVariables_MarkedDefaultWinsWhenNothingElseProvidesArchive()
    {
        var runId = "wr_default_wins01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var runManager = new WorkflowRunProfileManager(dbFactory);
        await runManager.EnsureArchiveDefaultAsync(runId);

        await SeedAllLayersAsync(
            "proj_default_wins", 1, runId,
            project: VariableBundle.Empty,
            issue: VariableBundle.Empty);

        var result = await Manager.ResolveEffectiveVariablesAsync(runId, null);
        using var doc = JsonDocument.Parse(result.GetRawText());
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("archive").GetString());
    }

    [Fact]
    public async Task PatchRunVariables_ClearsMarkedDefaultAndWritesAsExplicit()
    {
        var runId = "wr_replace_default01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var runManager = new WorkflowRunProfileManager(dbFactory);
        await runManager.EnsureArchiveDefaultAsync(runId);

        var patch = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                archive = "/run/archive",
            })));
        await runManager.PatchVariablesAsync(runId, patch);

        var defaults = await runManager.GetDefaultVariablesAsync(runId);
        var explicitBundle = await runManager.GetVariablesAsync(runId);
        Assert.True(defaults.DefaultVars is null
            || !defaults.DefaultVars.Value.TryGetProperty("archive", out _));
        Assert.Equal("/run/archive", explicitBundle.Vars!.Value.GetProperty("archive").GetString());

        await SeedAllLayersAsync(
            "proj_replace_default", 1, runId,
            project: VariableBundle.Empty,
            issue: VariableBundle.Empty);

        var result = await Manager.ResolveEffectiveVariablesAsync(runId, null);
        using var doc = JsonDocument.Parse(result.GetRawText());
        Assert.Equal("/run/archive", doc.RootElement.GetProperty("archive").GetString());
    }

    [Fact]
    public async Task PatchRunVariables_PreservesDefaultsForKeysItDoesNotTouch()
    {
        // Issue-474 review: PatchVariablesAsync must derive the cleared
        // defaults from the row read under the same ETag snapshot as the
        // write, not from a detached snapshot taken before it. A PATCH that
        // does not touch archive must leave a seeded archive default intact
        // even though the patch was computed against a snapshot.
        var runId = "wr_patch_preserves_default01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var runManager = new WorkflowRunProfileManager(dbFactory);
        await runManager.EnsureArchiveDefaultAsync(runId);

        await runManager.PatchVariablesAsync(
            runId,
            new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    unrelated = "patched",
                }))));

        var defaults = await runManager.GetDefaultVariablesAsync(runId);
        Assert.Equal(string.Empty, defaults.DefaultVars!.Value.GetProperty("archive").GetString());

        var explicitBundle = await runManager.GetVariablesAsync(runId);
        Assert.Equal("patched", explicitBundle.Vars!.Value.GetProperty("unrelated").GetString());
        Assert.False(explicitBundle.Vars.Value.TryGetProperty("archive", out _));
    }

    [Fact]
    public async Task PutRunVariables_ClearsMarkedDefaultAndWins()
    {
        var runId = "wr_put_replace01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var runManager = new WorkflowRunProfileManager(dbFactory);
        await runManager.EnsureArchiveDefaultAsync(runId);

        await runManager.SetVariablesAsync(
            runId,
            new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    archive = "/put/archive",
                    extra = "kept",
                }))));

        var defaults = await runManager.GetDefaultVariablesAsync(runId);
        var explicitBundle = await runManager.GetVariablesAsync(runId);
        Assert.True(defaults.DefaultVars is null
            || !defaults.DefaultVars.Value.TryGetProperty("archive", out _));
        Assert.Equal("/put/archive", explicitBundle.Vars!.Value.GetProperty("archive").GetString());
        Assert.Equal("kept", explicitBundle.Vars!.Value.GetProperty("extra").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ReadLatestRunArchive_AcrossReReads()
    {
        var runId = "wr_retry_archive01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var runManager = new WorkflowRunProfileManager(dbFactory);
        await runManager.EnsureArchiveDefaultAsync(runId);

        await SeedAllLayersAsync(
            "proj_retry_archive", 1, runId,
            project: VariableBundle.Empty,
            issue: VariableBundle.Empty);

        var beforeWrite = await Manager.ResolveEffectiveVariablesAsync(runId, null);
        using (var beforeDoc = JsonDocument.Parse(beforeWrite.GetRawText()))
        {
            Assert.Equal(string.Empty, beforeDoc.RootElement.GetProperty("archive").GetString());
        }

        await runManager.PatchVariablesAsync(
            runId,
            new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    archive = "/post-setvars/archive",
                }))));

        var afterWrite = await Manager.ResolveEffectiveVariablesAsync(runId, null);
        using var afterDoc = JsonDocument.Parse(afterWrite.GetRawText());
        Assert.Equal("/post-setvars/archive", afterDoc.RootElement.GetProperty("archive").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_IgnoreGlobalConfigBundle()
    {
        var runId = "wr_global_ignore01";
        var configService = WorkflowGrainTestHelpers.CreateEmptyConfigService();
        var configBundle = await configService.GetVariables();
        Assert.True(configBundle.Vars is null && configBundle.Stages is null);

        await SeedAllLayersAsync(
            "proj_global_ignore", 1, runId,
            project: VariableBundle.Empty,
            issue: VariableBundle.Empty);

        var result = await Manager.ResolveLayeredVariablesAsync(runId);
        Assert.True(result.Vars is null
            || !result.Vars.Value.EnumerateObject().Any());
    }

    [Fact]
    public async Task EnsureArchiveDefault_SurvivesRepeatedStartAttempts()
    {
        var runId = "wr_archive_idempotent01";
        var dbFactory = new TestDbContextFactory(Database.Options);
        var runManager = new WorkflowRunProfileManager(dbFactory);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await runManager.EnsureArchiveDefaultAsync(runId);
        }

        var defaults = await runManager.GetDefaultVariablesAsync(runId);
        var defaultKeys = defaults.DefaultVars!.Value.EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        Assert.Single(defaultKeys);
        Assert.Equal("archive", defaultKeys[0]);
        Assert.Equal(string.Empty, defaults.DefaultVars!.Value.GetProperty("archive").GetString());
    }

    [Fact]
    public async Task IssueProfileStartContext_SeedsAgentWhenAbsent_AndPreservesExplicitAgent()
    {
        var dbFactory = new TestDbContextFactory(Database.Options);
        var issueManager = new IssueWorkflowProfileManager(dbFactory);

        await SeedIssueOnly(
            "proj_issue_seed", 1, "wr_issue_seed01",
            IssueBundleWithAgent(model: "anthropic/claude-sonnet-4-6"));

        var existing = await issueManager.GetVariablesAsync("proj_issue_seed", 1);
        Assert.Equal("anthropic/claude-sonnet-4-6",
            existing.Vars!.Value.GetProperty("agent").GetProperty("model").GetString());

        var ctxBundle = IssueVariableBuilder.BuildContextBundle(
            "wr_issue_seed01",
            NewTestIssue(),
            NewProjectContext(),
            NewWorkspace(),
            existing);
        var merged = VariableBundle.Patch(existing, ctxBundle);
        await issueManager.SetVariablesAsync("proj_issue_seed", 1, merged);

        var after = await issueManager.GetVariablesAsync("proj_issue_seed", 1);
        Assert.Equal("anthropic/claude-sonnet-4-6",
            after.Vars!.Value.GetProperty("agent").GetProperty("model").GetString());

        await issueManager.SetVariablesAsync(
            "proj_issue_seed", 1,
            new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    issue = new { number = 1 },
                }))));

        var noAgent = await issueManager.GetVariablesAsync("proj_issue_seed", 1);
        var seedBundle = IssueVariableBuilder.BuildContextBundle(
            "wr_issue_seed01",
            NewTestIssue(),
            NewProjectContext(),
            NewWorkspace(),
            noAgent);
        var seeded = VariableBundle.Patch(noAgent, seedBundle);
        await issueManager.SetVariablesAsync("proj_issue_seed", 1, seeded);

        var final = await issueManager.GetVariablesAsync("proj_issue_seed", 1);
        Assert.Equal(JsonValueKind.Object, final.Vars!.Value.GetProperty("agent").ValueKind);
        Assert.Empty(final.Vars!.Value.GetProperty("agent").EnumerateObject());
    }

    private async Task SeedIssueOnly(
        string projectId,
        int issueNumber,
        string runId,
        VariableBundle issueBundle)
    {
        await using var db = new MohistDbContext(Database.Options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = "{}",
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Variables = issueBundle.ToJson(),
        });
        await db.SaveChangesAsync();
    }

    private static VariableBundle IssueBundleWithAgent(string model) =>
        new(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model },
            })));

    private static Mohist.Server.Issue.Domain.Issue NewTestIssue() => new()
    {
        ProjectId = "proj_issue_seed",
        Number = 1,
        Title = "Issue seed spec",
        Priority = "p2",
    };

    private static WorkflowProjectContext NewProjectContext() =>
        new(
            Id: "proj_issue_seed",
            Name: "spec",
            RepositoryName: "master",
            RepositoryGitUrl: null,
            RepositoryBaseBranch: "master");

    private static WorkspaceIdentity NewWorkspace() =>
        new(
            Path: "/tmp/mohist/spec/wr_issue_seed01",
            Branch: "mohist/run-wr_issue_seed01",
            ChangeDir: "openspec/changes/issue-1");
}
