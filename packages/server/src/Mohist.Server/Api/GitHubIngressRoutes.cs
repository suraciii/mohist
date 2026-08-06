using System.Text.Json;
using Mohist.Server.GitHub;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Api;

public static class GitHubIngressRoutes
{
    public static WebApplication MapGitHubIngressRoutes(this WebApplication app)
    {
        app.MapPost("/api/github-connections/{connectionId}/ingress", async (
            HttpContext context,
            string connectionId,
            GitHubConnectionStore store,
            IEventStore events,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var connection = await store.GetByIdAsync(connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound($"GitHub connection '{connectionId}' not found");
            if (connection.Status != GitHubConnectionStatus.Active)
                return ApiResults.Conflict($"GitHub connection '{connectionId}' is disabled", "github_connection_disabled");

            var payload = await ReadBodyAsync(context, ct);
            var secret = await store.LoadWebhookSecretAsync(connection.ProjectId, connection.Id, ct);
            if (secret is null || secret.Length == 0)
                return ApiResults.Fail("webhook secret is not configured", 401, "unauthorized");
            var secretText = System.Text.Encoding.UTF8.GetString(secret);
            var signature = context.Request.Headers[GitHubWebhookSignature.SignatureHeader].ToString();
            if (!GitHubWebhookSignature.Verify(payload, secretText, signature))
                return ApiResults.Fail("invalid signature", 401, "unauthorized");

            var eventHeader = context.Request.Headers["X-GitHub-Event"].ToString();
            if (string.IsNullOrWhiteSpace(eventHeader))
                return ApiResults.BadRequest("missing X-GitHub-Event header", "missing_event_header");

            JsonElement body;
            try
            {
                using var document = JsonDocument.Parse(payload);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest("request body must be a JSON object", "invalid_json_body");
            }

            var deliveryId = context.Request.Headers["X-GitHub-Delivery"].ToString();
            var envelope = GitHubEventNormalizer.Normalize(
                eventHeader, body, connection.ProjectId, connection.Id, deliveryId, timeProvider.GetUtcNow());
            if (envelope is null)
                return ApiResults.Ok();

            await events.AppendAsync(envelope, ct);
            return ApiResults.Ok();
        });

        return app;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context, CancellationToken ct)
    {
        await using var memory = new MemoryStream();
        await context.Request.Body.CopyToAsync(memory, ct);
        return memory.ToArray();
    }
}
