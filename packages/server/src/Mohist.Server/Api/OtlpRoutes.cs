using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;
using Mohist.Server.Otel.OtlpProtobuf;

namespace Mohist.Server.Api;

/// <summary>
/// OTLP HTTP/JSON ingestion endpoint. Only mounted on the OTLP port
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

        var otlpPort = otelOptions.Port;

        var group = app.MapGroup("");

        group.MapPost(OtlpTracesPath, async (HttpContext context, TraceIngester ingester, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Mohist.Server.Api.OtlpRoutes");

            if (ResolveLocalPort(context) != otlpPort)
            {
                return Results.NotFound();
            }

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
                return Results.Json(
                    OtlpError.NotAcceptable(request.ContentType),
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    contentType: "application/json");
            }

            string? body = null;
            byte[]? protobufBody = null;
            try
            {
                if (isProtobuf)
                {
                    using var memory = new MemoryStream();
                    await request.Body.CopyToAsync(memory, ct);
                    protobufBody = memory.ToArray();
                }
                else
                {
                    using var reader = new StreamReader(request.Body);
                    body = await reader.ReadToEndAsync(ct);
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Failed to read OTLP request body.");
                return Results.Json(
                    OtlpError.BadBody("Failed to read request body."),
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json");
            }

            int spansWritten;
            try
            {
                spansWritten = isProtobuf
                    ? ingester.Ingest(OtlpProtobufTraceParser.Parse(protobufBody!), ct)
                    : ingester.IngestJson(body!, ct);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex, "OTLP request body is not valid JSON.");
                return Results.Json(
                    OtlpError.BadBody(ex.Message),
                    statusCode: StatusCodes.Status400BadRequest,
                    contentType: "application/json");
            }

            logger.LogDebug(
                "OTLP ingest accepted {SpanCount} span(s); responding with empty JSON object.",
                spansWritten);

            return Results.Json(
                OtlpSuccess.Empty,
                statusCode: StatusCodes.Status200OK,
                contentType: "application/json");
        });

        return app;
    }

    private static string? ResolveContentType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var semi = raw.IndexOf(';');
        return semi < 0 ? raw.Trim() : raw[..semi].Trim();
    }

    private static int ResolveLocalPort(HttpContext context)
    {
        var isTesting = context.RequestServices.GetRequiredService<IHostEnvironment>()
            .IsEnvironment(MohistHostEnvironment.Testing);
        if (isTesting
            && context.Request.Headers.TryGetValue("X-Mohist-Test-Local-Port", out var value)
            && int.TryParse(value, out var localPort))
            return localPort;
        return context.Connection.LocalPort;
    }
}

/// <summary>
/// Minimal OTLP success envelope. Per the OTel spec the response body
/// must be an empty JSON object (<c>{}</c>) — no <c>partialSuccess</c>
/// field is needed because we never apply server-side filtering.
/// </summary>
internal static class OtlpSuccess
{
    public static readonly object Empty = new { };
}

/// <summary>
/// OTLP error envelope. The OTel spec is permissive about error shape;
/// we emit a JSON object with a stable <c>error</c> message so
/// HTTP-aware clients can debug without parsing status text.
/// </summary>
internal static class OtlpError
{
    public static object BadBody(string message) => new { error = message };

    public static object NotAcceptable(string? contentType) => new
    {
        error = $"Unsupported Content-Type '{contentType ?? "<missing>"}'. Only application/json is supported.",
    };
}
