using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrleansStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrleansStorage",
                columns: table => new
                {
                    GrainIdHash = table.Column<int>(type: "INTEGER", nullable: false),
                    GrainIdN0 = table.Column<long>(type: "INTEGER", nullable: false),
                    GrainIdN1 = table.Column<long>(type: "INTEGER", nullable: false),
                    GrainTypeHash = table.Column<int>(type: "INTEGER", nullable: false),
                    GrainTypeString = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    GrainIdExtensionString = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ServiceId = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    PayloadBinary = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: true)
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrleansStorage",
                table: "OrleansStorage",
                columns: new[] { "GrainIdHash", "GrainTypeHash" });

            migrationBuilder.Sql(
                """
                INSERT INTO OrleansQuery (QueryKey, QueryText) VALUES
                ('WriteToStorageKey', '
                    BEGIN TRANSACTION;

                    CREATE TEMP TABLE IF NOT EXISTS OrleansStorageWriteState
                    (
                        TotalChangesBefore INT NOT NULL
                    );
                    DELETE FROM OrleansStorageWriteState;
                    INSERT INTO OrleansStorageWriteState (TotalChangesBefore) VALUES (total_changes() + 1);

                    UPDATE OrleansStorage
                    SET
                        PayloadBinary = @PayloadBinary,
                        ModifiedOn = datetime(''now''),
                        Version = Version + 1
                    WHERE
                        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                        AND Version = @GrainStateVersion;

                    INSERT INTO OrleansStorage (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary, ModifiedOn, Version)
                    SELECT @GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString, @GrainIdExtensionString, @ServiceId, @PayloadBinary, datetime(''now''), 1
                    WHERE changes() = 0
                      AND @GrainStateVersion IS NULL
                      AND NOT EXISTS (
                        SELECT 1 FROM OrleansStorage
                        WHERE GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                    );

                    SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                    WHERE total_changes() > (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                        AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId;

                    SELECT @GrainStateVersion AS NewGrainStateVersion
                    WHERE total_changes() = (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                        AND @GrainStateVersion IS NOT NULL;

                    COMMIT;
                '),
                ('ReadFromStorageKey', '
                    SELECT
                        PayloadBinary,
                        Version AS Version
                    FROM
                        OrleansStorage
                    WHERE
                        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                    LIMIT 1;
                '),
                ('ClearStorageKey', '
                    UPDATE OrleansStorage
                    SET
                        PayloadBinary = NULL,
                        ModifiedOn = datetime(''now''),
                        Version = Version + 1
                    WHERE
                        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                        AND Version = @GrainStateVersion;

                    SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                    WHERE changes() > 0
                        AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId;

                    SELECT @GrainStateVersion AS NewGrainStateVersion
                    WHERE changes() = 0
                        AND @GrainStateVersion IS NOT NULL;
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM OrleansQuery WHERE QueryKey IN ('WriteToStorageKey', 'ReadFromStorageKey', 'ClearStorageKey');");
            migrationBuilder.DropTable(name: "OrleansStorage");
        }
    }
}
