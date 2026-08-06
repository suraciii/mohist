using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Events;

namespace Mohist.Server.GitHub;

/// <summary>
/// Maps a GitHub webhook delivery (X-GitHub-Event header + JSON body) to a
/// CloudEvent envelope. Returns null when the delivery is not part of the
/// v1 event set (e.g. the ping connectivity probe); the payload is kept
/// verbatim in <c>data</c> for consumers, never read here.
/// </summary>
public static class GitHubEventNormalizer
{
    public static CloudEvent? Normalize(
        string eventHeader,
        JsonElement body,
        string projectId,
        string connectionId,
        string deliveryId,
        DateTimeOffset receivedAt)
    {
        var type = MapType(eventHeader, body);
        if (type is null)
            return null;

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.ProjectId] = projectId,
        };
        if (TryGetRepository(body, out var owner, out var repo))
            extensions[EventCatalog.Lineage.GitHubRepo] = $"{owner}/{repo}";
        if (TryGetIssueNumber(body, out var issueNumber))
            extensions[EventCatalog.Lineage.GitHubIssue] = issueNumber.ToString();

        return new CloudEvent(
            id: string.IsNullOrWhiteSpace(deliveryId) ? Guid.NewGuid().ToString("N") : deliveryId,
            source: new Uri(IngressEventPersistence.ConnectionSource(projectId, connectionId), UriKind.Relative),
            type: type,
            time: receivedAt,
            data: body.Clone(),
            extensions: extensions);
    }

    private static string? MapType(string eventHeader, JsonElement body)
    {
        var action = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("action", out var actionValue)
            ? actionValue.GetString()
            : null;
        return eventHeader switch
        {
            "issues" => action switch
            {
                "labeled" => EventCatalog.ReverseDns.GitHubIssuesLabeled,
                "closed" => EventCatalog.ReverseDns.GitHubIssuesClosed,
                "reopened" => EventCatalog.ReverseDns.GitHubIssuesReopened,
                _ => null,
            },
            "pull_request_review" => EventCatalog.ReverseDns.GitHubPullRequestReviewed,
            "check_suite" when action == "completed" => EventCatalog.ReverseDns.GitHubCheckSuiteCompleted,
            _ => null,
        };
    }

    private static bool TryGetRepository(JsonElement body, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object)
            return false;
        var name = repository.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var ownerValue = repository.TryGetProperty("owner", out var ownerElement) && ownerElement.ValueKind == JsonValueKind.Object
            ? ownerElement.TryGetProperty("login", out var loginValue) ? loginValue.GetString() : null
            : null;
        if (string.IsNullOrWhiteSpace(ownerValue) && repository.TryGetProperty("full_name", out var fullName))
        {
            var parts = fullName.GetString()?.Split('/', 2);
            if (parts is { Length: 2 })
                ownerValue = parts[0];
        }
        if (string.IsNullOrWhiteSpace(ownerValue) || string.IsNullOrWhiteSpace(name))
            return false;
        owner = ownerValue;
        repo = name;
        return true;
    }

    private static bool TryGetIssueNumber(JsonElement body, out int issueNumber)
    {
        issueNumber = 0;
        if (body.ValueKind != JsonValueKind.Object)
            return false;
        if (body.TryGetProperty("issue", out var issue) && TryReadNumber(issue, out issueNumber))
            return true;
        if (body.TryGetProperty("pull_request", out var pullRequest) && TryReadNumber(pullRequest, out issueNumber))
            return true;
        return false;
    }

    private static bool TryReadNumber(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("number", out var number)
            && number.TryGetInt32(out value);
    }
}
