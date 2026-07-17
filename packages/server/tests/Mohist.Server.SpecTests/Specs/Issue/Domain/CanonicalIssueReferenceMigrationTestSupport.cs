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

public abstract class CanonicalIssueReferenceMigrationTestSupport
{
    protected const string BeforeIssueReferences = "20260715123000_ReconcileLineageSnapshotsFromMembership";
    protected const string BeforeAttachments = "20260618100150_AddAgentsTable";
    protected const string ReferenceColumns = "20260716130000_AddCanonicalIssueReferenceColumns";
    protected const string TargetMigration = "20260716140000_BackfillCanonicalIssueReferences";
    protected static readonly DateTimeOffset SeedTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    protected static async Task SeedOwnerGraphAsync(MohistDbContext context)
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

    protected static async Task SeedProjectsAndIssuesAsync(MohistDbContext context)
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

    protected static async Task SeedIssueAsync(
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

    protected static Task SeedLegacyProfileAsync(MohistDbContext context, string issueId)
    {
        var emptyJson = "{}";
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IssueWorkflowProfiles" ("IssueId", "Variables", "Prompts", "UpdatedAt")
            VALUES ({issueId}, {emptyJson}, {emptyJson}, {SeedTime})
            """);
    }

    protected static Task SeedLegacyArtifactAsync(
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

    protected static Task SeedAttachmentAsync(
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

    protected static async Task SeedWorkflowRunAsync(
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

    protected static Task SeedPendingUploadAsync(
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

    protected static async Task SeedSessionAsync(
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

    protected static Task ExecuteAsync(
        MohistDbContext context,
        string sql,
        DateTimeOffset value) =>
        context.Database.ExecuteSqlRawAsync(sql, value);

    protected static async Task<OwnerCounts> CountOwnersAsync(MohistDbContext context) => new(
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

    protected static Task<long> CountTableAsync(MohistDbContext context, string tableName)
    {
        var sql = tableName switch
        {
            "EpicIssues" => "SELECT COUNT(*) AS \"Value\" FROM \"EpicIssues\"",
            "EpicActiveIssues" => "SELECT COUNT(*) AS \"Value\" FROM \"EpicActiveIssues\"",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
        };
        return context.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    protected static async Task<HistoricalEvent> ReadHistoricalEventAsync(MohistDbContext context)
    {
        var row = await context.IssueEvents.AsNoTracking()
            .SingleAsync(item => item.EventId == "event_legacy");
        return new HistoricalEvent(row.Source, row.Data.GetRawText(), row.ExtensionsJson);
    }

    protected static async Task<ConvergedValues> ReadConvergedValuesAsync(MohistDbContext context)
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

    protected static TestDatabase CreateDatabase(string targetMigration)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, targetMigration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, options);
    }

    protected sealed class TestDatabase : IAsyncDisposable
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

    protected sealed record OwnerCounts(
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

    protected sealed record HistoricalEvent(string Source, string Data, string Extensions);

    protected sealed record ConvergedValues(
        string ProjectId,
        int ProfileIssueNumber,
        int? ArtifactIssueNumber,
        int? AttachmentIssueNumber,
        string? RunProjectId,
        int? RunIssueNumber);
}
