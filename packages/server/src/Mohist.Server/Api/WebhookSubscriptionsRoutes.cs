using System.Text;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Webhooks.Domain;
using Mohist.Server.Webhooks.Services;

namespace Mohist.Server.Api;

public static class WebhookSubscriptionsRoutes
{
    public static WebApplication MapWebhookSubscriptionsRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/webhook/subscriptions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/failures", async (HttpContext context, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var failures = await store.ListFailuresAsync(project.Id, subscriptionId: null, ct);
            return ApiResults.Ok(failures.Select(ToFailureDto).ToArray());
        });

        group.MapGet("", async (HttpContext context, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var subscriptions = await store.ListAsync(project.Id, context.Request.Query.ContainsKey("all") || context.Request.Query.ContainsKey("includeArchived"), ct);
            var dtos = new List<WebhookSubscriptionDto>(subscriptions.Count);
            foreach (var subscription in subscriptions)
                dtos.Add(await ToDtoAsync(store, subscription, ct));
            return ApiResults.Ok(dtos.ToArray());
        });

        group.MapPost("", async (HttpContext context, WebhookSubscriptionCreateRequest request, WebhookSubscriptionStore store, CancellationToken ct) =>
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
            };
            try
            {
                var created = await store.CreateAsync(subscription, DecodeSecret(request.Secret), ct);
                return Results.Json(
                    new ApiResponse<WebhookSubscriptionDto>(true, await ToDtoAsync(store, created, ct)),
                    statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapGet("/{subscriptionId}", async (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var subscription = await store.GetAsync(project.Id, subscriptionId, ct);
            return subscription is null
                ? ApiResults.NotFound($"Webhook subscription '{subscriptionId}' not found")
                : ApiResults.Ok(await ToDtoAsync(store, subscription, ct));
        });

        group.MapPatch("/{subscriptionId}", async (HttpContext context, string subscriptionId, WebhookSubscriptionUpdateRequest request, WebhookSubscriptionStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            try
            {
                var updated = await store.UpdateAsync(project.Id, subscriptionId, request.Name, request.Match, request.TargetUrl, request.Fields, ct);
                return updated is null
                    ? ApiResults.NotFound($"Webhook subscription '{subscriptionId}' not found")
                    : ApiResults.Ok(await ToDtoAsync(store, updated, ct));
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapPost("/{subscriptionId}/enable", (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, subscriptionId, WebhookSubscriptionStatus.Active, ct));

        group.MapPost("/{subscriptionId}/disable", (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, subscriptionId, WebhookSubscriptionStatus.Disabled, ct));

        group.MapPost("/{subscriptionId}/archive", (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
            SetStatusAsync(context, store, subscriptionId, WebhookSubscriptionStatus.Archived, ct));

        group.MapPost("/{subscriptionId}/rotate-secret", async (HttpContext context, string subscriptionId, WebhookSubscriptionRotateSecretRequest request, WebhookSubscriptionStore store, CancellationToken ct) =>
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

        group.MapGet("/{subscriptionId}/failures", async (HttpContext context, string subscriptionId, WebhookSubscriptionStore store, CancellationToken ct) =>
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

    private static async Task<WebhookSubscriptionDto> ToDtoAsync(WebhookSubscriptionStore store, WebhookSubscription subscription, CancellationToken ct) =>
        new(
            subscription.Id, subscription.ProjectId, subscription.Name, subscription.Match,
            subscription.TargetUrl, subscription.Status,
            await store.HasSecretAsync(subscription.ProjectId, subscription.Id, ct),
            subscription.CreatedAt, subscription.UpdatedAt);

    private static WebhookDeliveryFailureDto ToFailureDto(WebhookDeliveryFailure failure) =>
        new(
            failure.Id, failure.ProjectId, failure.SubscriptionId, failure.EventId, failure.EventType,
            failure.TargetUrl, failure.ErrorSummary, failure.OccurredAt);

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
    string Status, bool HasSecret, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record WebhookDeliveryFailureDto(
    string Id, string ProjectId, string SubscriptionId, string EventId, string EventType,
    string TargetUrl, string ErrorSummary, DateTimeOffset OccurredAt);

public sealed record WebhookSubscriptionCreateRequest(string? Name, string? Match, string? TargetUrl, string? Secret);

public sealed record WebhookSubscriptionRotateSecretRequest(string? Secret);

public sealed record WebhookSubscriptionUpdateRequest(
    string? Name, string? Match, string? TargetUrl, IReadOnlySet<string> Fields, JsonElement Raw)
{
    public static async ValueTask<WebhookSubscriptionUpdateRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (raw.TryGetProperty("name", out _)) fields.Add(nameof(Name));
            if (raw.TryGetProperty("match", out _)) fields.Add(nameof(Match));
            if (raw.TryGetProperty("targetUrl", out _)) fields.Add(nameof(TargetUrl));
        }
        return new WebhookSubscriptionUpdateRequest(
            GetString(raw, "name"), GetString(raw, "match"), GetString(raw, "targetUrl"), fields, raw);
    }

    private static string? GetString(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
}
