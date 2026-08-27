using System.Text.Json;

namespace Mohist.Server.GitHub;

/// <summary>
/// The slice of a GitHub <c>issues</c> webhook payload the linked-issue
/// translators consume. Parsed from the verbatim event <c>data</c>; events
/// without an issue number (malformed or unexpected shape) yield
/// <c>null</c> and are skipped by the handlers.
/// </summary>
public sealed record GitHubIssueEventPayload(
    int IssueNumber,
    string Title,
    string? Body,
    IReadOnlyList<string> Labels,
    string? EditorLogin = null,
    string? StateReason = null)
{
    public static GitHubIssueEventPayload? Parse(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty("issue", out var issue))
            return null;
        if (issue.ValueKind != JsonValueKind.Object
            || !issue.TryGetProperty("number", out var number)
            || !number.TryGetInt32(out var issueNumber)
            || issueNumber <= 0)
        {
            return null;
        }
        var title = issue.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var body = issue.TryGetProperty("body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.String
            ? bodyValue.GetString()
            : null;
        var labels = new List<string>();
        if (issue.TryGetProperty("labels", out var labelsValue) && labelsValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in labelsValue.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var name)
                    && name.GetString() is { Length: > 0 } labelName)
                {
                    labels.Add(labelName);
                }
            }
        }
        var editorLogin = element.TryGetProperty("sender", out var sender)
            && sender.ValueKind == JsonValueKind.Object
            && sender.TryGetProperty("login", out var login)
            && login.ValueKind == JsonValueKind.String
            ? login.GetString()
            : null;
        var stateReason = issue.TryGetProperty("state_reason", out var stateReasonValue)
            && stateReasonValue.ValueKind == JsonValueKind.String
            ? stateReasonValue.GetString()
            : null;
        return new GitHubIssueEventPayload(issueNumber, title, body, labels, editorLogin, stateReason);
    }
}
