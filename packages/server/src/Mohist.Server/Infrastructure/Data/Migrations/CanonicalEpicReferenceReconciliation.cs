using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Migrations;

internal static class CanonicalEpicReferenceReconciliation
{
    internal const string Sql = """
        DROP TABLE IF EXISTS "__CanonicalEpicReferenceGuard";
        CREATE TEMP TABLE "__CanonicalEpicReferenceGuard" (
            "Epics" INTEGER NOT NULL CONSTRAINT "CK_CanonicalEpicReference_Epics" CHECK ("Epics" = 0),
            "EpicIssues" INTEGER NOT NULL CONSTRAINT "CK_CanonicalEpicReference_EpicIssues" CHECK ("EpicIssues" = 0),
            "EpicActiveIssues" INTEGER NOT NULL CONSTRAINT "CK_CanonicalEpicReference_EpicActiveIssues" CHECK ("EpicActiveIssues" = 0),
            "Issues" INTEGER NOT NULL CONSTRAINT "CK_CanonicalEpicReference_Issues" CHECK ("Issues" = 0),
            "WorkflowRuns" INTEGER NOT NULL CONSTRAINT "CK_CanonicalEpicReference_WorkflowRuns" CHECK ("WorkflowRuns" = 0),
            "Sessions" INTEGER NOT NULL CONSTRAINT "CK_CanonicalEpicReference_Sessions" CHECK ("Sessions" = 0)
        );

        INSERT INTO "__CanonicalEpicReferenceGuard"
        SELECT
            EXISTS (
                SELECT 1
                FROM "Epics" AS e
                WHERE TRIM(e."ProjectId") = ''
                   OR e."Number" IS NULL
                   OR e."Number" <= 0
                   OR EXISTS (
                       SELECT 1
                       FROM "Epics" AS duplicate
                       WHERE duplicate."ProjectId" = e."ProjectId"
                         AND duplicate."Number" = e."Number"
                         AND duplicate."Id" <> e."Id")),
            EXISTS (
                SELECT 1
                FROM "EpicIssues" AS link
                LEFT JOIN "Epics" AS e ON e."Id" = link."EpicId"
                LEFT JOIN "Issues" AS i ON i."IssueId" = link."IssueId"
                WHERE e."Id" IS NULL
                   OR e."ProjectId" IS NOT link."ProjectId"
                   OR e."Number" IS NULL
                   OR e."Number" <= 0
                   OR (link."EpicNumber" IS NOT NULL AND link."EpicNumber" IS NOT e."Number")
                   OR i."IssueId" IS NULL
                   OR i."ProjectId" IS NOT link."ProjectId"
                   OR i."Number" IS NOT link."IssueNumber"
                   OR EXISTS (
                       SELECT 1
                       FROM "EpicIssues" AS duplicate
                       WHERE duplicate."ProjectId" = link."ProjectId"
                         AND duplicate."EpicId" <> link."EpicId"
                         AND duplicate."EpicNumber" = link."EpicNumber"
                         AND duplicate."IssueNumber" = link."IssueNumber")),
            EXISTS (
                SELECT 1
                FROM "EpicActiveIssues" AS active
                LEFT JOIN "Epics" AS e ON e."Id" = active."EpicId"
                LEFT JOIN "Issues" AS i ON i."IssueId" = active."IssueId"
                WHERE e."Id" IS NULL
                   OR e."ProjectId" IS NOT active."ProjectId"
                   OR e."Number" IS NULL
                   OR e."Number" <= 0
                   OR (active."EpicNumber" IS NOT NULL AND active."EpicNumber" IS NOT e."Number")
                   OR i."IssueId" IS NULL
                   OR i."ProjectId" IS NOT active."ProjectId"
                   OR i."Number" IS NOT active."IssueNumber"
                   OR EXISTS (
                       SELECT 1
                       FROM "EpicActiveIssues" AS duplicate
                       WHERE duplicate."ProjectId" = active."ProjectId"
                         AND duplicate."IssueId" <> active."IssueId"
                         AND duplicate."EpicNumber" = active."EpicNumber"
                         AND duplicate."IssueNumber" = active."IssueNumber")),
            EXISTS (
                SELECT 1
                FROM "Issues" AS i
                LEFT JOIN "Epics" AS e ON e."Id" = i."EpicId"
                WHERE (i."EpicId" IS NULL AND i."EpicNumber" IS NOT NULL)
                   OR (i."EpicId" IS NOT NULL AND (
                       e."Id" IS NULL
                       OR e."ProjectId" IS NOT i."ProjectId"
                       OR e."Number" IS NULL
                       OR e."Number" <= 0
                       OR (i."EpicNumber" IS NOT NULL AND i."EpicNumber" IS NOT e."Number")))
                   OR NULLIF(TRIM(COALESCE(
                        json_extract(i."State", '$.epicId'),
                        json_extract(i."State", '$.EpicId'))), '') IS NOT i."EpicId"
                   OR i."EpicId" IS NOT COALESCE(
                       (SELECT active."EpicId"
                        FROM "EpicActiveIssues" AS active
                        WHERE active."ProjectId" = i."ProjectId"
                          AND active."IssueId" = i."IssueId"
                        ORDER BY active."CreatedAt" DESC, active."EpicId"
                        LIMIT 1),
                       (SELECT link."EpicId"
                        FROM "EpicIssues" AS link
                        WHERE link."ProjectId" = i."ProjectId"
                          AND link."IssueId" = i."IssueId"
                        ORDER BY link."CreatedAt" DESC, link."EpicId"
                        LIMIT 1))),
            EXISTS (
                SELECT 1
                FROM "WorkflowRuns" AS w
                LEFT JOIN "Epics" AS e ON e."Id" = w."EpicId"
                WHERE (w."EpicId" IS NULL AND w."EpicNumber" IS NOT NULL)
                   OR (w."EpicId" IS NOT NULL AND (
                       e."Id" IS NULL
                       OR w."MetadataProjectId" IS NULL
                       OR e."ProjectId" IS NOT w."MetadataProjectId"
                       OR e."Number" IS NULL
                       OR e."Number" <= 0
                       OR (w."EpicNumber" IS NOT NULL AND w."EpicNumber" IS NOT e."Number")))),
            EXISTS (
                SELECT 1
                FROM "AgentSessions" AS s
                WHERE s."LabelAgentLaunchEpicNumber" IS NOT NULL
                  AND (s."LabelProjectId" IS NULL
                    OR CAST(CAST(s."LabelAgentLaunchEpicNumber" AS INTEGER) AS TEXT) IS NOT s."LabelAgentLaunchEpicNumber"
                    OR CAST(s."LabelAgentLaunchEpicNumber" AS INTEGER) <= 0
                    OR NOT EXISTS (
                        SELECT 1
                        FROM "Epics" AS e
                        WHERE e."ProjectId" = s."LabelProjectId"
                          AND e."Number" = CAST(s."LabelAgentLaunchEpicNumber" AS INTEGER)))
        );

        DROP TABLE "__CanonicalEpicReferenceGuard";

        UPDATE "EpicIssues"
        SET "EpicNumber" = (
            SELECT e."Number"
            FROM "Epics" AS e
            WHERE e."Id" = "EpicIssues"."EpicId")
        WHERE "EpicNumber" IS NULL;

        UPDATE "EpicActiveIssues"
        SET "EpicNumber" = (
            SELECT e."Number"
            FROM "Epics" AS e
            WHERE e."Id" = "EpicActiveIssues"."EpicId")
        WHERE "EpicNumber" IS NULL;

        UPDATE "Issues"
        SET "EpicNumber" = (
            SELECT e."Number"
            FROM "Epics" AS e
            WHERE e."Id" = "Issues"."EpicId")
        WHERE "EpicId" IS NOT NULL AND "EpicNumber" IS NULL;

        UPDATE "WorkflowRuns"
        SET "EpicNumber" = (
            SELECT e."Number"
            FROM "Epics" AS e
            WHERE e."Id" = "WorkflowRuns"."EpicId")
        WHERE "EpicId" IS NOT NULL AND "EpicNumber" IS NULL;
        """;

    internal static Task<int> ApplyAsync(
        MohistDbContext db,
        CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlRawAsync(Sql, cancellationToken);
}
