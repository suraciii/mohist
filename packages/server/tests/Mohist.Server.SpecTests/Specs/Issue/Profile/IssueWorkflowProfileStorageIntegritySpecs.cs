using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public class IssueWorkflowProfileStorageIntegritySpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly TestDbContextFactory _factory;

    public IssueWorkflowProfileStorageIntegritySpecs()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _factory = new TestDbContextFactory(_database.Options);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Verify_EmptyDatasetIsHealthy()
    {
        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.Equal(0, report.Scanned);
        Assert.True(report.IsHealthy);
        Assert.Empty(report.UnreachableIssues);
    }

    [Fact]
    public async Task Verify_ReportsIssueScopedAgentPaths()
    {
        await SeedProfileAsync("project_1", 1, """{"vars":{"agent":{"model":"gpt-5"}}}""");
        await SeedProfileAsync("project_1", 2, """{"stages":{"build":{"vars":{"agent":{"model":"gpt-5"}}}}}""");

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.True(report.IsHealthy);
        Assert.Contains(report.Rows, row => row is { ProjectId: "project_1", IssueNumber: 1, AgentPath: "vars.agent" });
        Assert.Contains(report.Rows, row => row is { ProjectId: "project_1", IssueNumber: 2, AgentPath: "stages.build.vars.agent" });
    }

    [Fact]
    public void InspectVariables_RequiresScopedIdentity()
    {
        Assert.Throws<ArgumentException>(() => IssueWorkflowProfileStorageIntegrity.InspectVariables("", 1, "{}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => IssueWorkflowProfileStorageIntegrity.InspectVariables("project_1", 0, "{}"));
    }

    [Fact]
    public async Task DefensiveCopy_WritesAgentDataToTheScopedProfile()
    {
        await SeedProfileAsync("project_1", 7, "{}");

        await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "project_1",
            7,
            new Dictionary<string, object?> { ["model"] = "gpt-5" },
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["build"] = new() { ["model"] = "gpt-5-mini" },
            });

        await using var db = new MohistDbContext(_database.Options);
        var row = await db.IssueWorkflowProfiles.SingleAsync(profile =>
            profile.ProjectId == "project_1" && profile.IssueNumber == 7);
        using var document = JsonDocument.Parse(row.Variables);
        Assert.Equal("gpt-5", document.RootElement.GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("gpt-5-mini", document.RootElement.GetProperty("stages").GetProperty("build").GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task DefensiveCopy_RejectsMissingScopedProfile()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
                _factory,
                "project_1",
                1,
                new Dictionary<string, object?> { ["model"] = "gpt-5" },
                null));
    }

    [Fact]
    public async Task DefensiveCopy_PreservesPersistedLegacyKeys_NoRewrite()
    {
        // Per #410 T-002 spec: an already-persisted vars.agent carrying
        // legacy `type` / `liveness*` keys MUST NOT be mutated by the
        // storage-integrity defensive-copy path. FoldAgentDataIntoBundle
        // is the read-in chokepoint; legacy keys in storage carry through
        // untouched. The mohist/opencode runtime's unknownKeys diagnostic
        // path covers them when they reach an execution request.
        var legacy = """
        {
          "vars": { "agent": { "type": "opencode", "livenessQuietThresholdMs": 1200000, "probeTimeoutMs": 30000, "model": "gpt-5.6" } },
          "stages": { "build": { "vars": { "agent": { "type": "opencode", "model": "gpt-5-mini" } } } }
        }
        """;
        await SeedProfileAsync("project_legacy", 1, legacy);

        await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "project_legacy",
            1,
            new Dictionary<string, object?> { ["variant"] = "high" },
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["build"] = new() { ["variant"] = "low" },
            });

        await using var db = new MohistDbContext(_database.Options);
        var row = await db.IssueWorkflowProfiles.SingleAsync(profile =>
            profile.ProjectId == "project_legacy" && profile.IssueNumber == 1);
        using var document = JsonDocument.Parse(row.Variables);

        // Root agent: legacy keys are still present, not stripped; variant
        // was added to the converged surface.
        var rootAgent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", rootAgent.GetProperty("type").GetString());
        Assert.Equal(1200000, rootAgent.GetProperty("livenessQuietThresholdMs").GetInt32());
        Assert.Equal(30000, rootAgent.GetProperty("probeTimeoutMs").GetInt32());
        Assert.Equal("gpt-5.6", rootAgent.GetProperty("model").GetString());
        Assert.Equal("high", rootAgent.GetProperty("variant").GetString());

        // Stage agent: legacy keys are still present.
        var buildAgent = document.RootElement.GetProperty("stages").GetProperty("build").GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", buildAgent.GetProperty("type").GetString());
        Assert.Equal("gpt-5-mini", buildAgent.GetProperty("model").GetString());
        Assert.Equal("low", buildAgent.GetProperty("variant").GetString());
    }

    [Fact]
    public void FoldAgentDataIntoBundle_FiltersLegacyKeysFromIncomingOverlay()
    {
        // The defensive-copy helper projects incoming agent data down to
        // the converged {model, variant} whitelist (per D5): legacy
        // ACP/liveness keys supplied on write do NOT enter vars.agent.
        var baseBundle = VariableBundle.Empty;

        var result = IssueWorkflowProfileStorageIntegrity.FoldAgentDataIntoBundle(
            baseBundle,
            new Dictionary<string, object?>
            {
                ["model"] = "openai/gpt-5.6",
                ["variant"] = "high",
                ["type"] = "opencode",
                ["livenessQuietThresholdMs"] = 1200000,
                ["probeTimeoutMs"] = 30000,
            },
            stageAgentConfigs: null);

        using var document = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = document.RootElement.GetProperty("agent");
        Assert.Equal("openai/gpt-5.6", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
        Assert.False(agent.TryGetProperty("type", out _));
        Assert.False(agent.TryGetProperty("livenessQuietThresholdMs", out _));
        Assert.False(agent.TryGetProperty("probeTimeoutMs", out _));
    }

    [Fact]
    public void FoldAgentDataIntoBundle_FiltersLegacyKeysFromIncomingStageOverlay()
    {
        var baseBundle = VariableBundle.Empty;

        var result = IssueWorkflowProfileStorageIntegrity.FoldAgentDataIntoBundle(
            baseBundle,
            agentConfig: null,
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["build"] = new()
                {
                    ["model"] = "openai/gpt-5.6",
                    ["type"] = "opencode",
                    ["compaction"] = new { strategy = "truncate" },
                },
            });

        Assert.NotNull(result.Stages);
        var buildAgent = result.Stages!["build"].Vars;
        Assert.NotNull(buildAgent);
        using var document = JsonDocument.Parse(buildAgent.Value.GetRawText());
        var agent = document.RootElement.GetProperty("agent");
        Assert.Equal("openai/gpt-5.6", agent.GetProperty("model").GetString());
        Assert.False(agent.TryGetProperty("type", out _));
        Assert.False(agent.TryGetProperty("compaction", out _));
    }

    private async Task SeedProfileAsync(string projectId, int issueNumber, string variables)
    {
        await using var db = new MohistDbContext(_database.Options);
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Variables = variables,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

}
