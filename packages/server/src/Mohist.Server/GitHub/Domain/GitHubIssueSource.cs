namespace Mohist.Server.GitHub.Domain;

/// <summary>
/// Legacy feed-origin label constants retained only for decoding historical
/// issue data. New GitHub-fed issues use the persisted link as their origin
/// identity and never emit this product label.
/// </summary>
public static class GitHubIssueSource
{
    public const string LabelKey = "github-issue";

    public static string LabelValue(string owner, string repo, int githubIssueNumber) =>
        $"{owner}/{repo}#{githubIssueNumber}";
}
