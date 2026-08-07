using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-130 T-001: focused unit specs for <see cref="AgentSessionQuery"/> that
/// exercise the newly queryable agent-launch label keys (and the workflow
/// regression), over a migrated <see cref="TestSqliteDatabase"/>.
/// </summary>
public class AgentSessionQuerySpecs
{
    private const string ProjectA = "proj-A";
    private const string ProjectB = "proj-B";

    // Agent profile A1 emits generic agent-launch sessions into ProjectA.
    private const string AgentA1Id = "agent_A1";
    private const string AgentA1Name = "agent-alpha";
    private const string AgentA1IssueNumber = "42";
    private const string AgentA1EpicNumber = "7";
    private const string AgentA1Repository = "mohist/repo-a";
    private const string AgentA1WorkspaceName = "pay";

    // Agent profile A2 also lives in ProjectA so the agent-id filter
    // can distinguish between two agents sharing a project.
    private const string AgentA2Id = "agent_A2";
    private const string AgentA2Name = "agent-bravo";

    // Workflow-shaped session fields.
    private const string WorkflowRunW1 = "wr-w1";
    private const string SessionNameW1 = "plan";
    private const string WorkflowIssueNumberW1 = "100";
    private static readonly FakeTimeProvider TimeProvider = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task QueryByAgentId_ReturnsOnlyThatAgentsGenericSessions()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        var query = new AgentSessionQuery(fixture, TimeProvider);

        var matches = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
        });

        var ids = matches.Select(m => m.Row.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "s_a1_1", "s_a1_2_with_issue", "s_a1_3_with_epic", "s_a1_with_repo", "s_a1_with_workspace"
        }, ids);
        Assert.DoesNotContain(matches, m => m.Row.Id == "s_a2_1");
        Assert.DoesNotContain(matches, m => m.Row.Id == "s_w1_workflow");
        // Agent-id alone (without project) is the index-only filter the
        // workbench pass-through uses; check the agent-id query in
        // isolation too — every result must be an agent-launch session.
        var unfiltered = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
        });
        Assert.NotEmpty(unfiltered);
        Assert.All(unfiltered, m =>
            Assert.Equal(AgentA1Id, m.Row.LabelAgentId));
        Assert.DoesNotContain(unfiltered, m => m.Row.Id == "s_a2_1");
        Assert.DoesNotContain(unfiltered, m => m.Row.Id == "s_w1_workflow");
    }

    [Fact]
    public async Task ProvisionalLaunch_IsHiddenFromDefaultSessionQueries()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        var state = JsonSerializer.Serialize(new
        {
            id = "s_provisional",
            metadata = new
            {
                labels = new Dictionary<string, string>
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
                },
            },
            runtime = new { runnerId = "runner-provisional", workDir = "/workspace" },
            settings = new { },
            status = new { createdAt = TimeProvider.GetUtcNow().UtcDateTime },
        }, JSON.Options);
        await using (var db = fixture.CreateDbContext())
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = "s_provisional",
                State = state,
                Status = "opened",
                CreatedAt = TimeProvider.GetUtcNow().UtcDateTime,
                LaunchVisibility = "provisional",
            });
            await db.SaveChangesAsync();
        }

        var query = new AgentSessionQuery(fixture, TimeProvider);
        Assert.Empty(await query.ListByIdsAsync(["s_provisional"]));
        Assert.Empty(await query.ListByLabelsAsync(new Dictionary<string, string>
        {
            [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
        }));
    }

    [Fact]
    public async Task QueryByAgentName_ResolvesToSameSetAsAgentId()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        var query = new AgentSessionQuery(fixture, TimeProvider);

        var byId = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
        });
        var byName = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [GenericAgentSessionMetadata.AgentName] = AgentA1Name,
        });

        Assert.Equal(
            byId.Select(r => r.Row.Id).OrderBy(id => id, StringComparer.Ordinal),
            byName.Select(r => r.Row.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("s_a1_2_with_issue", GenericAgentSessionMetadata.IssueNumber, AgentA1IssueNumber)]
    [InlineData("s_a1_3_with_epic", GenericAgentSessionMetadata.EpicNumber, AgentA1EpicNumber)]
    [InlineData("s_a1_with_repo", GenericAgentSessionMetadata.Repository, AgentA1Repository)]
    [InlineData("s_a1_with_workspace", GenericAgentSessionMetadata.WorkspaceName, AgentA1WorkspaceName)]
    public async Task QueryByAgentLaunchContextRef_ResolvesViaIndexedColumn(
        string matchedSessionId, string labelKey, string labelValue)
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        var query = new AgentSessionQuery(fixture, TimeProvider);

        var matches = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [labelKey] = labelValue,
        });

        var ids = matches.Select(m => m.Row.Id).ToList();
        Assert.Contains(matchedSessionId, ids);
        // The four context-ref filters must NOT collapse into the no-match
        // fallback that swallowed agent-launch keys pre-T-001; verify by
        // confirming at least one match even when scoped narrowly.
        Assert.NotEmpty(matches);
        // Other agents' sessions with no matching context-ref must be
        // excluded (the A2 session has no context refs at all).
        Assert.DoesNotContain(matches, m => m.Row.Id == "s_a2_1");
        // Workflow-shaped sessions must be excluded even when they share
        // the project and even when they happen to carry a workflow-side
        // reference like issue-number=100.
        Assert.DoesNotContain(matches, m => m.Row.Id == "s_w1_workflow");
    }

    [Theory]
    [InlineData(AgentSessionQueryMetadataKeys.ProjectId, ProjectA, new[] { "s_a1_1", "s_a1_2_with_issue", "s_a1_3_with_epic", "s_a1_with_repo", "s_a1_with_workspace", "s_a2_1", "s_w1_workflow" })]
    [InlineData(AgentSessionQueryMetadataKeys.WorkflowRunId, WorkflowRunW1, new[] { "s_w1_workflow" })]
    [InlineData(AgentSessionQueryMetadataKeys.SessionName, SessionNameW1, new[] { "s_w1_workflow" })]
    [InlineData(AgentSessionQueryMetadataKeys.IssueNumber, WorkflowIssueNumberW1, new[] { "s_w1_workflow" })]
    [InlineData(AgentSessionQueryMetadataKeys.WorkId, "task-1.1", new[] { "s_w1_workflow" })]
    [InlineData(AgentSessionQueryMetadataKeys.WorkType, "task", new[] { "s_w1_workflow" })]
    [InlineData(AgentSessionQueryMetadataKeys.Stage, "plan", new[] { "s_w1_workflow" })]
    public async Task WorkflowShapedLookupKeys_StillResolveExactlyAsBefore(        string labelKey, string labelValue, string[] expectedIds)
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        var query = new AgentSessionQuery(fixture, TimeProvider);

        var matches = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [labelKey] = labelValue,
        });

        var ids = matches.Select(m => m.Row.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedIds.OrderBy(id => id, StringComparer.Ordinal), ids);
    }

    [Fact]
    public async Task QueryByTriggerLabels_ResolvesRuleTriggeredSessions()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        const string eventId = "evt_rule_42";
        const string ruleId = "rule_abc123";
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
            [GenericAgentSessionMetadata.AgentName] = AgentA1Name,
            [GenericAgentSessionMetadata.TriggerEventId] = eventId,
            [GenericAgentSessionMetadata.TriggerRuleId] = ruleId,
        };
        await using (var db = fixture.CreateDbContext())
        {
            db.AgentSessions.Add(BuildRow("s_triggered", labels, createdAt: TestTime.UtcDateTime));
            await db.SaveChangesAsync();
        }

        var query = new AgentSessionQuery(fixture, TimeProvider);

        var byEvent = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = eventId,
        });
        Assert.Equal(new[] { "s_triggered" }, byEvent.Select(m => m.Row.Id).ToArray());

        var byRule = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerRuleId] = ruleId,
        });
        Assert.Equal(new[] { "s_triggered" }, byRule.Select(m => m.Row.Id).ToArray());

        var byBoth = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = eventId,
            [GenericAgentSessionMetadata.TriggerRuleId] = ruleId,
        });
        Assert.Equal(new[] { "s_triggered" }, byBoth.Select(m => m.Row.Id).ToArray());

        var byMissingEvent = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = "evt_does_not_exist",
        });
        Assert.Empty(byMissingEvent);
    }

    [Fact]
    public async Task QueryByTriggerLabels_ManualSessionsDoNotMatch()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        var query = new AgentSessionQuery(fixture, TimeProvider);

        var matches = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = "evt_any",
        });

        Assert.Empty(matches);
    }

    [Fact]
    public async Task ComputedColumns_PopulateFromStateJson_WithoutBackfill()
    {
        // Adds a session by id/createdAt only — the new computed columns must
        // derive their values from the State JSON (no data backfill step),
        // and QueryRowsByLabels must therefore find it via the indexed label
        // path rather than a no-match fallback.
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        var storedLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.AgentId] = "agent_runtime_discovered",
            [GenericAgentSessionMetadata.AgentName] = "agent-runtime",
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectB,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.IssueNumber] = "999",
            [GenericAgentSessionMetadata.TriggerEventId] = "evt_runtime",
            [GenericAgentSessionMetadata.TriggerRuleId] = "rule_runtime",
        };
        var state = JsonSerializer.Serialize(new
        {
            id = "s_runtime",
            metadata = new { labels = storedLabels },
            runtime = new { runnerId = "runtime-runner", workDir = (string?)null },
            settings = new { },
            status = new { createdAt = TestTime.UtcDateTime },
        }, JSON.Options);
        await using (var db = fixture.CreateDbContext())
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = "s_runtime",
                State = state,
                Status = "opened",
                CreatedAt = TestTime.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        var query = new AgentSessionQuery(fixture, TimeProvider);

        // The agent-id index must resolve the row that was inserted with
        // labels only in its State JSON — proving the computed column SQL
        // is being applied to State, not relying on column writes.
        var byId = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.AgentId] = "agent_runtime_discovered",
        });
        Assert.Equal(new[] { "s_runtime" }, byId.Select(m => m.Row.Id).ToArray());

        // Same assertion against the agent-launch issue-number index.
        var byIssue = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.IssueNumber] = "999",
        });
        Assert.Contains("s_runtime", byIssue.Select(m => m.Row.Id));

        // issue-391 T-003: trigger correlation labels must also resolve via
        // the new stored computed columns.
        var byEvent = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = "evt_runtime",
        });
        Assert.Equal(new[] { "s_runtime" }, byEvent.Select(m => m.Row.Id).ToArray());

        var byRule = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerRuleId] = "rule_runtime",
        });
        Assert.Equal(new[] { "s_runtime" }, byRule.Select(m => m.Row.Id).ToArray());

        // Read back via EF so the populated computed-column values are
        // visible to the test — protects against the (silent) regression
        // where the column SQL evaluates to null.
        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.AgentSessions.AsNoTracking().SingleAsync(r => r.Id == "s_runtime");
            Assert.Equal("agent_runtime_discovered", row.LabelAgentId);
            Assert.Equal("agent-runtime", row.LabelAgentName);
            Assert.Equal("999", row.LabelAgentLaunchIssueNumber);
            Assert.Equal(ProjectB, row.LabelProjectId);
            Assert.Equal("agent-launch", row.LabelSourceKind);
            Assert.Equal("evt_runtime", row.LabelTriggerEventId);
            Assert.Equal("rule_runtime", row.LabelTriggerRuleId);
        }
    }

    [Fact]
    public async Task AgentScopedQuery_FiltersByProjectAndAgent_ExcludesOtherProjects()
    {
        // Sanity check: a project-id + agent-id combination that matches
        // nothing must return zero rows (proves neither filter collapses
        // to "all rows" nor bleeds across projects).
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        SeedMixedSessions(fixture);

        var query = new AgentSessionQuery(fixture, TimeProvider);

        var inOtherProject = await query.ListByLabelsAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = ProjectB,
            [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
        });
        Assert.Empty(inOtherProject);
    }

    private static void SeedMixedSessions(IDbContextFactory<MohistDbContext> factory)
    {
        var createdAt = new DateTime(2026, 6, 10, 1, 0, 0, DateTimeKind.Utc);
        using var db = factory.CreateDbContext();
        var rows = new[]
        {
            // Three agent-launch sessions for agent A1, covering the bare,
            // issue-ref, and epic-ref labels.
            BuildRow("s_a1_1",
                labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
                    [GenericAgentSessionMetadata.AgentName] = AgentA1Name,
                }, createdAt: createdAt),
            BuildRow("s_a1_2_with_issue",
                labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
                    [GenericAgentSessionMetadata.AgentName] = AgentA1Name,
                    [GenericAgentSessionMetadata.IssueNumber] = AgentA1IssueNumber,
                }, createdAt: createdAt.AddMinutes(1)),
            BuildRow("s_a1_3_with_epic",
                labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
                    [GenericAgentSessionMetadata.AgentName] = AgentA1Name,
                    [GenericAgentSessionMetadata.EpicNumber] = AgentA1EpicNumber,
                }, createdAt: createdAt.AddMinutes(2)),
            BuildRow("s_a1_with_repo",
                labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
                    [GenericAgentSessionMetadata.AgentName] = AgentA1Name,
                    [GenericAgentSessionMetadata.Repository] = AgentA1Repository,
                }, createdAt: createdAt.AddMinutes(3)),
            BuildRow("s_a1_with_workspace",
                labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = AgentA1Id,
                    [GenericAgentSessionMetadata.AgentName] = AgentA1Name,
                    [GenericAgentSessionMetadata.WorkspaceName] = AgentA1WorkspaceName,
                }, createdAt: createdAt.AddMinutes(4)),
            // Second agent A2 sharing ProjectA — must not leak into A1 queries.
            BuildRow("s_a2_1",
                labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = AgentA2Id,
                    [GenericAgentSessionMetadata.AgentName] = AgentA2Name,
                }, createdAt: createdAt.AddMinutes(5)),
            // Workflow-shaped session in ProjectA; must not match generic
            // agent-launch queries and must still match the workflow keys.
            BuildRow("s_w1_workflow",
                labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = ProjectA,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "workflow",
                    [AgentSessionQueryMetadataKeys.WorkflowRunId] = WorkflowRunW1,
                    [AgentSessionQueryMetadataKeys.SessionName] = SessionNameW1,
                    [AgentSessionQueryMetadataKeys.IssueNumber] = WorkflowIssueNumberW1,
                    [AgentSessionQueryMetadataKeys.WorkId] = "task-1.1",
                    [AgentSessionQueryMetadataKeys.WorkType] = "task",
                    [AgentSessionQueryMetadataKeys.Stage] = "plan",
                }, createdAt: createdAt.AddMinutes(6)),
        };
        db.AgentSessions.AddRange(rows);
        db.SaveChanges();
    }

    private static AgentSessionRow BuildRow(
        string id,
        Dictionary<string, string> labels,
        DateTime createdAt)
    {
        var stateJson = JsonSerializer.Serialize(new
        {
            id,
            metadata = new { labels },
            runtime = new { runnerId = $"runner-{id}", workDir = (string?)null },
            settings = new { },
            status = new { createdAt = createdAt },
        }, JSON.Options);
        return new AgentSessionRow
        {
            Id = id,
            State = stateJson,
            RunnerId = $"runner-{id}",
            Status = "opened",
            CreatedAt = createdAt,
        };
    }
}
