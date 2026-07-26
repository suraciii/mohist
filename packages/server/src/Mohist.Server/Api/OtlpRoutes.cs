using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Otel;
using Mohist.Server.Otel.OtlpJson;
using Mohist.Server.Otel.OtlpProtobuf;
using Google.Protobuf;

namespace Mohist.Server.Api;

/// <summary>
/// OTLP HTTP ingestion endpoint. Only mounted on the OTLP port
/// (the route group is filtered by <c>RequireHost</c>) so the main
/// API port never exposes this surface.
/// </summary>
/// <remarks>
/// OTLP responses intentionally bypass <see cref="ApiResults"/>:
/// the OTel spec mandates a literal <c>{}</c> body on success and a
/// specific JSON error envelope shape on failure, neither of which
/// fits the standard <c>ApiResponse&lt;T&gt;</c> envelope. See
/// design.md Decision 8.
/// </remarks>
public static class OtlpRoutes
{
    public const string OtlpTracesPath = "/otel/v1/traces";

    public static WebApplication MapOtlpRoutes(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var otelOptions = app.Services.GetRequiredService<IOptions<OtelOptions>>().Value;
        if (!otelOptions.Enabled)
            return app;

        var group = app.MapGroup("");

        group.MapPost(OtlpTracesPath, async (HttpContext context, TraceIngester ingester, OtlpTraceResponseWriter writer, IOtlpIngestGate gate, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Mohist.Server.Api.OtlpRoutes");

            var request = context.Request;
            var contentType = ResolveContentType(request.ContentType);

            var isJson = string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase);
            var isProtobuf = string.Equals(contentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(contentType, "application/protobuf", StringComparison.OrdinalIgnoreCase);

            if (!isJson && !isProtobuf)
            {
                logger.LogDebug(
                    "Rejecting OTLP request with unsupported Content-Type: {ContentType}.",
                    request.ContentType);
                await writer.WriteUnsupportedMediaAsync(
                    context.Response,
                    $"Unsupported Content-Type '{request.ContentType ?? "<missing>"}'.");
                return Results.Empty;
            }

            var admission = gate.TryAcquireRequestLease();
            if (!admission.Admitted)
            {
                logger.LogDebug(
                    "Rejecting OTLP request: temporary admission pressure (retry after {RetryAfter}s).",
                    admission.RetryAfterSeconds);
                await writer.WriteTemporaryAdmissionAsync(context.Response, contentType, admission.RetryAfterSeconds);
                return Results.Empty;
            }

            try
            {
                return await HandleAdmittedAsync(context, ingester, writer, logger, contentType, isProtobuf, ct);
            }
            finally
            {
                gate.ReleaseRequestLease();
            }
        });

        return app;
    }

    internal static async Task<IResult> HandleAdmittedAsync(
        HttpContext context,
        TraceIngester ingester,
        OtlpTraceResponseWriter writer,
        ILogger logger,
        string? contentType,
        bool isProtobuf,
        CancellationToken ct)
    {
        var request = context.Request;
        var maxBytes = LimitedOtlpBodyReader.DefaultMaxBytes;
        var bodyReader = new LimitedOtlpBodyReader(request.Body, maxBytes);

        byte[]? bodyBytes;
        try
        {
            bodyBytes = await bodyReader.ReadAllAsync(ct);
        }
        catch (OtlpBodyTooLargeException)
        {
            logger.LogDebug(
                "Rejecting OTLP request: decoded body exceeded {MaxBytes} bytes.",
                maxBytes);
            await writer.WriteDecodedSizeExceededAsync(context.Response, contentType);
            return Results.Empty;
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Failed to read OTLP request body.");
            await writer.WriteInvalidArgumentAsync(
                context.Response,
                contentType,
                "Failed to read request body.");
            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        IngestOutcome outcome;
        try
        {
            if (isProtobuf)
            {
                outcome = ingester.IngestBatch(OtlpProtobufTraceParser.Parse(bodyBytes!), ct);
            }
            else
            {
                var json = Encoding.UTF8.GetString(bodyBytes!);
                var parsed = JsonSerializer.Deserialize<OtlpTraceRequest>(json, OtlpJsonSerializer.Options())
                    ?? throw new JsonException("The OTLP request must be a JSON object.");
                outcome = ingester.IngestBatch(parsed, ct);
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "OTLP request body is not valid JSON.");
            await writer.WriteInvalidArgumentAsync(context.Response, contentType, ex.Message);
            return Results.Empty;
        }
        catch (InvalidProtocolBufferException ex)
        {
            logger.LogDebug(ex, "OTLP request body is not valid protobuf.");
            await writer.WriteInvalidArgumentAsync(context.Response, contentType, ex.Message);
            return Results.Empty;
        }

        logger.LogDebug(
            "OTLP ingest classified {SpanCount} parsed span(s); responding with {Disposition}.",
            outcome.Received,
            outcome.ResponseDisposition);
        await writer.WriteOutcomeAsync(context.Response, contentType, outcome);
        return Results.Empty;
    }

    private static string? ResolveContentType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var semi = raw.IndexOf(';');
        return semi < 0 ? raw.Trim() : raw[..semi].Trim();
    }
}
