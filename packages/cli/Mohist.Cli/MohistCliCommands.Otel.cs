using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Mohist.Cli;

/// <summary>
/// Result of a <c>mo otel query</c> execution: the column names plus the
/// materialized rows. Kept framework-agnostic so tests can supply a fake
/// executor without touching <see cref="SqliteConnection"/>.
/// </summary>
internal sealed record OtelQueryResult(string[] Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// Executes a SQL statement against the OTel SQLite database. The default
/// production implementation (<see cref="SqliteOtelQueryExecutor"/>) opens a
/// read-only <see cref="SqliteConnection"/>; tests inject a fake so they never
/// touch a real SQLite file (design/testing.md hard-constraint 1).
/// </summary>
internal interface IOtelQueryExecutor
{
    /// <summary>
    /// Runs <paramref name="sql"/> against <paramref name="databasePath"/>.
    /// Returns the column names and rows on success; throws
    /// <see cref="OtelQueryException"/> for SQL/SQLite errors so the caller can
    /// surface a uniform diagnostic.
    /// </summary>
    Task<OtelQueryResult> ExecuteAsync(string databasePath, string sql, CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps a <see cref="SqliteException"/> (or other SQLite-engine failure) so the
/// command layer can distinguish SQLite errors from other failures when deciding
/// which diagnostic to print.
/// </summary>
internal sealed class OtelQueryException : Exception
{
    public bool IsReadOnlyViolation { get; }

    public OtelQueryException(string message, bool isReadOnlyViolation = false)
        : base(message)
    {
        IsReadOnlyViolation = isReadOnlyViolation;
    }

    public OtelQueryException(string message, Exception inner, bool isReadOnlyViolation = false)
        : base(message, inner)
    {
        IsReadOnlyViolation = isReadOnlyViolation;
    }
}

/// <summary>
/// Production <see cref="IOtelQueryExecutor"/>: opens a read-only
/// <see cref="SqliteConnection"/> against <paramref name="databasePath"/>,
/// materializes the result set, and translates SQLite errors into
/// <see cref="OtelQueryException"/>.
/// </summary>
internal sealed class SqliteOtelQueryExecutor : IOtelQueryExecutor
{
    /// <summary>Wall-clock timeout for a single query.</summary>
    private static readonly TimeSpan QueryCommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<OtelQueryResult> ExecuteAsync(string databasePath, string sql, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = OpenReadOnlyConnection(databasePath);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = (int)QueryCommandTimeout.TotalSeconds;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var fieldCount = reader.FieldCount;
            var columnNames = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                columnNames[i] = reader.GetName(i);
            }

            var rows = new List<object?[]>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            return new OtelQueryResult(columnNames, rows);
        }
        catch (SqliteException ex)
        {
            // SQLite error 8 ("attempt to write a readonly database") surfaces as
            // the "readonly" message users see for write attempts against otel.db.
            var isReadOnly = ex.Message.Contains("readonly", StringComparison.OrdinalIgnoreCase);
            throw new OtelQueryException($"SQLite error: {ex.Message}", isReadOnly);
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
}

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

    public static Command Build(MohistCliApi api, IEnvironmentVariableProvider environment, IOtelQueryExecutor queryExecutor)
    {
        var otel = new Command("otel", "OpenTelemetry trace collection and query commands");
        otel.Subcommands.Add(BuildQuery(api, environment, queryExecutor));
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

        var home = environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, DataDirectoryName);
    }

    private static Command BuildQuery(MohistCliApi api, IEnvironmentVariableProvider environment, IOtelQueryExecutor queryExecutor)
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
            return RunQueryAsync(api, environment, queryExecutor, sql, dbPath);
        });
        return cmd;
    }

    private static Command BuildStatus(MohistCliApi api)
    {
        var cmd = new Command("status", "Show OTel collector status and database statistics (requires server)");
        cmd.SetAction(_ => RunStatusAsync(api));
        return cmd;
    }

    private static async Task<int> RunQueryAsync(MohistCliApi api, IEnvironmentVariableProvider environment, IOtelQueryExecutor queryExecutor, string? sql, string? dbPath)
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
            var result = await queryExecutor.ExecuteAsync(resolvedPath, sql).ConfigureAwait(false);

            await RenderTableAsync(api.Output, result.Columns, result.Rows).ConfigureAwait(false);
            if (result.Rows.Count == 0)
            {
                await api.Output.WriteLineAsync("(0 rows)").ConfigureAwait(false);
            }
            return 0;
        }
        catch (OtelQueryException ex)
        {
            await api.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
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
            using var response = await api.SendAsync(HttpMethod.Get, StatusPath, body: null).ConfigureAwait(false);
            if (response is null)
                return 1;

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
        catch (InvalidDataException ex)
        {
            await api.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
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
        var status = data?["status"]?.GetValue<string>();
        if (status is not ("off" or "healthy" or "degraded"))
            throw new InvalidDataException("Server returned an invalid OTel status payload.");

        var storage = data?["storage"]?.AsObject()
            ?? throw new InvalidDataException("Server returned an incomplete OTel status payload.");
        var telemetry = data?["telemetry"]?.AsObject()
            ?? throw new InvalidDataException("Server returned an incomplete OTel status payload.");
        var process = data?["process"]?.AsObject()
            ?? throw new InvalidDataException("Server returned an incomplete OTel status payload.");

        await output.WriteLineAsync($"status: {status}").ConfigureAwait(false);
        await output.WriteLineAsync($"collector_online: {data?["collector_online"]?.GetValue<bool>() ?? false}").ConfigureAwait(false);
        await output.WriteLineAsync($"since: {data?["since"]?.GetValue<string>() ?? ""}").ConfigureAwait(false);
        await output.WriteLineAsync("storage:").ConfigureAwait(false);
        await output.WriteLineAsync($"  usage_bytes: {RenderJsonValue(storage["usage_bytes"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  budget_bytes: {RenderJsonValue(storage["budget_bytes"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  growth_bytes_per_second: {RenderJsonValue(storage["growth_bytes_per_second"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  growth_window_seconds: {RenderJsonValue(storage["growth_window_seconds"])}").ConfigureAwait(false);
        await output.WriteLineAsync("telemetry:").ConfigureAwait(false);
        await output.WriteLineAsync($"  received_spans: {RenderJsonValue(telemetry["received_spans"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  saved_spans: {RenderJsonValue(telemetry["saved_spans"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  rejected_spans: {RenderJsonValue(telemetry["rejected_spans"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  dropped_spans: {RenderJsonValue(telemetry["dropped_spans"])}").ConfigureAwait(false);
        await output.WriteLineAsync("process:").ConfigureAwait(false);
        await output.WriteLineAsync($"  cpu_utilization: {RenderJsonValue(process["cpu_utilization"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  working_set_bytes: {RenderJsonValue(process["working_set_bytes"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"  gc_heap_bytes: {RenderJsonValue(process["gc_heap_bytes"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"latest_degradation: {RenderJsonValue(data?["latest_degradation"])}").ConfigureAwait(false);
        await output.WriteLineAsync($"routes: {data?["routes"]?.AsArray().Count ?? 0}").ConfigureAwait(false);
    }

    private static string RenderJsonValue(JsonNode? value) => value is null || value is JsonValue { } jsonValue && jsonValue.ToJsonString() == "null"
        ? "null"
        : value?.ToJsonString() ?? "null";
}
