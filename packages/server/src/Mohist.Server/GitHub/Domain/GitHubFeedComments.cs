namespace Mohist.Server.GitHub.Domain;

/// <summary>
/// User-facing copy posted back to the GitHub issue by the minimal comment
/// port. Product language: explains what Mohist did and what the demand
/// needs next.
/// </summary>
public static class GitHubFeedComments
{
    public static string Rejection(int issueNumber, string reason) =>
        $"已创建 Mohist issue #{issueNumber}，但无法启动：{reason}。该需求保留在 backlog 中，可在 Mohist 侧手动启动。";
}
