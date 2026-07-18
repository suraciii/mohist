using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Events.Matching;

namespace Mohist.Server.Api;

/// <summary>
/// NDJSON streaming tail of the project-scoped live event envelope
/// (issue-413 T-002). The endpoint compiles the optional
/// <c>?match=</c> expression as the single authority (400 with a
/// structured location diagnostic before any stream on failure), opens a
/// transient strictly project-scoped subscription against
/// <see cref="IEventTailSource"/>, and writes one compact JSON envelope
/// object (core fields + extensions, no payload) per line until the
/// client disconnects or cancels. Events before subscription are not
/// replayed (best-effort, transient).
/// </summary>
public static class ProjectEventTailRoutes
{
    public const string ContentType = "application/x-ndjson";

    public static WebApplication MapProjectEventTailRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/events/tail")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("", (HttpContext context, string? match, IEventTailSource source, CancellationToken ct)
            => HandleTailAsync(context, match, source, ct));

        return app;
    }

    /// <summary>
    /// Direct-invocation seam for the tail handler. Production wiring
    /// registers this through the route table; spec tests invoke it
    /// directly with a <see cref="DefaultHttpContext"/> + in-memory
    /// response body so they can drive streaming without going through
    /// TestServer's buffered HTTP pipeline. Callers that bypass
    /// <see cref="MapProjectEventTailRoutes"/> are responsible for
    /// populating <c>HttpContext.Items</c> with the resolved project
    /// (via <see cref="ProjectResolutionEndpointFilter.ProjectInfoItemKey"/>).
    /// </summary>
    internal static async Task HandleTailAsync(
        HttpContext context,
        string? match,
        IEventTailSource source,
        CancellationToken ct)
    {
        var project = context.GetResolvedProject();

        EventMatchExpression? compiled = null;
        if (!string.IsNullOrWhiteSpace(match))
        {
            var result = EventMatchExpression.Compile(match!);
            if (!result.IsSuccess)
            {
                await WriteCompileFailureAsync(context, match!, result.Diagnostic!);
                return;
            }
            compiled = result.Expression;
        }

        await using var subscription = source.Open(project.Id, compiled);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        try
        {
            await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
            {
                var dto = CloudEventTailDto.From(evt);
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    dto,
                    CloudEvent.JsonOptions,
                    ct).ConfigureAwait(false);
                await context.Response.Body.WriteAsync(NewLine, ct).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static readonly byte[] NewLine = [(byte)'\n'];

    private static async Task WriteCompileFailureAsync(
        HttpContext context,
        string source,
        MatchDiagnostic diagnostic)
    {
        var details = new
        {
            offset = diagnostic.Offset,
            line = diagnostic.Line,
            column = diagnostic.Column,
            source,
        };
        var body = new ApiResponse<object>(
            false,
            Data: default,
            Error: diagnostic.Message,
            Code: "invalid_match_expression",
            Details: details);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(body, cancellationToken: context.RequestAborted);
    }
}

/// <summary>
/// Wire-level DTO for the NDJSON tail line. Carries the canonical
/// CloudEvent envelope identity (<c>type</c>, <c>source</c>, <c>id</c>,
/// <c>time</c>, <c>subject</c>, <c>specversion</c>) plus context
/// extensions as a flat object. The payload (<c>data</c>) is
/// intentionally omitted — matching is envelope-only
/// (<c>specs/event-envelope-matching</c>) and the tail observes only the
/// envelope.
/// </summary>
public sealed record CloudEventTailDto(
    string Type,
    string Source,
    string Id,
    string Time,
    string? Subject,
    [property: JsonPropertyName("specversion")] string SpecVersion,
    Dictionary<string, string> Extensions)
{
    public static CloudEventTailDto From(CloudEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var extensions = evt.Extensions.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(evt.Extensions, StringComparer.Ordinal);
        return new CloudEventTailDto(
            Type: evt.Type,
            Source: evt.Source?.ToString() ?? string.Empty,
            Id: evt.Id,
            Time: evt.Time.ToString("o"),
            Subject: evt.Subject,
            SpecVersion: evt.SpecVersion,
            Extensions: extensions);
    }
}