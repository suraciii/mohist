using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

/// <summary>
/// <c>mo otel</c> command group. Provides <c>query</c> (HTTP
/// submission to <c>POST /otel/api/query</c>), <c>status</c>
/// (HTTP probe of <c>GET /otel/api/status</c>), and <c>traces</c>
/// (typed recent-traces list against <c>GET /otel/api/traces</c>).
/// All subcommands go through the Server; the CLI never opens the
/// local SQLite database. See
/// <c>openspec/changes/issue-529/specs/otel-cli-query/spec.md</c>
/// and <c>openspec/changes/issue-530/specs/otel-cli-traces/spec.md</c>.
/// </summary>
internal static class OtelCommands
{
    private const string StatusPath = "/otel/api/status";
    private const string QueryPath = "/otel/api/query";
    private const string TracesPath = "/otel/api/traces";

    internal static readonly ResourceDescriptor QueryDescriptor = new(
        ResourceCardinality.Single,
        ["columns", "rows", "truncated", "truncate_reason"]);

    internal static readonly ResourceDescriptor TracesDescriptor = new(
        ResourceCardinality.Collection,
        ["trace_id", "service_name", "start_time", "end_time", "span_count"]);

    public static Command Build(MohistCliApi api)
    {
        var otel = new Command("otel", "OpenTelemetry trace collection and query commands");
        otel.Subcommands.Add(BuildQuery(api));
        otel.Subcommands.Add(BuildStatus(api));
        otel.Subcommands.Add(BuildTraces(api));
        return otel;
    }

    private static Command BuildQuery(MohistCliApi api)
    {
        var cmd = new Command("query", "Run a SQL query against the OTel SQLite database through the Server");
        var sqlArg = new Argument<string?>("sql")
        {
            Description = "SQL statement to execute (e.g. \"SELECT COUNT(*) FROM traces\")",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(QueryDescriptor);
        cmd.Arguments.Add(sqlArg);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var sql = ctx.GetValue(sqlArg);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return RunQueryAsync(api, sql, jsonProvided, json);
        });
        return cmd;
    }

    private static Command BuildStatus(MohistCliApi api)
    {
        var cmd = new Command("status", "Show OTel collector status and database statistics (requires server)");
        cmd.SetAction(_ => RunStatusAsync(api));
        return cmd;
    }

    private static Command BuildTraces(MohistCliApi api)
    {
        var cmd = new Command(
            "traces",
            "List recent traces (most-recent first) through the Server. Use --service to restrict to one service and --limit to request more rows; for arbitrary SQL exploration use 'mo otel query'.");
        var serviceOpt = new Option<string?>("--service")
        {
            Description = "Restrict results to a single service_name (exact match)",
        };
        var limitOpt = new Option<int?>("--limit")
        {
            Description = "Maximum number of traces to request (Server applies its own default and cap)",
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(TracesDescriptor);
        cmd.Options.Add(serviceOpt);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var service = ctx.GetValue(serviceOpt);
            var limit = ctx.GetValue(limitOpt);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return RunTracesAsync(api, service, limit, jsonProvided, json);
        });
        return cmd;
    }

    private static async Task<int> RunTracesAsync(
        MohistCliApi api,
        string? service,
        int? limit,
        bool jsonProvided,
        string? json)
    {
        var selection = JsonSelection.Parse(TracesDescriptor, jsonProvided, json);
        if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
            return api.WriteJsonSelectionResult(TracesDescriptor, selection);

        var path = BuildTracesPath(service, limit);
        var (exitCode, data) = await api.GetDataOrPrintErrorAsync(path).ConfigureAwait(false);
        if (exitCode != 0)
            return exitCode;

        if (selection.Kind == JsonSelectionKind.Selected)
        {
            var projected = selection.Project(data, TracesDescriptor.Cardinality);
            return await new CliResultWriter(api.Invocation)
                .WriteSuccessAsync(projected)
                .ConfigureAwait(false);
        }

        return await api.RenderTableAsync(data, MohistCliApi.TableShape.OtelTracesList).ConfigureAwait(false);
    }

    private static string BuildTracesPath(string? service, int? limit)
    {
        var hasLimit = limit.HasValue;
        var hasService = !string.IsNullOrWhiteSpace(service);
        if (!hasLimit && !hasService)
            return TracesPath;

        var separator = '?';
        var query = new StringBuilder();
        if (hasLimit)
        {
            query.Append(separator).Append("limit=").Append(limit!.Value.ToString(CultureInfo.InvariantCulture));
            separator = '&';
        }
        if (hasService)
        {
            query.Append(separator)
                .Append("service=")
                .Append(Uri.EscapeDataString(service!.Trim()));
        }
        return TracesPath + query.ToString();
    }

    private static async Task<int> RunQueryAsync(MohistCliApi api, string? sql, bool jsonProvided, string? json)
    {
        var selection = JsonSelection.Parse(QueryDescriptor, jsonProvided, json);
        if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
            return api.WriteJsonSelectionResult(QueryDescriptor, selection);

        if (string.IsNullOrWhiteSpace(sql))
        {
            await api.Error.WriteLineAsync(
                "mo otel query requires a SQL argument (e.g. mo otel query \"SELECT COUNT(*) FROM traces\")")
                .ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.OperationFailure);
        }

        HttpResponseMessage? response;
        try
        {
            response = await api.SendAsync(HttpMethod.Post, QueryPath, new { sql }).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await api.Error.WriteLineAsync(MohistCliApi.ServerUnavailableMessage).ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.OperationFailure);
        }
        catch (TaskCanceledException)
        {
            await api.Error.WriteLineAsync(MohistCliApi.ServerUnavailableMessage).ConfigureAwait(false);
            return CliExitCode.For(CliExitOutcome.OperationFailure);
        }

        if (response is null)
            return CliExitCode.For(CliExitOutcome.OperationFailure);

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonNode? node = string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);

            var envelope = MohistCliApi.ExtractEnvelope(node, response);
            if (!envelope.HasBody)
            {
                await api.Error.WriteLineAsync(
                    $"Server returned an empty response with status {(int)response.StatusCode}.")
                    .ConfigureAwait(false);
                return CliExitCode.For(CliExitOutcome.OperationFailure);
            }

            if (!envelope.Success)
            {
                await api.Error.WriteLineAsync($"{envelope.Error} (code={envelope.Code})").ConfigureAwait(false);
                return CliExitCode.For(CliExitOutcome.OperationFailure);
            }

            var data = envelope.Data
                ?? throw new InvalidDataException("Server returned a query response without data.");

            if (selection.Kind == JsonSelectionKind.Selected)
            {
                var projected = selection.Project(data, QueryDescriptor.Cardinality);
                await api.Output.WriteLineAsync(projected.ToJsonString(MohistCliApi.JsonOutputOptions))
                    .ConfigureAwait(false);
                return CliExitCode.For(CliExitOutcome.Success);
            }

            var columns = ReadColumns(data);
            var rows = ReadRows(data);
            var truncated = data["truncated"]?.GetValue<bool>() ?? false;
            var truncateReason = data["truncate_reason"]?.GetValue<string>();

            await RenderTableAsync(api.Output, columns, rows).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                await api.Output.WriteLineAsync("(0 rows)").ConfigureAwait(false);
            }
            if (truncated)
            {
                var reason = string.IsNullOrWhiteSpace(truncateReason) ? "unknown" : truncateReason;
                await api.Output.WriteLineAsync($"(truncated: {reason})").ConfigureAwait(false);
            }
            return CliExitCode.For(CliExitOutcome.Success);
        }
    }

    private static IReadOnlyList<string> ReadColumns(JsonNode data)
    {
        if (data["columns"] is JsonArray columnArray)
        {
            var columns = new List<string>(columnArray.Count);
            foreach (var item in columnArray)
            {
                var value = item?.GetValue<string>();
                columns.Add(value ?? string.Empty);
            }
            return columns;
        }
        return Array.Empty<string>();
    }

    private static IReadOnlyList<Dictionary<string, object?>> ReadRows(JsonNode data)
    {
        if (data["rows"] is not JsonArray rowArray)
            return Array.Empty<Dictionary<string, object?>>();

        var rows = new List<Dictionary<string, object?>>(rowArray.Count);
        foreach (var item in rowArray)
        {
            if (item is JsonObject obj)
            {
                var row = new Dictionary<string, object?>(obj.Count, StringComparer.Ordinal);
                foreach (var entry in obj)
                    row[entry.Key] = JsonNodeToObject(entry.Value);
                rows.Add(row);
            }
            else
            {
                rows.Add(new Dictionary<string, object?>(StringComparer.Ordinal));
            }
        }
        return rows;
    }

    private static object? JsonNodeToObject(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<bool>(out var b)) return b;
            return value.ToJsonString();
        }
        if (node is JsonArray || node is JsonObject)
            return node.ToJsonString();
        return null;
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
            await api.Error.WriteLineAsync(MohistCliApi.ServerUnavailableMessage).ConfigureAwait(false);
            return 1;
        }
        catch (InvalidDataException ex)
        {
            await api.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task RenderTableAsync(TextWriter output, IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        if (columns.Count == 0)
        {
            return;
        }

        var columnWidths = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            columnWidths[i] = columns[i].Length;
        }

        var renderedRows = new string[rows.Count][];
        for (var r = 0; r < rows.Count; r++)
        {
            renderedRows[r] = new string[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                rows[r].TryGetValue(columns[i], out var cell);
                var rendered = RenderCell(cell);
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
        var routes = data?["routes"]?.AsArray();
        await output.WriteLineAsync($"routes: {routes?.Count ?? 0}").ConfigureAwait(false);
        if (routes is not null)
        {
            foreach (var route in routes)
            {
                await output.WriteLineAsync($"  route: {RenderJsonValue(route?["route"])}").ConfigureAwait(false);
                await output.WriteLineAsync($"    request_count: {RenderJsonValue(route?["request_count"])}").ConfigureAwait(false);
                await output.WriteLineAsync($"    average_duration_ms: {RenderJsonValue(route?["average_duration_ms"])}").ConfigureAwait(false);
                await output.WriteLineAsync($"    max_duration_ms: {RenderJsonValue(route?["max_duration_ms"])}").ConfigureAwait(false);
                await output.WriteLineAsync($"    database_calls_per_request: {RenderJsonValue(route?["database_calls_per_request"])}").ConfigureAwait(false);
                await output.WriteLineAsync($"    downstream_calls_per_request: {RenderJsonValue(route?["downstream_calls_per_request"])}").ConfigureAwait(false);
            }
        }
    }

    private static string RenderJsonValue(JsonNode? value) => value is null || value is JsonValue { } jsonValue && jsonValue.ToJsonString() == "null"
        ? "null"
        : value?.ToJsonString() ?? "null";
}
