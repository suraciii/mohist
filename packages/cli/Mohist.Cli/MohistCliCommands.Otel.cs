using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Mohist.Cli;

/// <summary>
/// <c>mo otel</c> command group. Provides <c>query</c> (direct
/// read-only SQLite against <c>otel.db</c>) and <c>status</c> (HTTP
/// probe of <c>GET /otel/api/status</c>). See
/// <c>openspec/changes/issue-219/specs/otel-cli/spec.md</c>.
/// </summary>
internal static class OtelCommands
{
    /// <summary>Default name of the OTel SQLite database file.</summary>
    public const string DefaultDatabaseFileName = "otel.db";

    /// <summary>Default data directory under <c>$HOME</c>.</summary>
    public const string DataDirectoryName = ".mohist";

    public const string MainDbPathEnvironmentVariable = "MOHIST_DB_PATH";

    private const string StatusPath = "/otel/api/status";

    /// <summary>Wall-clock timeout for a single <c>mo otel query</c>.</summary>
    private static readonly TimeSpan QueryCommandTimeout = TimeSpan.FromSeconds(30);

    public static Command Build(MohistCliApi api, IEnvironmentVariableProvider environment)
    {
        var otel = new Command("otel", "OpenTelemetry trace collection and query commands");
        otel.Subcommands.Add(BuildQuery(api, environment));
        otel.Subcommands.Add(BuildStatus(api));
        return otel;
    }

    /// <summary>
    /// Resolves the absolute path of the <c>otel.db</c> file. When
    /// <paramref name="dbPath"/> is supplied the value is returned
    /// (after <see cref="Path.GetFullPath"/>); otherwise the default
    /// <c>otel.db</c> next to the configured main database is returned.
    /// </summary>
    public static string ResolveDatabasePath(string? dbPath, IEnvironmentVariableProvider environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!string.IsNullOrWhiteSpace(dbPath))
            return Path.GetFullPath(dbPath);

        return Path.GetFullPath(Path.Combine(ResolveDefaultDataDirectory(environment), DefaultDatabaseFileName));
    }

    private static string ResolveDefaultDataDirectory(IEnvironmentVariableProvider environment)
    {
        var mainDbPath = environment.GetEnvironmentVariable(MainDbPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(mainDbPath))
        {
            var fullMainDbPath = Path.GetFullPath(mainDbPath);
            var directory = Path.GetDirectoryName(fullMainDbPath);
            if (!string.IsNullOrWhiteSpace(directory))
                return directory;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, DataDirectoryName);
    }

    private static Command BuildQuery(MohistCliApi api, IEnvironmentVariableProvider environment)
    {
        var cmd = new Command("query", "Run a SQL query against otel.db directly (does not require the server)");
        var sqlArg = new Argument<string?>("sql")
        {
            Description = "SQL statement to execute (e.g. \"SELECT COUNT(*) FROM traces\")",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var dbOpt = new Option<string?>("--db", "-d")
        {
            Description = "Path to the otel.db file (defaults to ~/.mohist/otel.db)",
        };
        cmd.Arguments.Add(sqlArg);
        cmd.Options.Add(dbOpt);
        cmd.SetAction(ctx =>
        {
            var sql = ctx.GetValue(sqlArg);
            var dbPath = ctx.GetValue(dbOpt);
            return RunQueryAsync(api, environment, sql, dbPath);
        });
        return cmd;
    }

    private static Command BuildStatus(MohistCliApi api)
    {
        var cmd = new Command("status", "Show OTel collector status and database statistics (requires server)");
        cmd.SetAction(_ => RunStatusAsync(api));
        return cmd;
    }

    private static async Task<int> RunQueryAsync(MohistCliApi api, IEnvironmentVariableProvider environment, string? sql, string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            await api.Error.WriteLineAsync(
                "mo otel query requires a SQL argument (e.g. mo otel query \"SELECT COUNT(*) FROM traces\")")
                .ConfigureAwait(false);
            return 1;
        }

        var resolvedPath = ResolveDatabasePath(dbPath, environment);
        if (!api.FileSystem.Exists(resolvedPath))
        {
            await api.Error.WriteLineAsync(
                $"otel.db not found at '{resolvedPath}'. Start the server to create it, or pass --db <path>.")
                .ConfigureAwait(false);
            return 1;
        }

        try
        {
            await using var connection = OpenReadOnlyConnection(resolvedPath);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = (int)QueryCommandTimeout.TotalSeconds;

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            var fieldCount = reader.FieldCount;
            var columnNames = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                columnNames[i] = reader.GetName(i);
            }

            var rows = new List<object?[]>();
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var row = new object?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            await RenderTableAsync(api.Output, columnNames, rows).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                await api.Output.WriteLineAsync("(0 rows)").ConfigureAwait(false);
            }
            return 0;
        }
        catch (SqliteException ex)
        {
            await api.Error.WriteLineAsync($"SQLite error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await api.Error.WriteLineAsync($"Failed to execute query: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunStatusAsync(MohistCliApi api)
    {
        try
        {
            using var response = await api.Http.GetAsync(StatusPath).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream).ConfigureAwait(false);

            var envelope = MohistCliApi.ExtractEnvelope(node, response);
            if (!envelope.HasBody)
            {
                await api.Error.WriteLineAsync(
                    $"Server returned an empty response with status {(int)response.StatusCode}.")
                    .ConfigureAwait(false);
                return 1;
            }

            if (!envelope.Success)
            {
                await api.Error.WriteLineAsync(envelope.Error).ConfigureAwait(false);
                return 1;
            }

            await RenderStatusAsync(api.Output, envelope.Data).ConfigureAwait(false);
            return 0;
        }
        catch (HttpRequestException)
        {
            await api.Error.WriteLineAsync(MohistCliApi.ServerUnavailableMessage).ConfigureAwait(false);
            return 1;
        }
        catch (TaskCanceledException)
        {
            // Timeout: server is reachable but slow. Surface the same
            // "not running" diagnostic rather than a stack trace so the
            // user is nudged toward the right next step.
            await api.Error.WriteLineAsync(MohistCliApi.ServerUnavailableMessage).ConfigureAwait(false);
            return 1;
        }
    }

    private static SqliteConnection OpenReadOnlyConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static async Task RenderTableAsync(TextWriter output, string[] columns, IReadOnlyList<object?[]> rows)
    {
        if (columns.Length == 0)
        {
            return;
        }

        var columnWidths = new int[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            columnWidths[i] = columns[i].Length;
        }

        var renderedRows = new string[rows.Count][];
        for (var r = 0; r < rows.Count; r++)
        {
            renderedRows[r] = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                var rendered = RenderCell(rows[r][i]);
                renderedRows[r][i] = rendered;
                if (rendered.Length > columnWidths[i])
                    columnWidths[i] = rendered.Length;
            }
        }

        await output.WriteLineAsync(BuildRow(columns, columnWidths)).ConfigureAwait(false);
        await output.WriteLineAsync(BuildSeparator(columnWidths)).ConfigureAwait(false);

        for (var r = 0; r < renderedRows.Length; r++)
        {
            await output.WriteLineAsync(BuildRow(renderedRows[r], columnWidths)).ConfigureAwait(false);
        }
    }

    private static string BuildRow(IReadOnlyList<string> cells, int[] widths)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
                sb.Append("  ");
            sb.Append(cells[i].PadRight(widths[i]));
        }
        return sb.ToString();
    }

    private static string BuildSeparator(int[] widths)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < widths.Length; i++)
        {
            if (i > 0)
                sb.Append("  ");
            sb.Append(new string('-', widths[i]));
        }
        return sb.ToString();
    }

    private static string RenderCell(object? value)
    {
        if (value is null || value is DBNull)
            return string.Empty;
        if (value is byte[] bytes)
            return "0x" + Convert.ToHexString(bytes);
        if (value is DateTime dt)
            return dt.ToString("O", CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto)
            return dto.ToString("O", CultureInfo.InvariantCulture);
        if (value is double d)
            return d.ToString("R", CultureInfo.InvariantCulture);
        if (value is float f)
            return f.ToString("R", CultureInfo.InvariantCulture);
        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? string.Empty;
    }

    private static async Task RenderStatusAsync(TextWriter output, JsonNode? data)
    {
        var collectorOnline = data?["collector_online"]?.GetValue<bool>() ?? false;
        var dbSize = data?["db_size_bytes"]?.GetValue<long>() ?? 0L;
        var traceCount = data?["trace_count"]?.GetValue<long>() ?? 0L;
        var spanCount = data?["span_count"]?.GetValue<long>() ?? 0L;

        await output.WriteLineAsync($"collector: {(collectorOnline ? "online" : "offline")}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"db_size_bytes: {dbSize}").ConfigureAwait(false);
        await output.WriteLineAsync($"trace_count: {traceCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"span_count: {spanCount}").ConfigureAwait(false);
    }
}
