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

public class CanonicalIssueReferenceMigrationSpecs : CanonicalIssueReferenceMigrationTestSupport
{
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

}
