using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.Otel;

namespace Mohist.Server.Api;

/// <summary>
/// Query side of the built-in OTel trace collector. The
/// <c>/otel/api/*</c> routes run on the main API port (no
/// <c>RequireHost</c> filter) and are protected from the OTLP port by
/// the <c>OtelPortIsolationMiddleware</c> installed in T-002.
/// </summary>
/// <remarks>
/// <para>All three endpoints wrap their payload in the standard
/// <see cref="ApiResponse{T}"/> envelope used by the rest of
/// <c>/api/*</c>; the OTLP ingest endpoint is the only OTel surface that
/// bypasses the envelope.</para>
/// <para>The free-SQL endpoint
/// (<c>POST /otel/api/query</c>) sits behind a three-layer safety net
/// a top-level keyword allow-list enforced by
/// <see cref="TraceQuerier.ValidateSelectOnly"/>, the
/// <see cref="OtelDb.ReadOnlyConnectionString"/> opened by the querier
/// (physically refuses writes), an injected execution budget that interrupts
/// active SQLite work, and <see cref="TraceQuerier.QueryCommandTimeout"/>
/// as lock-wait defense-in-depth.</para>
/// </remarks>
public static class OtelQueryRoutes
{
    public const string ListTracesPath = "/otel/api/traces";
    public const string QueryPath = "/otel/api/query";
    public const string StatusPath = "/otel/api/status";

    private static readonly JsonSerializerOptions QueryRequestOptions = JSON.Options;

    public static WebApplication MapOtelQueryRoutes(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var otelOptions = app.Services.GetRequiredService<IOptions<OtelOptions>>().Value;
        var group = app.MapGroup("/otel/api");

        group.MapGet("/traces", async (
            int? limit,
            string? service,
            TraceQuerier querier,
            CancellationToken ct) =>
        {
            var rows = await querier.ListAsync(limit, service, ct);
            return ApiResults.Ok(rows);
        });

        group.MapGet("/status", async (TraceQuerier querier, CancellationToken ct) =>
        {
            var snapshot = await querier.GetStatusAsync(ct);
            return ApiResults.Ok(snapshot);
        });

        group.MapPost("/query", async (
            HttpRequest request,
            IOtelQueryExecutor queryExecutor,
            ILoggerFactory loggerFactory,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Mohist.Server.Api.OtelQueryRoutes");

            if (request.ContentLength > TraceQuerier.MaxQueryRequestBodyBytes)
            {
                return ApiResults.PayloadTooLarge(
                    "Query request body is too large.",
                    "query_request_too_large");
            }

            string body;
            using (var reader = new StreamReader(request.Body))
            {
                try
                {
                    body = await reader.ReadToEndAsync(ct);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    logger.LogDebug(ex, "The /otel/api/query request body is too large.");
                    return ApiResults.PayloadTooLarge(
                        "Query request body is too large.",
                        "query_request_too_large");
                }
                catch (IOException ex)
                {
                    logger.LogDebug(ex, "Failed to read /otel/api/query request body.");
                    return ApiResults.BadRequest("Failed to read request body.");
                }
            }

            QueryRequest? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<QueryRequest>(body, QueryRequestOptions);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "/otel/api/query body is not valid JSON.");
                return ApiResults.BadRequest(
                    $"Invalid JSON body: {ex.Message}",
                    "query_malformed");
            }

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Sql))
            {
                return ApiResults.BadRequest(
                    "Missing required field 'sql'.",
                    "query_missing_sql");
            }

            var rejection = TraceQuerier.ValidateSelectOnly(parsed.Sql);
            if (rejection is not null)
            {
                return ApiResults.BadRequest(rejection, "query_not_select");
            }

            using var budgetCts = new CancellationTokenSource();
            using var budgetTimer = timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                budgetCts,
                TimeSpan.FromSeconds(TraceQuerier.QueryExecutionBudgetSeconds),
                Timeout.InfiniteTimeSpan);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);

            QueryResult result;
            try
            {
                result = await queryExecutor.Execute(parsed.Sql, linkedCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested)
            {
                return ApiResults.BadRequest(
                    "Query execution exceeded its budget.",
                    "query_execution_budget_exhausted");
            }
            catch (SqliteException ex)
            {
                if (ct.IsCancellationRequested)
                    throw;

                if (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return ApiResults.BadRequest(
                        "Query execution exceeded its budget.",
                        "query_execution_budget_exhausted");
                }

                // Includes "no such table" / "syntax error" /
                // "attempt to write a readonly database" — the SQLite
                // engine is the ultimate source of truth on these.
                logger.LogDebug(
                    ex,
                    "SQLite rejected /otel/api/query: {SqliteError}",
                    ex.SqliteErrorCode);
                return ApiResults.BadRequest(
                    $"SQLite error: {ex.Message}",
                    "query_sqlite_error",
                    new { sqliteErrorCode = ex.SqliteErrorCode });
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to execute /otel/api/query.");
                return ApiResults.BadRequest(
                    $"Query failed: {ex.Message}",
                    "query_failed");
            }

            return ApiResults.Ok(result);
        }).WithMetadata(new RequestSizeLimitAttribute(TraceQuerier.MaxQueryRequestBodyBytes));

        return app;
    }

    /// <summary>Body of <c>POST /otel/api/query</c>.</summary>
    private sealed class QueryRequest
    {
        public string? Sql { get; set; }
    }
}
