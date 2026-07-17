using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Migrations;

internal static class CanonicalIssueReferenceReconciliation
{
    internal const string Sql = """
        DROP TABLE IF EXISTS "__CanonicalIssueReferenceGuard";
        CREATE TEMP TABLE "__CanonicalIssueReferenceGuard" (
            "Profiles" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_Profiles" CHECK ("Profiles" = 0),
            "Artifacts" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_Artifacts" CHECK ("Artifacts" = 0),
            "Attachments" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_Attachments" CHECK ("Attachments" = 0),
            "WorkflowRuns" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_WorkflowRuns" CHECK ("WorkflowRuns" = 0),
            "Comments" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_Comments" CHECK ("Comments" = 0),
            "Inbox" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_Inbox" CHECK ("Inbox" = 0),
            "EpicIssues" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_EpicIssues" CHECK ("EpicIssues" = 0),
            "EpicActiveIssues" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_EpicActiveIssues" CHECK ("EpicActiveIssues" = 0),
            "Prerequisites" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_Prerequisites" CHECK ("Prerequisites" = 0),
            "Sessions" INTEGER NOT NULL CONSTRAINT "CK_CanonicalIssueReference_Sessions" CHECK ("Sessions" = 0)
        );

        INSERT INTO "__CanonicalIssueReferenceGuard"
        SELECT
            EXISTS (
                SELECT 1
                FROM "IssueWorkflowProfiles" AS p
                LEFT JOIN "Issues" AS i ON i."IssueId" = p."IssueId"
                WHERE i."IssueId" IS NULL
                   OR i."ProjectId" IS NULL
                   OR i."Number" IS NULL
                   OR i."Number" <= 0
                   OR (p."ProjectId" IS NOT NULL AND p."ProjectId" IS NOT i."ProjectId")
                   OR (p."IssueNumber" IS NOT NULL AND p."IssueNumber" IS NOT i."Number")),
            EXISTS (
                SELECT 1
                FROM "WorkflowArtifacts" AS a
                LEFT JOIN "Issues" AS i ON i."IssueId" = a."IssueId"
                WHERE a."IssueId" IS NOT NULL
                  AND (i."IssueId" IS NULL
                    OR i."ProjectId" IS NULL
                    OR i."Number" IS NULL
                    OR i."Number" <= 0
                    OR (a."ProjectId" IS NOT NULL AND a."ProjectId" IS NOT i."ProjectId")
                    OR (a."IssueNumber" IS NOT NULL AND a."IssueNumber" IS NOT i."Number"))),
            EXISTS (
                SELECT 1
                FROM "Attachments" AS a
                LEFT JOIN "Issues" AS i ON i."IssueId" = a."OwnerId"
                WHERE (a."OwnerKind" = 'issue'
                    AND (a."OwnerId" IS NULL
                      OR i."IssueId" IS NULL
                      OR i."ProjectId" IS NULL
                      OR i."Number" IS NULL
                      OR i."Number" <= 0
                      OR a."ProjectId" IS NOT i."ProjectId"
                      OR (a."OwnerIssueNumber" IS NOT NULL AND a."OwnerIssueNumber" IS NOT i."Number")))
                   OR (a."OwnerKind" IS NOT 'issue' AND a."OwnerIssueNumber" IS NOT NULL)),
            EXISTS (
                SELECT 1
                FROM "WorkflowRuns" AS w
                LEFT JOIN "Issues" AS i ON i."IssueId" = COALESCE(
                    json_extract(w."State", '$.metadata.annotations.issueId'),
                    json_extract(w."State", '$.Metadata.Annotations.issueId'),
                    json_extract(w."State", '$.Metadata.Annotations.IssueId'))
                WHERE COALESCE(
                        json_extract(w."State", '$.metadata.annotations.issueId'),
                        json_extract(w."State", '$.Metadata.Annotations.issueId'),
                        json_extract(w."State", '$.Metadata.Annotations.IssueId')) IS NOT NULL
                  AND (i."IssueId" IS NULL
                    OR i."ProjectId" IS NULL
                    OR i."Number" IS NULL
                    OR i."Number" <= 0
                    OR (COALESCE(
                            json_extract(w."State", '$.metadata.annotations.projectId'),
                            json_extract(w."State", '$.Metadata.Annotations.projectId'),
                            json_extract(w."State", '$.Metadata.Annotations.ProjectId')) IS NOT NULL
                        AND COALESCE(
                            json_extract(w."State", '$.metadata.annotations.projectId'),
                            json_extract(w."State", '$.Metadata.Annotations.projectId'),
                            json_extract(w."State", '$.Metadata.Annotations.ProjectId')) IS NOT i."ProjectId")
                    OR (COALESCE(
                            json_extract(w."State", '$.metadata.annotations.issueNumber'),
                            json_extract(w."State", '$.Metadata.Annotations.issueNumber'),
                            json_extract(w."State", '$.Metadata.Annotations.IssueNumber')) IS NOT NULL
                        AND CAST(COALESCE(
                            json_extract(w."State", '$.metadata.annotations.issueNumber'),
                            json_extract(w."State", '$.Metadata.Annotations.issueNumber'),
                            json_extract(w."State", '$.Metadata.Annotations.IssueNumber')) AS INTEGER) IS NOT i."Number"))),
            EXISTS (
                SELECT 1
                FROM "IssueComments" AS c
                LEFT JOIN "Issues" AS i ON i."IssueId" = c."IssueId"
                WHERE i."IssueId" IS NULL
                   OR c."ProjectId" IS NOT i."ProjectId"
                   OR c."IssueNumber" IS NOT i."Number"),
            EXISTS (
                SELECT 1
                FROM "InboxItems" AS n
                LEFT JOIN "Issues" AS i ON i."IssueId" = n."IssueId"
                WHERE i."IssueId" IS NULL
                   OR n."ProjectId" IS NOT i."ProjectId"
                   OR n."IssueNumber" IS NOT i."Number"),
            EXISTS (
                SELECT 1
                FROM "EpicIssues" AS e
                LEFT JOIN "Issues" AS i ON i."IssueId" = e."IssueId"
                WHERE i."IssueId" IS NULL
                   OR e."ProjectId" IS NOT i."ProjectId"
                   OR e."IssueNumber" IS NOT i."Number"),
            EXISTS (
                SELECT 1
                FROM "EpicActiveIssues" AS e
                LEFT JOIN "Issues" AS i ON i."IssueId" = e."IssueId"
                WHERE i."IssueId" IS NULL
                   OR e."ProjectId" IS NOT i."ProjectId"
                   OR e."IssueNumber" IS NOT i."Number"),
            EXISTS (
                SELECT 1
                FROM "IssuePrerequisites" AS p
                WHERE NOT EXISTS (
                        SELECT 1 FROM "Issues" AS i
                        WHERE i."ProjectId" = p."ProjectId" AND i."Number" = p."IssueNumber")
                   OR NOT EXISTS (
                        SELECT 1 FROM "Issues" AS i
                        WHERE i."ProjectId" = p."ProjectId" AND i."Number" = p."PrerequisiteNumber")),
            EXISTS (
                SELECT 1
                FROM "AgentSessions" AS s
                WHERE (s."LabelIssueNumber" IS NOT NULL
                    AND (s."LabelProjectId" IS NULL
                      OR CAST(s."LabelIssueNumber" AS INTEGER) <= 0
                      OR NOT EXISTS (
                          SELECT 1 FROM "Issues" AS i
                          WHERE i."ProjectId" = s."LabelProjectId"
                            AND i."Number" = CAST(s."LabelIssueNumber" AS INTEGER))))
                   OR (s."LabelAgentLaunchIssueNumber" IS NOT NULL
                    AND (s."LabelProjectId" IS NULL
                      OR CAST(s."LabelAgentLaunchIssueNumber" AS INTEGER) <= 0
                      OR NOT EXISTS (
                          SELECT 1 FROM "Issues" AS i
                          WHERE i."ProjectId" = s."LabelProjectId"
                            AND i."Number" = CAST(s."LabelAgentLaunchIssueNumber" AS INTEGER)))));

        DROP TABLE "__CanonicalIssueReferenceGuard";

        UPDATE "IssueWorkflowProfiles"
        SET "ProjectId" = COALESCE("ProjectId", (
                SELECT i."ProjectId" FROM "Issues" AS i
                WHERE i."IssueId" = "IssueWorkflowProfiles"."IssueId")),
            "IssueNumber" = COALESCE("IssueNumber", (
                SELECT i."Number" FROM "Issues" AS i
                WHERE i."IssueId" = "IssueWorkflowProfiles"."IssueId"))
        WHERE "ProjectId" IS NULL OR "IssueNumber" IS NULL;

        UPDATE "WorkflowArtifacts"
        SET "ProjectId" = COALESCE("ProjectId", (
                SELECT i."ProjectId" FROM "Issues" AS i
                WHERE i."IssueId" = "WorkflowArtifacts"."IssueId")),
            "IssueNumber" = COALESCE("IssueNumber", (
                SELECT i."Number" FROM "Issues" AS i
                WHERE i."IssueId" = "WorkflowArtifacts"."IssueId"))
        WHERE "IssueId" IS NOT NULL
          AND ("ProjectId" IS NULL OR "IssueNumber" IS NULL);

        UPDATE "Attachments"
        SET "OwnerIssueNumber" = COALESCE("OwnerIssueNumber", (
                SELECT i."Number" FROM "Issues" AS i
                WHERE i."IssueId" = "Attachments"."OwnerId"))
        WHERE "OwnerKind" = 'issue' AND "OwnerIssueNumber" IS NULL;

        UPDATE "WorkflowRuns" AS w
        SET "State" = CASE
            WHEN json_type(w."State", '$.metadata') IS NOT NULL THEN
                json_set(
                    w."State",
                    '$.metadata.annotations.projectId', (
                        SELECT i."ProjectId" FROM "Issues" AS i
                        WHERE i."IssueId" = COALESCE(
                            json_extract(w."State", '$.metadata.annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.IssueId'))),
                    '$.metadata.annotations.issueNumber', CAST((
                        SELECT i."Number" FROM "Issues" AS i
                        WHERE i."IssueId" = COALESCE(
                            json_extract(w."State", '$.metadata.annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.IssueId'))) AS TEXT))
            ELSE
                json_set(
                    w."State",
                    '$.Metadata.Annotations.projectId', (
                        SELECT i."ProjectId" FROM "Issues" AS i
                        WHERE i."IssueId" = COALESCE(
                            json_extract(w."State", '$.metadata.annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.IssueId'))),
                    '$.Metadata.Annotations.issueNumber', CAST((
                        SELECT i."Number" FROM "Issues" AS i
                        WHERE i."IssueId" = COALESCE(
                            json_extract(w."State", '$.metadata.annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.issueId'),
                            json_extract(w."State", '$.Metadata.Annotations.IssueId'))) AS TEXT))
            END
        WHERE COALESCE(
                json_extract(w."State", '$.metadata.annotations.issueId'),
                json_extract(w."State", '$.Metadata.Annotations.issueId'),
                json_extract(w."State", '$.Metadata.Annotations.IssueId')) IS NOT NULL
          AND (COALESCE(
                json_extract(w."State", '$.metadata.annotations.projectId'),
                json_extract(w."State", '$.Metadata.Annotations.projectId'),
                json_extract(w."State", '$.Metadata.Annotations.ProjectId')) IS NULL
            OR COALESCE(
                json_extract(w."State", '$.metadata.annotations.issueNumber'),
                json_extract(w."State", '$.Metadata.Annotations.issueNumber'),
                json_extract(w."State", '$.Metadata.Annotations.IssueNumber')) IS NULL);
        """;

    internal static Task<int> ApplyAsync(
        MohistDbContext db,
        CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, cancellationToken);
}
