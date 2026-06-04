using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mohist.Server.Infrastructure.Persistence.Db;

public static class MohistDatabaseMigrator
{
    private const string InitialCreateMigrationId = "20260530040459_InitialCreate";
    private const string ProjectVariablesBagMigrationId = "20260603090000_AddProjectVariablesBag";
    private const string WorkflowTemplateAndVariablesTablesMigrationId = "20260603200000_AddWorkflowTemplateAndVariablesTables";
    private const string InitialCreateProductVersion = "10.0.8";

    public static void Migrate(MohistDbContext db)
    {
        MarkInitialCreateAppliedForLegacyDatabase(db);
        db.Database.Migrate();
    }

    private static void MarkInitialCreateAppliedForLegacyDatabase(MohistDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State == System.Data.ConnectionState.Closed;
        if (wasClosed)
            connection.Open();

        try
        {
            if (!HistoryTableExists(db))
                return;
            if (InitialCreateAlreadyRecorded(db))
                return;
            if (!TableExists(db, "Configs"))
                return;

            CreateMissingInitialSchemaObjects(db);
            RecordAllMigrationsAsApplied(db);
        }
        finally
        {
            if (wasClosed)
                connection.Close();
        }
    }

    private static bool HistoryTableExists(MohistDbContext db) => TableExists(db, "__EFMigrationsHistory");

    private static bool InitialCreateAlreadyRecorded(MohistDbContext db)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = $migrationId
            LIMIT 1;
            """;
        AddParameter(command, "$migrationId", InitialCreateMigrationId);
        return command.ExecuteScalar() is not null;
    }

    private static void RecordAllMigrationsAsApplied(MohistDbContext db)
    {
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        foreach (var migrationId in migrationsAssembly.Migrations.Keys)
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ($migrationId, $productVersion);
                """;
            AddParameter(command, "$migrationId", migrationId);
            AddParameter(command, "$productVersion", InitialCreateProductVersion);
            command.ExecuteNonQuery();
        }
    }

    private static void CreateMissingInitialSchemaObjects(MohistDbContext db)
    {
        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ")
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ");

        db.Database.ExecuteSqlRaw(script);
    }

    private static bool TableExists(MohistDbContext db, string name)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM "sqlite_master"
            WHERE "type" = 'table' AND "name" = $name
            LIMIT 1;
            """;
        AddParameter(command, "$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
