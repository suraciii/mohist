using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Mohist.Server.Storage.Db;

public static class MohistDatabaseInitializer
{
    private static readonly Regex CreateTableRegex = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?""?(\w+)""?\s*\((?>[^()]+|\((?<Depth>)|\)(?<-Depth>))*(?(Depth)(?!))\);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CreateIndexRegex = new(
        @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?""?(\w+)""?\s+ON\s+""?\w+""?\s*\((?>[^()]+|\((?<Depth>)|\)(?<-Depth>))*(?(Depth)(?!))\);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    public static void Initialize(MohistDbContext db)
    {
        var script = db.Database.GenerateCreateScript();
        var connection = db.Database.GetDbConnection();
        var needsOpen = connection.State != System.Data.ConnectionState.Open;

        if (needsOpen) connection.Open();

        try
        {
            ExecuteStatementsInTransaction(connection, script);
        }
        finally
        {
            if (needsOpen) connection.Close();
        }
    }

    private static void ExecuteStatementsInTransaction(System.Data.Common.DbConnection connection, string script)
    {
        var tableMatches = CreateTableRegex.Matches(script);
        var indexMatches = CreateIndexRegex.Matches(script);

        if (tableMatches.Count == 0 && indexMatches.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();

        try
        {
            foreach (Match match in tableMatches)
            {
                ExecuteStatement(connection, WrapWithIfNotExists(match.Value));
            }

            foreach (Match match in indexMatches)
            {
                ExecuteStatement(connection, WrapWithIfNotExists(match.Value));
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string WrapWithIfNotExists(string statement)
    {
        if (statement.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return statement;
        }

        if (statement.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
        {
            return statement.Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase);
        }

        if (statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase))
        {
            return statement.Replace("CREATE UNIQUE INDEX", "CREATE UNIQUE INDEX IF NOT EXISTS", StringComparison.OrdinalIgnoreCase);
        }

        if (statement.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase))
        {
            return statement.Replace("CREATE INDEX", "CREATE INDEX IF NOT EXISTS", StringComparison.OrdinalIgnoreCase);
        }

        return statement;
    }

    private static void ExecuteStatement(System.Data.Common.DbConnection connection, string statement)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = statement;
        cmd.ExecuteNonQuery();
    }
}
