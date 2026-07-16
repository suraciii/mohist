using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public class CanonicalIssueReferenceMigrationSpecs
{
    private const string BeforeIssueReferences = "20260715123000_ReconcileLineageSnapshotsFromMembership";
    private const string BeforeAttachments = "20260618100150_AddAgentsTable";
    private const string ReferenceColumns = "20260716130000_AddCanonicalIssueReferenceColumns";
    private const string TargetMigration = "20260716140000_BackfillCanonicalIssueReferences";
    private static readonly DateTimeOffset SeedTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AttachmentMigration_IsDiscoverableInTheRealChain()
    {
        await using var database = CreateDatabase(BeforeIssueReferences);
        await using var context = database.CreateDbContext();

        var migrations = await context.Database
            .SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM \"__EFMigrationsHistory\"")
            .ToListAsync();
        var tables = await context.Database
            .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();

        Assert.Contains("20260618112000_AddAttachments", migrations);
        Assert.Contains("Attachments", tables);
    }

    [Fact]
    public async Task AttachmentMigration_AdoptsExistingOutOfBandTableWithoutDeletingRows()
    {
        await using var database = CreateDatabase(BeforeAttachments);
        await using var context = database.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "Attachments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Attachments" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "OwnerKind" TEXT NULL,
                "OwnerId" TEXT NULL,
                "OriginalFileName" TEXT NOT NULL,
                "ContentType" TEXT NULL,
                "Size" INTEGER NOT NULL,
                "StoragePath" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NULL
            );
            INSERT INTO "Attachments" (
                "Id", "ProjectId", "OriginalFileName", "Size", "StoragePath", "CreatedAt")
            VALUES ('existing_attachment', 'proj_existing', 'existing.txt', 1, '/existing.txt',
                    '2026-07-01 00:00:00+00:00');
            """);

        await context.GetService<IMigrator>().MigrateAsync(BeforeIssueReferences);

        var fileName = await context.Database.SqlQueryRaw<string>("""
            SELECT "OriginalFileName" AS "Value"
            FROM "Attachments"
            WHERE "Id" = 'existing_attachment'
            """).SingleAsync();
        var migration = await context.Database.SqlQueryRaw<string>("""
            SELECT "MigrationId" AS "Value"
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = '20260618112000_AddAttachments'
            """).SingleAsync();
        Assert.Equal("existing.txt", fileName);
        Assert.Equal("20260618112000_AddAttachments", migration);
    }

    [Fact]
    public async Task Migration_BackfillsEveryCurrentOwnerWithoutDeletingOrCrossingProjects()
    {
        await using var database = CreateDatabase(ReferenceColumns);
        await using var context = database.CreateDbContext();
        await SeedOwnerGraphAsync(context);
        var before = await CountOwnersAsync(context);
        var historicalBefore = await ReadHistoricalEventAsync(context);

        await context.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using var verify = database.CreateDbContext();
        Assert.Equal(before, await CountOwnersAsync(verify));
        Assert.Equal(historicalBefore, await ReadHistoricalEventAsync(verify));

        var alphaProfile = await verify.IssueWorkflowProfiles.AsNoTracking()
            .SingleAsync(row => row.ProjectId == "proj_alpha" && row.IssueNumber == 42);
        var betaProfile = await verify.IssueWorkflowProfiles.AsNoTracking()
            .SingleAsync(row => row.ProjectId == "proj_beta" && row.IssueNumber == 42);
        Assert.Equal(("proj_alpha", 42), (alphaProfile.ProjectId, alphaProfile.IssueNumber));
        Assert.Equal(("proj_beta", 42), (betaProfile.ProjectId, betaProfile.IssueNumber));

        var artifacts = await verify.WorkflowArtifacts.AsNoTracking()
            .OrderBy(row => row.ArtifactId)
            .ToListAsync();
        Assert.Collection(
            artifacts,
            row => Assert.Equal(("proj_alpha", 42), (row.ProjectId, row.IssueNumber)),
            row => Assert.Equal(("proj_beta", 42), (row.ProjectId, row.IssueNumber)));

        var issueAttachment = await verify.Attachments.AsNoTracking()
            .SingleAsync(row => row.Id == "att_alpha_issue");
        var commentAttachment = await verify.Attachments.AsNoTracking()
            .SingleAsync(row => row.Id == "att_alpha_comment");
        Assert.Equal(42, issueAttachment.OwnerIssueNumber);
        Assert.Null(commentAttachment.OwnerIssueNumber);

        var runs = await verify.WorkflowRuns.AsNoTracking()
            .OrderBy(row => row.WorkflowRunId)
            .Select(row => new { row.MetadataProjectId, row.IssueNumber })
            .ToListAsync();
        Assert.Collection(
            runs,
            row => Assert.Equal(("proj_alpha", 42), (row.MetadataProjectId, row.IssueNumber)),
            row => Assert.Equal(("proj_beta", 42), (row.MetadataProjectId, row.IssueNumber)));

        var pending = await (
            from upload in verify.WorkflowArtifactPendingUploads.AsNoTracking()
            join run in verify.WorkflowRuns.AsNoTracking()
                on upload.WorkflowRunId equals run.WorkflowRunId
            select new { upload.UploadId, run.MetadataProjectId, run.IssueNumber })
            .SingleAsync();
        Assert.Equal("upload_alpha", pending.UploadId);
        Assert.Equal(("proj_alpha", 42), (pending.MetadataProjectId, pending.IssueNumber));

        var comments = await verify.IssueComments.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Collection(
            comments,
            row => Assert.Equal(("proj_alpha", 42), (row.ProjectId, row.IssueNumber)),
            row => Assert.Equal(("proj_beta", 42), (row.ProjectId, row.IssueNumber)));

        var inbox = await verify.InboxItems.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Collection(
            inbox,
            row => Assert.Equal(("proj_alpha", 42), (row.ProjectId, row.IssueNumber)),
            row => Assert.Equal(("proj_beta", 42), (row.ProjectId, row.IssueNumber)));

        var sessions = await verify.AgentSessions.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Collection(
            sessions,
            row => Assert.Equal(("proj_beta", "42"), (row.LabelProjectId, row.LabelAgentLaunchIssueNumber)),
            row => Assert.Equal(("proj_alpha", "42"), (row.LabelProjectId, row.LabelIssueNumber)));
    }

    [Fact]
    public async Task Reconciliation_ConvergesOnceAndThenIsANoOp()
    {
        await using var database = CreateDatabase(ReferenceColumns);
        await using var context = database.CreateDbContext();
        await SeedProjectsAndIssuesAsync(context);
        await SeedLegacyProfileAsync(context, "issue_alpha_42");
        await SeedLegacyArtifactAsync(context, "artifact_alpha", "issue_alpha_42", "proj_alpha");
        await SeedAttachmentAsync(context, "att_alpha", "proj_alpha", "issue", "issue_alpha_42");
        await SeedWorkflowRunAsync(
            context,
            "run_alpha",
            "proj_alpha",
            "issue_alpha_42",
            includeProjectId: false);

        await CanonicalIssueReferenceReconciliation.ApplyAsync(context);
        var once = await ReadConvergedValuesAsync(context);

        await CanonicalIssueReferenceReconciliation.ApplyAsync(context);
        var twice = await ReadConvergedValuesAsync(context);

        Assert.Equal(once, twice);
        Assert.Equal(new ConvergedValues("proj_alpha", 42, 42, 42, "proj_alpha", 42), twice);
    }

    [Theory]
    [InlineData("profile", "CK_CanonicalIssueReference_Profiles")]
    [InlineData("artifact", "CK_CanonicalIssueReference_Artifacts")]
    [InlineData("attachment", "CK_CanonicalIssueReference_Attachments")]
    [InlineData("workflow", "CK_CanonicalIssueReference_WorkflowRuns")]
    [InlineData("comment", "CK_CanonicalIssueReference_Comments")]
    [InlineData("inbox", "CK_CanonicalIssueReference_Inbox")]
    [InlineData("epic-link", "CK_CanonicalIssueReference_EpicIssues")]
    [InlineData("epic-active", "CK_CanonicalIssueReference_EpicActiveIssues")]
    [InlineData("prerequisite", "CK_CanonicalIssueReference_Prerequisites")]
    [InlineData("session", "CK_CanonicalIssueReference_Sessions")]
    public async Task Reconciliation_FailsExplicitlyForUnresolvedOrMismatchedLegacyOwner(
        string owner,
        string expectedConstraint)
    {
        await using var database = CreateDatabase(ReferenceColumns);
        await using var context = database.CreateDbContext();
        await SeedProjectsAndIssuesAsync(context);

        switch (owner)
        {
            case "profile":
                await SeedLegacyProfileAsync(context, "issue_missing");
                break;
            case "artifact":
                await SeedLegacyArtifactAsync(context, "artifact_bad", "issue_alpha_42", "proj_beta");
                break;
            case "attachment":
                await SeedAttachmentAsync(context, "att_bad", "proj_alpha", "issue", "issue_missing");
                break;
            case "workflow":
                await SeedWorkflowRunAsync(context, "run_bad", "proj_alpha", "issue_missing");
                break;
            case "comment":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "IssueComments" ("Id", "ProjectId", "IssueId", "IssueNumber", "Body", "CreatedAt")
                    VALUES ('comment_bad', 'proj_alpha', 'issue_missing', 42, 'bad', {SeedTime});
                    """);
                break;
            case "inbox":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "InboxItems" (
                        "Id", "ProjectId", "IssueId", "IssueNumber", "IssueTitle", "NotificationKind",
                        "SourceEventSource", "SourceEventId", "CreatedAt", "ReadAt", "ArchivedAt")
                    VALUES ('inbox_bad', 'proj_alpha', 'issue_missing', 42, 'bad', 'issue_started',
                            '/legacy/bad', 'source_bad', {SeedTime}, NULL, NULL);
                    """);
                break;
            case "epic-link":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "EpicIssues" ("EpicId", "IssueId", "ProjectId", "IssueNumber", "CreatedAt")
                    VALUES ('epic_bad', 'issue_missing', 'proj_alpha', 42, {SeedTime});
                    """);
                break;
            case "epic-active":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
                    VALUES ('proj_alpha', 'issue_missing', 'epic_bad', 42, {SeedTime});
                    """);
                break;
            case "prerequisite":
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "IssuePrerequisites" ("ProjectId", "IssueNumber", "PrerequisiteNumber", "CreatedAt")
                    VALUES ('proj_alpha', 42, 99, {SeedTime});
                    """);
                break;
            case "session":
                await SeedSessionAsync(context, "session_bad", new Dictionary<string, string>
                {
                    ["mohist.io/project-id"] = "proj_alpha",
                    ["mohist.io/issue-number"] = "99",
                });
                break;
        }

        var before = await CountOwnersAsync(context);
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => CanonicalIssueReferenceReconciliation.ApplyAsync(context));
        Assert.Contains(expectedConstraint, exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, await CountOwnersAsync(context));
    }

    [Fact]
    public async Task Migration_RequiresScopedProfileAndCreatesScopedOwnerIndexes()
    {
        await using var database = CreateDatabase(TargetMigration);
        await using var context = database.CreateDbContext();

        var requiredProfileColumns = await context.Database.SqlQueryRaw<string>("""
            SELECT "name" AS "Value"
            FROM pragma_table_info('IssueWorkflowProfiles')
            WHERE "name" IN ('ProjectId', 'IssueNumber') AND "notnull" = 1
            ORDER BY "name"
            """).ToListAsync();
        Assert.Equal(new[] { "IssueNumber", "ProjectId" }, requiredProfileColumns);

        var indexes = await context.Database.SqlQueryRaw<string>("""
            SELECT "name" AS "Value"
            FROM sqlite_master
            WHERE type = 'index'
            ORDER BY "name"
            """).ToListAsync();
        Assert.Contains("IX_IssueWorkflowProfiles_ProjectId_IssueNumber", indexes);
        Assert.Contains("IX_WorkflowArtifacts_ProjectId_IssueNumber_RecordedAt", indexes);
        Assert.Contains("IX_Attachments_ProjectId_OwnerIssueNumber", indexes);
        Assert.Contains("IX_WorkflowRuns_ProjectId_IssueNumber", indexes);
        Assert.Contains("IX_AgentSessions_LabelProjectId_LabelIssueNumber_CreatedAt", indexes);
        Assert.Contains("IX_AgentSessions_LabelProjectId_LabelAgentLaunchIssueNumber_CreatedAt", indexes);
    }

    private static async Task SeedOwnerGraphAsync(MohistDbContext context)
    {
        await SeedProjectsAndIssuesAsync(context);
        await SeedLegacyProfileAsync(context, "issue_alpha_42");
        await SeedLegacyProfileAsync(context, "issue_beta_42");
        await SeedLegacyArtifactAsync(context, "artifact_alpha", "issue_alpha_42", "proj_alpha");
        await SeedLegacyArtifactAsync(context, "artifact_beta", "issue_beta_42", "proj_beta");
        await SeedAttachmentAsync(context, "att_alpha_issue", "proj_alpha", "issue", "issue_alpha_42");
        await SeedAttachmentAsync(context, "att_alpha_comment", "proj_alpha", "comment", "comment_alpha");
        await SeedWorkflowRunAsync(context, "run_alpha", "proj_alpha", "issue_alpha_42");
        await SeedWorkflowRunAsync(context, "run_beta", "proj_beta", "issue_beta_42");
        await SeedPendingUploadAsync(context, "upload_alpha", "run_alpha");

        await ExecuteAsync(context, """
            INSERT INTO "IssueComments" ("Id", "ProjectId", "IssueId", "IssueNumber", "Body", "CreatedAt")
            VALUES ('comment_alpha', 'proj_alpha', 'issue_alpha_42', 42, 'alpha', {0}),
                   ('comment_beta', 'proj_beta', 'issue_beta_42', 42, 'beta', {0});
            """, SeedTime);
        await ExecuteAsync(context, """
            INSERT INTO "InboxItems" (
                "Id", "ProjectId", "IssueId", "IssueNumber", "IssueTitle", "NotificationKind",
                "SourceEventSource", "SourceEventId", "CreatedAt", "ReadAt", "ArchivedAt")
            VALUES ('inbox_alpha', 'proj_alpha', 'issue_alpha_42', 42, 'alpha', 'issue_started',
                    '/legacy/alpha', 'source_alpha', {0}, NULL, NULL),
                   ('inbox_beta', 'proj_beta', 'issue_beta_42', 42, 'beta', 'issue_started',
                    '/legacy/beta', 'source_beta', {0}, NULL, NULL);
            """, SeedTime);
        await ExecuteAsync(context, """
            INSERT INTO "Epics" (
                "Id", "ProjectId", "Number", "Title", "Description", "Priority", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('epic_alpha_7', 'proj_alpha', 7, 'alpha', '', 'p2', 'running', {0}, {0}),
                   ('epic_beta_7', 'proj_beta', 7, 'beta', '', 'p2', 'running', {0}, {0});
            """, SeedTime);
        await ExecuteAsync(context, """
            INSERT INTO "EpicIssues" ("EpicId", "IssueId", "ProjectId", "IssueNumber", "CreatedAt")
            VALUES ('epic_alpha_7', 'issue_alpha_42', 'proj_alpha', 42, {0}),
                   ('epic_beta_7', 'issue_beta_42', 'proj_beta', 42, {0});
            """, SeedTime);
        await ExecuteAsync(context, """
            INSERT INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
            VALUES ('proj_alpha', 'issue_alpha_42', 'epic_alpha_7', 42, {0}),
                   ('proj_beta', 'issue_beta_42', 'epic_beta_7', 42, {0});
            """, SeedTime);
        await ExecuteAsync(context, """
            INSERT INTO "IssuePrerequisites" ("ProjectId", "IssueNumber", "PrerequisiteNumber", "CreatedAt")
            VALUES ('proj_alpha', 42, 7, {0}),
                   ('proj_beta', 42, 7, {0});
            """, SeedTime);

        await SeedSessionAsync(context, "session_agent_beta", new Dictionary<string, string>
        {
            ["mohist.io/project-id"] = "proj_beta",
            ["mohist.io/agent-launch/issue-number"] = "42",
        });
        await SeedSessionAsync(context, "session_workflow_alpha", new Dictionary<string, string>
        {
            ["mohist.io/project-id"] = "proj_alpha",
            ["mohist.io/issue-number"] = "42",
        });

        var historicalData = "{\"legacy\":true}";
        var historicalExtensions = "{\"issueid\":\"removed\"}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IssueEvents" (
                "Source", "Id", "EventId", "Type", "SpecVersion", "Subject",
                "DataContentType", "Data", "ExtensionsJson", "Time", "DispatchedAt")
            VALUES ('/mohist/issues/removed-legacy-id', 1, 'event_legacy', 'com.mohist.issue.created',
                    '1.0', NULL, 'application/json', {historicalData}, {historicalExtensions}, {SeedTime}, NULL);
            """);
    }

    private static async Task SeedProjectsAndIssuesAsync(MohistDbContext context)
    {
        await ExecuteAsync(context, """
            INSERT INTO "Projects" ("Id", "Name", "RepositoriesJson", "CreatedAt", "UpdatedAt")
            VALUES ('proj_alpha', 'alpha', '[]', {0}, {0}),
                   ('proj_beta', 'beta', '[]', {0}, {0});
            """, SeedTime);
        await SeedIssueAsync(context, "issue_alpha_42", "proj_alpha", 42);
        await SeedIssueAsync(context, "issue_beta_42", "proj_beta", 42);
        await SeedIssueAsync(context, "issue_alpha_7", "proj_alpha", 7);
        await SeedIssueAsync(context, "issue_beta_7", "proj_beta", 7);
    }

    private static async Task SeedIssueAsync(
        MohistDbContext context,
        string issueId,
        string projectId,
        int issueNumber)
    {
        var state = JSON.Serialize(new
        {
            id = issueId,
            projectId,
            number = issueNumber,
            title = issueId,
            priority = "p2",
            status = "open",
        });
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Issues" ("IssueId", "State", "Risk")
            VALUES ({issueId}, {state}, NULL)
            """);
    }

    private static Task SeedLegacyProfileAsync(MohistDbContext context, string issueId)
    {
        var emptyJson = "{}";
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IssueWorkflowProfiles" ("IssueId", "Variables", "Prompts", "UpdatedAt")
            VALUES ({issueId}, {emptyJson}, {emptyJson}, {SeedTime})
            """);
    }

    private static Task SeedLegacyArtifactAsync(
        MohistDbContext context,
        string artifactId,
        string issueId,
        string projectId)
    {
        var workflowRunId = "run_" + artifactId;
        var path = "/" + artifactId;
        var storagePath = "/store/" + artifactId;
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowArtifacts" (
                "ArtifactId", "WorkflowRunId", "TaskRunId", "Path", "RecordedAt",
                "ArtifactStoragePath", "Kind", "ProjectId", "IssueId")
            VALUES ({artifactId}, {workflowRunId}, 'task', {path}, {SeedTime},
                    {storagePath}, 'file', {projectId}, {issueId})
            """);
    }

    private static Task SeedAttachmentAsync(
        MohistDbContext context,
        string attachmentId,
        string projectId,
        string ownerKind,
        string ownerId)
    {
        var fileName = attachmentId + ".txt";
        var storagePath = "/store/" + attachmentId;
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Attachments" (
                "Id", "ProjectId", "OwnerKind", "OwnerId", "OriginalFileName",
                "ContentType", "Size", "StoragePath", "CreatedAt", "ExpiresAt")
            VALUES ({attachmentId}, {projectId}, {ownerKind}, {ownerId}, {fileName},
                    'text/plain', 1, {storagePath}, {SeedTime}, NULL)
            """);
    }

    private static async Task SeedWorkflowRunAsync(
        MohistDbContext context,
        string runId,
        string projectId,
        string issueId,
        bool includeProjectId = true)
    {
        var annotations = new Dictionary<string, string>
        {
            ["issueId"] = issueId,
        };
        if (includeProjectId)
            annotations["projectId"] = projectId;

        var state = JSON.Serialize(new
        {
            id = runId,
            status = "running",
            metadata = new
            {
                createdAt = SeedTime,
                annotations,
            },
            stages = Array.Empty<object>(),
        });
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "ETag")
            VALUES ({runId}, {state}, 1)
            """);
    }

    private static Task SeedPendingUploadAsync(
        MohistDbContext context,
        string uploadId,
        string workflowRunId) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowArtifactPendingUploads" (
                "UploadId", "WorkflowRunId", "WorkId", "TaskRunId", "Path", "Kind",
                "FileCount", "ContentType", "ContentHash", "Size", "StoragePath", "CreatedAt", "ExpiresAt")
            VALUES ({uploadId}, {workflowRunId}, 'work_alpha', 'task_alpha', '/artifact.txt', 'file',
                    NULL, 'text/plain', 'hash_alpha', 1, '/store/' || {uploadId}, {SeedTime}, {SeedTime});
            """);

    private static async Task SeedSessionAsync(
        MohistDbContext context,
        string sessionId,
        Dictionary<string, string> labels)
    {
        var state = JSON.Serialize(new { metadata = new { labels } });
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgentSessions" ("Id", "State", "Status", "CreatedAt")
            VALUES ({sessionId}, {state}, 'opened', {SeedTime.UtcDateTime})
            """);
    }

    private static Task ExecuteAsync(
        MohistDbContext context,
        string sql,
        DateTimeOffset value) =>
        context.Database.ExecuteSqlRawAsync(sql, value);

    private static async Task<OwnerCounts> CountOwnersAsync(MohistDbContext context) => new(
        Projects: await context.Projects.CountAsync(),
        Issues: await context.Issues.CountAsync(),
        Epics: await context.Epics.CountAsync(),
        Profiles: await context.IssueWorkflowProfiles.CountAsync(),
        Artifacts: await context.WorkflowArtifacts.CountAsync(),
        PendingUploads: await context.WorkflowArtifactPendingUploads.CountAsync(),
        Attachments: await context.Attachments.CountAsync(),
        Runs: await context.WorkflowRuns.CountAsync(),
        Comments: await context.IssueComments.CountAsync(),
        Inbox: await context.InboxItems.CountAsync(),
        LegacyEpicIssues: await CountTableAsync(context, "EpicIssues"),
        LegacyEpicActiveIssues: await CountTableAsync(context, "EpicActiveIssues"),
        Prerequisites: await context.IssuePrerequisites.CountAsync(),
        Sessions: await context.AgentSessions.CountAsync(),
        IssueEvents: await context.IssueEvents.CountAsync());

    private static Task<long> CountTableAsync(MohistDbContext context, string tableName)
    {
        var sql = tableName switch
        {
            "EpicIssues" => "SELECT COUNT(*) AS \"Value\" FROM \"EpicIssues\"",
            "EpicActiveIssues" => "SELECT COUNT(*) AS \"Value\" FROM \"EpicActiveIssues\"",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
        };
        return context.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    private static async Task<HistoricalEvent> ReadHistoricalEventAsync(MohistDbContext context)
    {
        var row = await context.IssueEvents.AsNoTracking()
            .SingleAsync(item => item.EventId == "event_legacy");
        return new HistoricalEvent(row.Source, row.Data.GetRawText(), row.ExtensionsJson);
    }

    private static async Task<ConvergedValues> ReadConvergedValuesAsync(MohistDbContext context)
    {
        var profile = await context.IssueWorkflowProfiles.AsNoTracking()
            .SingleAsync(row => row.ProjectId == "proj_alpha" && row.IssueNumber == 42);
        var artifact = await context.WorkflowArtifacts.AsNoTracking()
            .SingleAsync(row => row.ArtifactId == "artifact_alpha");
        var attachment = await context.Attachments.AsNoTracking()
            .SingleAsync(row => row.Id == "att_alpha");
        var run = await context.WorkflowRuns.AsNoTracking()
            .Where(row => row.WorkflowRunId == "run_alpha")
            .Select(row => new { row.MetadataProjectId, row.IssueNumber })
            .SingleAsync();
        return new ConvergedValues(
            profile.ProjectId,
            profile.IssueNumber,
            artifact.IssueNumber,
            attachment.OwnerIssueNumber,
            run.MetadataProjectId,
            run.IssueNumber);
    }

    private static TestDatabase CreateDatabase(string targetMigration)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, targetMigration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, options);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<MohistDbContext> _options;

        public TestDatabase(
            SqliteConnection connection,
            DbContextOptions<MohistDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public MohistDbContext CreateDbContext() => new(_options);

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed record OwnerCounts(
        int Projects,
        int Issues,
        int Epics,
        int Profiles,
        int Artifacts,
        int PendingUploads,
        int Attachments,
        int Runs,
        int Comments,
        int Inbox,
        long LegacyEpicIssues,
        long LegacyEpicActiveIssues,
        int Prerequisites,
        int Sessions,
        int IssueEvents);

    private sealed record HistoricalEvent(string Source, string Data, string Extensions);

    private sealed record ConvergedValues(
        string ProjectId,
        int ProfileIssueNumber,
        int? ArtifactIssueNumber,
        int? AttachmentIssueNumber,
        string? RunProjectId,
        int? RunIssueNumber);
}
