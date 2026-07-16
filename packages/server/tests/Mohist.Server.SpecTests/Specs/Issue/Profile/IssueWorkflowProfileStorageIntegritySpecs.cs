using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

using DomainIssue = Mohist.Server.Issue.Domain.Issue;

/// <summary>
/// Storage-integrity verification for <c>IssueWorkflowProfile</c> rows.
///
/// These specs implement the migration requirement as a verification with a
/// reversible defensive-copy fallback (per issue-121 design.md / T-004).
/// Persistence is already unified, so on the day-1 dataset verification
/// is a no-op that passes without mutating any row. The defensive copy path
/// is covered by synthetic-row specs that simulate the unlikely case where
/// agent data would need to be folded into <c>Variables</c>.
/// </summary>
public class IssueWorkflowProfileStorageIntegritySpecs : IAsyncLifetime
{
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly TestFactory _factory;
    private readonly SqliteConnection _keeper;
    private int _nextIssueNumber;

    public IssueWorkflowProfileStorageIntegritySpecs()
    {
        var connectionString = $"Data Source=issue-profile-integrity-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        _factory = new TestFactory(_options);

        MigratedSqliteTemplate.CopyTo(_keeper);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    // ===================== Verification =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Verify_EmptyDataset_ReportsHealthy()
    {
        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.Equal(0, report.Scanned);
        Assert.True(report.IsHealthy);
        Assert.Empty(report.UnreachableIssueIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Verify_RowsWithAgentInVars_AreReachable()
    {
        await SeedRowAsync("issue_with_agent", BundleJson(new
        {
            agent = new { model = "gpt-4o", type = "opencode" }
        }));

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.Equal(1, report.Scanned);
        Assert.True(report.IsHealthy);
        var row = Assert.Single(report.Rows);
        Assert.True(row.Reachable);
        Assert.Equal("vars.agent", row.AgentPath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Verify_RowsWithStageAgent_AreReachable()
    {
        await SeedRowAsync("issue_with_stage_agent", BundleJson(
            vars: null,
            stages: new
            {
                build = new
                {
                    vars = new
                    {
                        agent = new { model = "claude-3-5" }
                    }
                }
            }));

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.Equal(1, report.Scanned);
        Assert.True(report.IsHealthy);
        var row = Assert.Single(report.Rows);
        Assert.True(row.Reachable);
        Assert.Equal("stages.build.vars.agent", row.AgentPath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Verify_RowWithEmptyVariables_IsReachable()
    {
        await SeedRowAsync("issue_empty", "{}");

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.True(report.IsHealthy);
        var row = Assert.Single(report.Rows);
        Assert.True(row.Reachable);
        Assert.Null(row.AgentPath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Verify_DayOneDataset_PassesWithoutMutatingRows()
    {
        // Simulate a day-1 dataset: every row already has its agent data in Variables.
        await SeedRowAsync("issue_d1_a",
            BundleJson(new { agent = new { model = "gpt-4o" } }));
        await SeedRowAsync("issue_d1_b",
            BundleJson(new
            {
                agent = new { model = "claude" },
            },
            stages: new
            {
                check = new { vars = new { agent = new { model = "haiku" } } }
            }));
        await SeedRowAsync("issue_d1_c", "{}");

        // Snapshot the rows before verification.
        var before = await SnapshotRowsAsync("issue_d1_a", "issue_d1_b", "issue_d1_c");

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        // Verification is healthy and the dataset is unchanged.
        Assert.Equal(3, report.Scanned);
        Assert.True(report.IsHealthy);
        Assert.Empty(report.UnreachableIssueIds);

        var after = await SnapshotRowsAsync("issue_d1_a", "issue_d1_b", "issue_d1_c");
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Variables, after[i].Variables);
            Assert.Equal(before[i].UpdatedAt, after[i].UpdatedAt);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Verify_CorruptVariablesJson_DoesNotThrow_AndStaysHealthy()
    {
        // VariableBundle.FromJson tolerates malformed JSON by returning Empty,
        // so a row with garbage Variables is still "reachable" (no agent
        // exists outside Variables) and verification is healthy.
        await SeedRowAsync("issue_corrupt", "{not-valid-json");

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);

        Assert.True(report.IsHealthy);
        var row = Assert.Single(report.Rows);
        Assert.True(row.Reachable);
        Assert.Null(row.AgentPath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void InspectVariables_ReachableRow_ReportsAgentPath()
    {
        var result = IssueWorkflowProfileStorageIntegrity.InspectVariables(
            "issue_i",
            BundleJson(new { agent = new { model = "gpt-4o" } }));

        Assert.True(result.Reachable);
        Assert.Equal("vars.agent", result.AgentPath);
    }

    // ===================== Defensive copy =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void FoldAgentDataIntoBundle_WritesAgentAtVarsAgent()
    {
        var baseBundle = VariableBundle.Empty;

        var result = IssueWorkflowProfileStorageIntegrity.FoldAgentDataIntoBundle(
            baseBundle,
            agentConfig: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = "gpt-4o",
                ["type"] = "opencode",
            },
            stageAgentConfigs: null);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void FoldAgentDataIntoBundle_WritesStageAgentAtStagesVarsAgent()
    {
        var baseBundle = VariableBundle.Empty;

        var result = IssueWorkflowProfileStorageIntegrity.FoldAgentDataIntoBundle(
            baseBundle,
            agentConfig: null,
            stageAgentConfigs: new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
            {
                ["build"] = new(StringComparer.Ordinal) { ["model"] = "haiku" },
                ["check"] = new(StringComparer.Ordinal) { ["model"] = "gpt-4o" },
            });

        Assert.NotNull(result.Stages);
        Assert.True(result.Stages!.ContainsKey("build"));
        Assert.True(result.Stages!.ContainsKey("check"));

        using var buildDoc = JsonDocument.Parse(result.Stages["build"]!.Vars!.Value.GetRawText());
        Assert.Equal("haiku", buildDoc.RootElement.GetProperty("agent").GetProperty("model").GetString());

        using var checkDoc = JsonDocument.Parse(result.Stages["check"]!.Vars!.Value.GetRawText());
        Assert.Equal("gpt-4o", checkDoc.RootElement.GetProperty("agent").GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void FoldAgentDataIntoBundle_PreservesExistingBundleFields()
    {
        var baseBundle = VariableBundle.FromJson(BundleJson(new
        {
            agent = new { model = "old-model" },
            mohist = new { system = "mohist" },
        }));

        var result = IssueWorkflowProfileStorageIntegrity.FoldAgentDataIntoBundle(
            baseBundle,
            agentConfig: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = "new-model",
            },
            stageAgentConfigs: null);

        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        Assert.Equal("new-model", doc.RootElement.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("mohist", doc.RootElement.GetProperty("mohist").GetProperty("system").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void TryValidate_RoundTrips_ForValidBundleJson()
    {
        var json = BundleJson(
            vars: new { agent = new { model = "gpt-4o" } },
            stages: new
            {
                build = new { vars = new { agent = new { model = "haiku" } } }
            });

        Assert.True(IssueWorkflowProfileStorageIntegrity.TryValidate(json, out var error));
        Assert.Null(error);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryValidate_RejectsEmptyJson(string? json)
    {
        Assert.False(IssueWorkflowProfileStorageIntegrity.TryValidate(json!, out var error));
        Assert.NotNull(error);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DefensiveCopyVariables_WritesAgentToVarsAgent()
    {
        await SeedRowAsync("issue_dc1", "{}");

        var result = await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "issue_dc1",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = "gpt-4o",
            },
            stageAgentConfigs: null);

        Assert.NotNull(result);

        var stored = await LoadVariablesAsync("issue_dc1");
        using var doc = JsonDocument.Parse(stored);
        var agent = doc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DefensiveCopyVariables_WritesStageAgentToStagesVarsAgent()
    {
        await SeedRowAsync("issue_dc2", "{}");

        var result = await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "issue_dc2",
            agentConfig: null,
            stageAgentConfigs: new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
            {
                ["build"] = new(StringComparer.Ordinal) { ["model"] = "haiku" },
            });

        Assert.NotNull(result);

        var stored = await LoadVariablesAsync("issue_dc2");
        using var doc = JsonDocument.Parse(stored);
        var build = doc.RootElement.GetProperty("stages").GetProperty("build");
        Assert.Equal("haiku",
            build.GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DefensiveCopyVariables_ValidatesBeforeCommitting()
    {
        await SeedRowAsync("issue_dc3",
            BundleJson(new { agent = new { model = "original" } }));

        var result = await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "issue_dc3",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = "updated",
            },
            stageAgentConfigs: null);

        Assert.NotNull(result);

        // After commit, the stored bundle must round-trip through VariableBundle.
        var stored = await LoadVariablesAsync("issue_dc3");
        var reparsed = VariableBundle.FromJson(stored);
        Assert.NotNull(reparsed.Vars);
        Assert.Equal("updated", reparsed.Vars!.Value.GetProperty("agent").GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DefensiveCopyVariables_ValidationFailure_LeavesRowUntouched()
    {
        // Stage name is invalid (whitespace). The defensive copy should fail
        // before the row is mutated; the original Variables must remain.
        var originalJson = BundleJson(new { mohist = new { system = "mohist" } });
        await SeedRowAsync("issue_dc4", originalJson);
        var originalRow = await SnapshotRowAsync("issue_dc4");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
                _factory,
                "issue_dc4",
                agentConfig: null,
                stageAgentConfigs: new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
                {
                    ["   "] = new(StringComparer.Ordinal) { ["model"] = "haiku" },
                }));

        // Row must be unchanged.
        var afterRow = await SnapshotRowAsync("issue_dc4");
        Assert.Equal(originalRow.Variables, afterRow.Variables);
        Assert.Equal(originalRow.UpdatedAt, afterRow.UpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DefensiveCopyVariables_NoAgentData_ReturnsNullAndDoesNotWrite()
    {
        await SeedRowAsync("issue_dc5",
            BundleJson(new { mohist = new { system = "mohist" } }));
        var before = await SnapshotRowAsync("issue_dc5");

        var result = await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "issue_dc5",
            agentConfig: null,
            stageAgentConfigs: null);

        Assert.Null(result);
        var after = await SnapshotRowAsync("issue_dc5");
        Assert.Equal(before.Variables, after.Variables);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DefensiveCopyVariables_UnknownIssue_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
                _factory,
                "missing_issue",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["model"] = "gpt-4o" },
                stageAgentConfigs: null));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DefensiveCopyVariables_AfterCommit_VerificationReportsReachable()
    {
        await SeedRowAsync("issue_dc6", "{}");

        await IssueWorkflowProfileStorageIntegrity.DefensiveCopyVariablesAsync(
            _factory,
            "issue_dc6",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["model"] = "gpt-4o" },
            stageAgentConfigs: null);

        var report = await IssueWorkflowProfileStorageIntegrity.VerifyAsync(_factory);
        Assert.True(report.IsHealthy);
        var row = Assert.Single(report.Rows);
        Assert.Equal("vars.agent", row.AgentPath);
    }

    // ===================== helpers =====================

    private static string BundleJson(object? vars, object? stages = null)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (vars is not null) payload["vars"] = vars;
        if (stages is not null) payload["stages"] = stages;
        return JsonSerializer.Serialize(payload);
    }

    private async Task SeedRowAsync(string issueId, string variablesJson)
    {
        await using var db = new MohistDbContext(_options);
        var issueNumber = ++_nextIssueNumber;
        const string projectId = "profile-integrity";
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            State = IssueStore.Serialize(new DomainIssue
            {
                Id = issueId,
                ProjectId = projectId,
                Number = issueNumber,
                Title = issueId,
                Priority = "p2",
            }),
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            IssueId = issueId,
            Variables = variablesJson,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> LoadVariablesAsync(string issueId)
    {
        await using var db = new MohistDbContext(_options);
        var row = await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueId == issueId);
        Assert.NotNull(row);
        return row!.Variables;
    }

    private async Task<RowSnapshot> SnapshotRowAsync(string issueId)
    {
        await using var db = new MohistDbContext(_options);
        var row = await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueId == issueId);
        Assert.NotNull(row);
        return new RowSnapshot(row!.Variables, row.UpdatedAt);
    }

    private async Task<List<RowSnapshot>> SnapshotRowsAsync(params string[] issueIds)
    {
        var result = new List<RowSnapshot>(issueIds.Length);
        await using var db = new MohistDbContext(_options);
        foreach (var id in issueIds)
        {
            var row = await db.IssueWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.IssueId == id);
            Assert.NotNull(row);
            result.Add(new RowSnapshot(row!.Variables, row.UpdatedAt));
        }
        return result;
    }

    private sealed record RowSnapshot(string Variables, DateTimeOffset UpdatedAt);

    private sealed class TestFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;
        public TestFactory(DbContextOptions<MohistDbContext> options) => _options = options;
        public MohistDbContext CreateDbContext() => new(_options);
    }
}
