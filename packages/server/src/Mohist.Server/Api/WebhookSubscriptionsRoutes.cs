using System.Collections;
using System.Text;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Webhooks.Domain;
using Mohist.Server.Webhooks.Services;

namespace Mohist.Server.Api;

public static class WebhookSubscriptionsRoutes
{
    public static WebApplication MapWebhookSubscriptionsRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/webhook").AddEndpointFilter<ProjectResolutionEndpointFilter>();

        // Event catalog: grouped, stable type names sourced from EventCatalog.
        group.MapGet("/event-types", () => ApiResults.Ok(WebhookEventCatalog.Build()));

        var subs = group.MapGroup("/subscriptions");

        subs.MapGet("/failures", async (HttpContext context, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var failures = await store.ListFailuresAsync(project.Id, subscriptionId: null, ct);
            return ApiResults.Ok(failures.Select(ToFailureDto).ToArray());
        });

        subs.MapGet("", async (HttpContext context, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var includeArchived = context.Request.Query.ContainsKey("all") || context.Request.Query.ContainsKey("includeArchived");
            var list = await store.ListAsync(project.Id, includeArchived, ct);
            var dtos = new List<WebhookSubscriptionDto>(list.Count);
            foreach (var subscription in list)
                dtos.Add(await ToDtoAsync(store, subscription, ct));
            return ApiResults.Ok(dtos.ToArray());
        });

        subs.MapPost("", async (HttpContext context, WebhookSubscriptionCreateRequest request, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            var subscription = new WebhookSubscription
            {
                Id = $"whsub_{Guid.NewGuid():N}",
                ProjectId = project.Id,
                Name = request.Name ?? string.Empty,
                Match = request.Match ?? string.Empty,
                TargetUrl = request.TargetUrl ?? string.Empty,
                EventSelectionMode = string.IsNullOrWhiteSpace(request.EventSelectionMode) ? WebhookEventSelectionMode.All : request.EventSelectionMode,
                EventTypes = request.EventTypes ?? [],
                AuthType = string.IsNullOrWhiteSpace(request.AuthType) ? WebhookAuthType.None : request.AuthType,
            };
            try
            {
                var created = await store.CreateAsync(subscription, BuildAuth(request), DecodeSecret(request.Secret), ct);
                return Results.Json(
                    new ApiResponse<WebhookSubscriptionDto>(true, await ToDtoAsync(store, created, ct)),
                    statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        subs.MapGet("/{subscriptionId}", async (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var subscription = await store.GetAsync(project.Id, subscriptionId, ct);
            return subscription is null
                ? ApiResults.NotFound($"Webhook subscription '{subscriptionId}' not found")
                : ApiResults.Ok(await ToDtoAsync(store, subscription, ct));
        });

        subs.MapPatch("/{subscriptionId}", async (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            if (context.Request.Body is null) return ApiResults.BadRequest("request body required");
            WebhookSubscriptionPatchRequest? request;
            try
            {
                request = await WebhookSubscriptionPatchRequest.BindAsync(context);
            }
            catch (JsonException ex)
            {
                return ApiResults.BadRequest("invalid JSON body: " + ex.Message);
            }
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            var patch = request.ToPatch();
            try
            {
                var updated = await store.UpdateAsync(project.Id, subscriptionId, patch, ct);
                return updated is null
                    ? ApiResults.NotFound($"Webhook subscription '{subscriptionId}' not found")
                    : ApiResults.Ok(await ToDtoAsync(store, updated, ct));
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        subs.MapPost("/{subscriptionId}/enable", (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, subscriptionId, WebhookSubscriptionStatus.Active, ct));

        subs.MapPost("/{subscriptionId}/disable", (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, subscriptionId, WebhookSubscriptionStatus.Disabled, ct));

        subs.MapPost("/{subscriptionId}/archive", (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, subscriptionId, WebhookSubscriptionStatus.Archived, ct));

        subs.MapPost("/{subscriptionId}/rotate-secret", async (HttpContext context, string subscriptionId, WebhookSubscriptionRotateSecretRequest request, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            var secret = DecodeSecret(request.Secret);
            if (secret is null) return ApiResults.BadRequest("secret is required", "secret_required");
            try
            {
                var rotated = await store.RotateSecretAsync(project.Id, subscriptionId, secret, ct);
                return rotated
                    ? ApiResults.Ok()
                    : ApiResults.NotFound($"Webhook subscription '{subscriptionId}' not found");
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        subs.MapGet("/{subscriptionId}/failures", async (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var failures = await store.ListFailuresAsync(project.Id, subscriptionId, ct);
            return ApiResults.Ok(failures.Select(ToFailureDto).ToArray());
        });

        return app;
    }

    private static async Task<IResult> SetStatusAsync(HttpContext context, WebhookSubscriptionStore store, string subscriptionId, string status, CancellationToken ct)
    {
        var project = context.GetResolvedProject();
        try
        {
            var updated = await store.SetStatusAsync(project.Id, subscriptionId, status, ct);
            return updated is null
                ? ApiResults.NotFound($"Webhook subscription '{subscriptionId}' not found")
                : ApiResults.Ok(await ToDtoAsync(store, updated, ct));
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    private static byte[]? DecodeSecret(string? secret) =>
        string.IsNullOrEmpty(secret) ? null : Encoding.UTF8.GetBytes(secret);

    private static WebhookAuthInput? BuildAuth(WebhookSubscriptionCreateRequest request)
    {
        var type = string.IsNullOrWhiteSpace(request.AuthType) ? WebhookAuthType.None : request.AuthType;
        if (type == WebhookAuthType.None) return new WebhookAuthInput(WebhookAuthType.None, null, null, null);
        if (type == WebhookAuthType.Bearer)
            return new WebhookAuthInput(WebhookAuthType.Bearer, request.AuthToken, null, null);
        if (type == WebhookAuthType.Basic && request.AuthBasic is { } basic)
            return new WebhookAuthInput(WebhookAuthType.Basic, null, (basic.User ?? string.Empty, basic.Password ?? string.Empty), null);
        if (type == WebhookAuthType.Custom)
            return new WebhookAuthInput(WebhookAuthType.Custom, null, null, request.AuthHeaders);
        return new WebhookAuthInput(type, null, null, null);
    }

    private static async Task<WebhookSubscriptionDto> ToDtoAsync(WebhookSubscriptionStore store, WebhookSubscription subscription, CancellationToken ct) =>
        new(
            subscription.Id, subscription.ProjectId, subscription.Name, subscription.Match,
            subscription.TargetUrl, subscription.Status,
            subscription.EventSelectionMode, subscription.EventTypes.ToArray(),
            subscription.AuthType,
            await store.HasSigningSecretAsync(subscription.ProjectId, subscription.Id, ct),
            subscription.CreatedAt, subscription.UpdatedAt);

    private static WebhookDeliveryFailureDto ToFailureDto(WebhookDeliveryFailure failure) =>
        new(
            failure.Id, failure.ProjectId, failure.SubscriptionId, failure.EventId, failure.EventType,
            failure.TargetUrl, failure.ResponseStatus, failure.DurationMs, failure.ErrorSummary, failure.OccurredAt);

    private static IResult MapError(Exception exception) => exception switch
    {
        WebhookSubscriptionMatchException match => ApiResults.BadRequest(match.Message, "invalid_match_expression", new
        {
            offset = match.Diagnostic.Offset, line = match.Diagnostic.Line, column = match.Diagnostic.Column,
        }),
        WebhookSubscriptionValidationException validation => ApiResults.Conflict(validation.Message, validation.Code),
        WebhookSubscriptionNameConflictException conflict => ApiResults.Conflict(conflict.Message, "webhook_subscription_name_conflict", new { conflict.ProjectId, conflict.Name }),
        _ => throw exception,
    };
}

public sealed record WebhookSubscriptionDto(
    string Id, string ProjectId, string Name, string Match, string TargetUrl,
    string Status, string EventSelectionMode, string[] EventTypes, string AuthType,
    bool HasSecret, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record WebhookDeliveryFailureDto(
    string Id, string ProjectId, string SubscriptionId, string EventId, string EventType,
    string TargetUrl, int? ResponseStatus, int? DurationMs, string ErrorSummary, DateTimeOffset OccurredAt);

public sealed record WebhookEventTypeGroupDto(string Group, string[] EventTypes);

public sealed record WebhookSubscriptionCreateRequest(
    string? Name,
    string? Match,
    string? TargetUrl,
    string? EventSelectionMode,
    string[]? EventTypes,
    string? AuthType,
    string? AuthToken,
    BasicAuthRequest? AuthBasic,
    Dictionary<string, string>? AuthHeaders,
    string? Secret);

public sealed record BasicAuthRequest(string? User, string? Password);

public sealed record WebhookSubscriptionRotateSecretRequest(string? Secret);

internal sealed record WebhookSubscriptionPatchRequest
{
    public string? Name { get; init; }
    public string? Match { get; init; }
    public string? TargetUrl { get; init; }
    public string? EventSelectionMode { get; init; }
    public string[]? EventTypes { get; init; }
    public string? AuthType { get; init; }
    public string? AuthToken { get; init; }
    public BasicAuthRequest? AuthBasic { get; init; }
    public Dictionary<string, string>? AuthHeaders { get; init; }

    public static async Task<WebhookSubscriptionPatchRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        if (raw.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        return JsonSerializer.Deserialize<WebhookSubscriptionPatchRequest>(raw.GetRawText(), JSON.Options);
    }

    public WebhookSubscriptionPatch ToPatch()
    {
        var authProvided = AuthType is not null || AuthToken is not null || AuthBasic is not null || AuthHeaders is not null;
        WebhookAuthInput? auth = null;
        var type = string.IsNullOrWhiteSpace(AuthType) ? WebhookAuthType.None : AuthType;
        if (authProvided)
        {
            auth = type switch
            {
                WebhookAuthType.Bearer => new WebhookAuthInput(WebhookAuthType.Bearer, AuthToken, null, null),
                WebhookAuthType.Basic when AuthBasic is { } b => new WebhookAuthInput(WebhookAuthType.Basic, null, (b.User ?? string.Empty, b.Password ?? string.Empty), null),
                WebhookAuthType.Custom => new WebhookAuthInput(WebhookAuthType.Custom, null, null, AuthHeaders),
                _ => new WebhookAuthInput(WebhookAuthType.None, null, null, null),
            };
        }
        return new WebhookSubscriptionPatch
        {
            Name = Name,
            Match = Match,
            TargetUrl = TargetUrl,
            EventSelectionMode = EventSelectionMode,
            EventTypes = EventTypes,
            AuthType = AuthType,
            Auth = auth,
            AuthProvided = authProvided,
        };
    }
}

/// <summary>Builds the grouped event catalog from <see cref="EventCatalog"/>.</summary>
public static class WebhookEventCatalog
{
    public static IReadOnlyList<WebhookEventTypeGroupDto> Build()
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var type in EventCatalog.All)
        {
            var key = GroupKey(type);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<string>();
                groups[key] = list;
            }
            list.Add(type);
        }
        return groups
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new WebhookEventTypeGroupDto(g.Key, g.Value.OrderBy(t => t, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private static string GroupKey(string type)
    {
        // type looks like com.mohist.<group>[.<sub>...]
        var span = type.AsSpan();
        const string prefix = "com.mohist.";
        if (span.StartsWith(prefix))
        {
            var rest = span[prefix.Length..];
            var dot = rest.IndexOf('.');
            var head = dot < 0 ? rest.ToString() : rest[..dot].ToString();
            return Capitalize(GroupLabel(head));
        }
        return "Other";
    }

    private static string GroupLabel(string head) => head.ToLowerInvariant() switch
    {
        "issue" => "Issue",
        "epic" => "Epic",
        "workflowrun" => "Workflow",
        "stage" => "Workflow",
        "task" => "Workflow",
        "check" => "Workflow",
        "repair" => "Workflow",
        "workflowartifact" => "Workflow",
        "agentsession" => "Agent",
        "agentjob" => "Agent",
        "inboxitem" => "Inbox",
        "runner" => "Runner",
        "feedback" => "Workflow",
        _ => string.IsNullOrEmpty(head) ? "Other" : head,
    };

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
