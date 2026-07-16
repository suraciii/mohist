using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

[DbContext(typeof(MohistDbContext))]
[Migration("20260716165000_MigrateEpicAffiliationToIssues")]
public partial class MigrateEpicAffiliationToIssues : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "__IssueEpicAffiliation";
            CREATE TEMP TABLE "__IssueEpicAffiliation" AS
            SELECT
                i."ProjectId",
                i."Number" AS "IssueNumber",
                COALESCE(
                    (
                        SELECT active."EpicNumber"
                        FROM "EpicActiveIssues" active
                        WHERE active."ProjectId" = i."ProjectId"
                          AND active."IssueNumber" = i."Number"
                        ORDER BY active."CreatedAt" DESC, active."EpicNumber" DESC
                        LIMIT 1
                    ),
                    (
                        SELECT retained."EpicNumber"
                        FROM "EpicIssues" retained
                        WHERE retained."ProjectId" = i."ProjectId"
                          AND retained."IssueNumber" = i."Number"
                        ORDER BY retained."CreatedAt" DESC, retained."EpicNumber" DESC
                        LIMIT 1
                    )
                ) AS "EpicNumber"
            FROM "Issues" i;

            DROP TABLE IF EXISTS "__IssueEpicAffiliationGuard";
            CREATE TEMP TABLE "__IssueEpicAffiliationGuard" (
                "Conflicts" INTEGER NOT NULL CONSTRAINT "CK_IssueEpicAffiliation_Conflicts" CHECK ("Conflicts" = 0)
            );
            INSERT INTO "__IssueEpicAffiliationGuard"
            SELECT EXISTS (
                SELECT 1
                FROM "Issues" i
                INNER JOIN "__IssueEpicAffiliation" affiliation
                    ON affiliation."ProjectId" = i."ProjectId"
                   AND affiliation."IssueNumber" = i."Number"
                WHERE affiliation."EpicNumber" IS NOT NULL
                  AND i."EpicNumber" IS NOT NULL
                  AND i."EpicNumber" <> affiliation."EpicNumber"
            );
            DROP TABLE "__IssueEpicAffiliationGuard";

            UPDATE "Issues"
            SET "EpicNumber" = (
                SELECT affiliation."EpicNumber"
                FROM "__IssueEpicAffiliation" affiliation
                WHERE affiliation."ProjectId" = "Issues"."ProjectId"
                  AND affiliation."IssueNumber" = "Issues"."Number"
            )
            WHERE EXISTS (
                SELECT 1
                FROM "__IssueEpicAffiliation" affiliation
                WHERE affiliation."ProjectId" = "Issues"."ProjectId"
                  AND affiliation."IssueNumber" = "Issues"."Number"
                  AND affiliation."EpicNumber" IS NOT NULL
            );

            UPDATE "Issues"
            SET "State" = json_set("State", '$.epicNumber', "EpicNumber")
            WHERE "EpicNumber" IS NOT NULL;

            DROP TABLE "__IssueEpicAffiliation";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("Issue-owned Epic affiliation cannot be reconstructed from removed membership tables.");
    }
}
