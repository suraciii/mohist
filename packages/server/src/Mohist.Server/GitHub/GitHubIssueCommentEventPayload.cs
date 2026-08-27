using System.Text.Json;
using Mohist.Server.Issue.Domain;

namespace Mohist.Server.GitHub;

/// <summary>
/// The minimal snapshot carried by a GitHub <c>issue_comment.created</c>
/// webhook. The command translator reads only the issue, comment, and
/// commenter's repository association; ordinary GitHub discussion remains
/// outside the Mohist comment stream.
/// </summary>
public sealed record GitHubIssueCommentEventPayload(
    int IssueNumber,
    string Title,
    string? Body,
    IReadOnlyList<string> Labels,
    string CommentId,
    string CommentBody,
    string AuthorAssociation)
{
    public static GitHubIssueCommentEventPayload? Parse(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("issue", out var issue)
            || issue.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("comment", out var comment)
            || comment.ValueKind != JsonValueKind.Object)
            return null;

        if (issue.TryGetProperty("pull_request", out _))
            return null;
        if (!issue.TryGetProperty("number", out var number)
            || !number.TryGetInt32(out var issueNumber)
            || issueNumber <= 0)
            return null;
        var title = issue.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(title))
            return null;
        if (!comment.TryGetProperty("body", out var commentBodyValue)
            || commentBodyValue.ValueKind != JsonValueKind.String
            || commentBodyValue.GetString() is not { } commentBody)
            return null;
        if (!comment.TryGetProperty("id", out var commentIdValue))
            return null;
        var commentId = commentIdValue.ValueKind == JsonValueKind.String
            ? commentIdValue.GetString()
            : commentIdValue.ValueKind == JsonValueKind.Number && commentIdValue.TryGetInt64(out var numericId)
                ? numericId.ToString()
                : null;
        if (string.IsNullOrWhiteSpace(commentId))
            return null;

        var association = comment.TryGetProperty("author_association", out var associationValue)
            ? associationValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(association))
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

        return new GitHubIssueCommentEventPayload(
            issueNumber,
            title,
            body,
            labels,
            commentId!,
            commentBody,
            association.Trim());
    }
}

public enum GitHubIssueCommandVerb
{
    Unknown,
    Start,
}

public sealed record GitHubIssueCommand(GitHubIssueCommandVerb Verb, string RawVerb)
{
    public static GitHubIssueCommand? Parse(string? body)
    {
        if (string.IsNullOrEmpty(body) || !body.StartsWith("/mohist", StringComparison.OrdinalIgnoreCase))
            return null;
        if (body.Length > "/mohist".Length
            && !char.IsWhiteSpace(body["/mohist".Length]))
            return null;

        var tokens = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var rawVerb = tokens.Length > 1 ? tokens[1] : string.Empty;
        var verb = tokens.Length == 2 && string.Equals(rawVerb, "start", StringComparison.OrdinalIgnoreCase)
            ? GitHubIssueCommandVerb.Start
            : GitHubIssueCommandVerb.Unknown;
        return new GitHubIssueCommand(verb, rawVerb);
    }

    public static bool IsPermitted(string authorAssociation) =>
        authorAssociation.Equals("OWNER", StringComparison.OrdinalIgnoreCase)
        || authorAssociation.Equals("MEMBER", StringComparison.OrdinalIgnoreCase)
        || authorAssociation.Equals("COLLABORATOR", StringComparison.OrdinalIgnoreCase);
}

public static class GitHubIssueCommandTranslation
{
    public static string MapPriority(IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            var trimmed = label.Trim();
            if (trimmed.Length == 2
                && char.ToLowerInvariant(trimmed[0]) == 'p'
                && trimmed[1] is >= '0' and <= '4')
            {
                return trimmed.ToLowerInvariant();
            }
        }
        return IssuePriority.Default.Value;
    }
}
