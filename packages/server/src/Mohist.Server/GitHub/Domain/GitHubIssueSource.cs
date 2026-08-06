namespace Mohist.Server.GitHub.Domain;

/// <summary>
/// The Mohist-side label that makes a fed issue's origin traceable: the
/// value encodes the GitHub coordinates (<c>owner/repo#number</c>) the
/// issue was fed from. The reverse mapping (GitHub → Mohist issue number)
/// lives on <see cref="GitHubIssueLink"/>.
/// </summary>
public static class GitHubIssueSource
{
    public const string LabelKey = "github-issue";

    public static string LabelValue(string owner, string repo, int githubIssueNumber) =>
        $"{owner}/{repo}#{githubIssueNumber}";
}
