namespace Mohist.Server.GitHub.Domain;

public static class GitHubIssueCommandComments
{
    public static string Started(string projectId, int issueNumber) =>
        $"Mohist issue #{issueNumber} 已创建并启动：/projects/{projectId}/issues/{issueNumber}";

    public static string AlreadyLinked(string projectId, int issueNumber) =>
        $"该 GitHub issue 已接入 Mohist issue #{issueNumber}：/projects/{projectId}/issues/{issueNumber}";

    public static string Refused(string reason) =>
        $"无法接入 Mohist：{reason}";

    public static string UnknownVerb(string verb) =>
        string.IsNullOrWhiteSpace(verb)
            ? Refused("缺少命令；目前只支持 /mohist start")
            : Refused($"不支持命令 '{verb}'；目前只支持 /mohist start");

    public static string StartFailed(string reason) =>
        $"Mohist issue 已创建，但无法启动：{reason}。该需求保留在 backlog 中，可在 Mohist 侧手动启动。";
}
