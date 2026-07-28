using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[Migration("20260728000000_TypedWorkflowRunLineage")]
[DbContext(typeof(MohistDbContext))]
public partial class TypedWorkflowRunLineage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        Rebuild(migrationBuilder, typed: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        Rebuild(migrationBuilder, typed: false);
    }

    private static void Rebuild(MigrationBuilder migrationBuilder, bool typed)
    {
        foreach (var index in Indexes)
            migrationBuilder.Sql($"DROP INDEX IF EXISTS \"{index}\";");

        migrationBuilder.Sql(typed ? LegacyToTypedJson : TypedToLegacyJson);
        migrationBuilder.Sql(typed ? CreateTypedTable : CreateLegacyTable);
        migrationBuilder.Sql(CopyRows);
        migrationBuilder.Sql("DROP TABLE \"WorkflowRuns\";");
        migrationBuilder.Sql("ALTER TABLE \"__WorkflowRuns\" RENAME TO \"WorkflowRuns\";");

        foreach (var statement in CreateIndexStatements)
            migrationBuilder.Sql(statement);

    }

    private static readonly string[] Indexes =
    [
        "IX_WorkflowRuns_MetadataProjectId",
        "IX_WorkflowRuns_AssignedWorkerId",
        "IX_WorkflowRuns_MetadataProjectId_AssignedWorkerId_CreatedAt",
        "IX_WorkflowRuns_ProjectId_IssueNumber",
        "IX_WorkflowRuns_ProjectId_EpicNumber",
        "IX_WorkflowRuns_Status",
        "IX_WorkflowRuns_Status_ReadySince",
        "IX_WorkflowRuns_MetadataProjectId_WorkflowProfileIdKey",
    ];

    private static readonly string[] CreateIndexStatements =
    [
        "CREATE INDEX \"IX_WorkflowRuns_MetadataProjectId\" ON \"WorkflowRuns\" (\"MetadataProjectId\");",
        "CREATE INDEX \"IX_WorkflowRuns_AssignedWorkerId\" ON \"WorkflowRuns\" (\"AssignedWorkerId\");",
        "CREATE INDEX \"IX_WorkflowRuns_MetadataProjectId_AssignedWorkerId_CreatedAt\" ON \"WorkflowRuns\" (\"MetadataProjectId\", \"AssignedWorkerId\", \"CreatedAt\");",
        "CREATE INDEX \"IX_WorkflowRuns_ProjectId_IssueNumber\" ON \"WorkflowRuns\" (\"MetadataProjectId\", \"IssueNumber\");",
        "CREATE INDEX \"IX_WorkflowRuns_ProjectId_EpicNumber\" ON \"WorkflowRuns\" (\"MetadataProjectId\", \"EpicNumber\");",
        "CREATE INDEX \"IX_WorkflowRuns_Status\" ON \"WorkflowRuns\" (\"Status\", \"AssignedWorkerId\");",
        "CREATE INDEX \"IX_WorkflowRuns_Status_ReadySince\" ON \"WorkflowRuns\" (\"Status\", \"AssignedWorkerId\", \"ReadySince\");",
        "CREATE INDEX \"IX_WorkflowRuns_MetadataProjectId_WorkflowProfileIdKey\" ON \"WorkflowRuns\" (\"MetadataProjectId\", \"WorkflowProfileIdKey\");",
    ];

    private const string Whitespace = "char(9, 10, 11, 12, 13, 32)";

    private const string LcProjectId = """
        COALESCE(
            json_extract("State", '$.metadata.annotations.projectId'),
            json_extract("State", '$.metadata.annotations.ProjectId'),
            json_extract("State", '$.Metadata.Annotations.projectId'),
            json_extract("State", '$.Metadata.Annotations.ProjectId'))
        """;

    private const string LcIssueNumber = """
        COALESCE(
            json_extract("State", '$.metadata.annotations.issueNumber'),
            json_extract("State", '$.metadata.annotations.IssueNumber'),
            json_extract("State", '$.Metadata.Annotations.issueNumber'),
            json_extract("State", '$.Metadata.Annotations.IssueNumber'))
        """;

    private const string LcEpicNumber = """
        COALESCE(
            json_extract("State", '$.metadata.annotations.epicNumber'),
            json_extract("State", '$.metadata.annotations.EpicNumber'),
            json_extract("State", '$.Metadata.Annotations.epicNumber'),
            json_extract("State", '$.Metadata.Annotations.EpicNumber'))
        """;

    private const string PcProjectId = """
        COALESCE(
            json_extract("State", '$.Metadata.Annotations.projectId'),
            json_extract("State", '$.Metadata.Annotations.ProjectId'))
        """;

    private const string PcIssueNumber = """
        COALESCE(
            json_extract("State", '$.Metadata.Annotations.issueNumber'),
            json_extract("State", '$.Metadata.Annotations.IssueNumber'))
        """;

    private const string PcEpicNumber = """
        COALESCE(
            json_extract("State", '$.Metadata.Annotations.epicNumber'),
            json_extract("State", '$.Metadata.Annotations.EpicNumber'))
        """;

    private static string HasNonWhitespace(string candidate) =>
        $"length(trim({candidate}, {Whitespace})) > 0";

    private static string ValidPositiveIssueNumber(string candidate)
    {
        var trimmed = $"trim({candidate}, {Whitespace})";
        var digits = $$"""
            CASE
                WHEN substr({{trimmed}}, 1, 1) IN ('+', '-')
                    THEN substr({{trimmed}}, 2)
                ELSE {{trimmed}}
            END
            """;
        return $$"""
            CAST({{candidate}} AS INTEGER) > 0
            AND ({{digits}}) GLOB '[0-9]*'
            AND ({{digits}}) NOT GLOB '*[^0-9]*'
            """;
    }

    private static readonly string LegacyToTypedJson = $$"""
        UPDATE "WorkflowRuns"
        SET "State" = json_set(
            json_remove("State",
                '$.metadata.annotations.projectId', '$.metadata.annotations.ProjectId',
                '$.metadata.annotations.issueNumber', '$.metadata.annotations.IssueNumber',
                '$.metadata.annotations.epicNumber', '$.metadata.annotations.EpicNumber',
                '$.Metadata.Annotations.projectId', '$.Metadata.Annotations.ProjectId',
                '$.Metadata.Annotations.issueNumber', '$.Metadata.Annotations.IssueNumber',
                '$.Metadata.Annotations.epicNumber', '$.Metadata.Annotations.EpicNumber'),
            '$.metadata.projectId', {{LcProjectId}},
            '$.metadata.issueNumber', CAST({{LcIssueNumber}} AS INTEGER),
            '$.metadata.epicNumber', COALESCE("EpicNumber", CAST({{LcEpicNumber}} AS INTEGER)))
        WHERE json_type("State", '$') = 'object'
          AND json_type("State", '$.metadata') = 'object'
          AND {{HasNonWhitespace(LcProjectId)}}
          AND {{ValidPositiveIssueNumber(LcIssueNumber)}};

        UPDATE "WorkflowRuns"
        SET "State" = json_set(
            json_remove("State",
                '$.Metadata.Annotations.projectId', '$.Metadata.Annotations.ProjectId',
                '$.Metadata.Annotations.issueNumber', '$.Metadata.Annotations.IssueNumber',
                '$.Metadata.Annotations.epicNumber', '$.Metadata.Annotations.EpicNumber'),
            '$.Metadata.ProjectId', {{PcProjectId}},
            '$.Metadata.IssueNumber', CAST({{PcIssueNumber}} AS INTEGER),
            '$.Metadata.EpicNumber', COALESCE("EpicNumber", CAST({{PcEpicNumber}} AS INTEGER)))
        WHERE json_type("State", '$') = 'object'
          AND json_type("State", '$.metadata') IS NULL
          AND json_type("State", '$.Metadata') = 'object'
          AND {{HasNonWhitespace(PcProjectId)}}
          AND {{ValidPositiveIssueNumber(PcIssueNumber)}};
        """;

    private const string TypedToLegacyJson = """
        UPDATE "WorkflowRuns"
        SET "State" = json_remove(
            json_set("State",
                '$.metadata.annotations.projectId', json_extract("State", '$.metadata.projectId'),
                '$.metadata.annotations.issueNumber', CAST(json_extract("State", '$.metadata.issueNumber') AS TEXT),
                '$.metadata.annotations.epicNumber', CAST(COALESCE("EpicNumber", json_extract("State", '$.metadata.epicNumber')) AS TEXT)),
            '$.metadata.projectId', '$.metadata.issueNumber', '$.metadata.epicNumber',
            '$.Metadata.ProjectId', '$.Metadata.IssueNumber', '$.Metadata.EpicNumber')
        WHERE json_extract("State", '$.metadata.projectId') IS NOT NULL
          AND json_extract("State", '$.metadata.issueNumber') IS NOT NULL;

        UPDATE "WorkflowRuns"
        SET "State" = json_remove(
            json_set("State",
                '$.Metadata.Annotations.ProjectId', json_extract("State", '$.Metadata.ProjectId'),
                '$.Metadata.Annotations.IssueNumber', CAST(json_extract("State", '$.Metadata.IssueNumber') AS TEXT),
                '$.Metadata.Annotations.EpicNumber', CAST(COALESCE("EpicNumber", json_extract("State", '$.Metadata.EpicNumber')) AS TEXT)),
            '$.Metadata.ProjectId', '$.Metadata.IssueNumber', '$.Metadata.EpicNumber')
        WHERE json_extract("State", '$.metadata.projectId') IS NULL
          AND json_extract("State", '$.Metadata.ProjectId') IS NOT NULL
          AND json_extract("State", '$.Metadata.IssueNumber') IS NOT NULL;
        """;

    private const string CreateTypedTable = """
        CREATE TABLE "__WorkflowRuns" (
            "WorkflowRunId" TEXT NOT NULL CONSTRAINT "PK_WorkflowRuns" PRIMARY KEY,
            "State" TEXT NOT NULL,
            "EpicNumber" INTEGER NULL,
            "MetadataProjectId" TEXT GENERATED ALWAYS AS (COALESCE(json_extract(State, '$.metadata.projectId'), json_extract(State, '$.Metadata.ProjectId'))) STORED,
            "CreatedAt" TEXT GENERATED ALWAYS AS (json_extract(State, '$.metadata.createdAt')) VIRTUAL,
            "AssignedWorkerId" TEXT GENERATED ALWAYS AS (COALESCE(json_extract(State, '$.assignment.workerId'), json_extract(State, '$.assignment.runnerId'), json_extract(State, '$.claim.runnerId'))) VIRTUAL,
            "ReadySince" TEXT GENERATED ALWAYS AS (COALESCE(json_extract(State, '$.readySince'), json_extract(State, '$.ReadySince'))) VIRTUAL,
            "Status" TEXT GENERATED ALWAYS AS (LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))) STORED,
            "IssueNumber" INTEGER GENERATED ALWAYS AS (CAST(COALESCE(json_extract(State, '$.metadata.issueNumber'), json_extract(State, '$.Metadata.IssueNumber')) AS INTEGER)) STORED,
            "WorkflowProfileIdKey" TEXT NULL,
            "ETag" INTEGER NOT NULL,
            CONSTRAINT "FK_WorkflowRuns_WorkflowProfileRecords_MetadataProjectId_WorkflowProfileIdKey"
                FOREIGN KEY ("MetadataProjectId", "WorkflowProfileIdKey")
                REFERENCES "WorkflowProfileRecords" ("ProjectId", "ProfileId") ON DELETE RESTRICT
        );
        """;

    private const string CreateLegacyTable = """
        CREATE TABLE "__WorkflowRuns" (
            "WorkflowRunId" TEXT NOT NULL CONSTRAINT "PK_WorkflowRuns" PRIMARY KEY,
            "State" TEXT NOT NULL,
            "EpicNumber" INTEGER NULL,
            "MetadataProjectId" TEXT GENERATED ALWAYS AS (COALESCE(json_extract(State, '$.metadata.annotations.projectId'), json_extract(State, '$.Metadata.Annotations.projectId'), json_extract(State, '$.Metadata.Annotations.ProjectId'))) STORED,
            "CreatedAt" TEXT GENERATED ALWAYS AS (json_extract(State, '$.metadata.createdAt')) VIRTUAL,
            "AssignedWorkerId" TEXT GENERATED ALWAYS AS (COALESCE(json_extract(State, '$.assignment.workerId'), json_extract(State, '$.assignment.runnerId'), json_extract(State, '$.claim.runnerId'))) VIRTUAL,
            "ReadySince" TEXT GENERATED ALWAYS AS (COALESCE(json_extract(State, '$.readySince'), json_extract(State, '$.ReadySince'))) VIRTUAL,
            "Status" TEXT GENERATED ALWAYS AS (LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))) STORED,
            "IssueNumber" INTEGER GENERATED ALWAYS AS (CAST(COALESCE(json_extract(State, '$.metadata.annotations.issueNumber'), json_extract(State, '$.Metadata.Annotations.issueNumber'), json_extract(State, '$.Metadata.Annotations.IssueNumber')) AS INTEGER)) STORED,
            "WorkflowProfileIdKey" TEXT NULL,
            "ETag" INTEGER NOT NULL,
            CONSTRAINT "FK_WorkflowRuns_WorkflowProfileRecords_MetadataProjectId_WorkflowProfileIdKey"
                FOREIGN KEY ("MetadataProjectId", "WorkflowProfileIdKey")
                REFERENCES "WorkflowProfileRecords" ("ProjectId", "ProfileId") ON DELETE RESTRICT
        );
        """;

    private const string CopyRows = """
        INSERT INTO "__WorkflowRuns" ("WorkflowRunId", "State", "EpicNumber", "WorkflowProfileIdKey", "ETag")
        SELECT "WorkflowRunId", "State", "EpicNumber", "WorkflowProfileIdKey", "ETag"
        FROM "WorkflowRuns";
        """;
}
