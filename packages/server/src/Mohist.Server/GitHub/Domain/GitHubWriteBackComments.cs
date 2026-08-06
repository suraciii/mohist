namespace Mohist.Server.GitHub.Domain;

/// <summary>
/// User-facing copy posted back to the GitHub issue by the progress
/// write-back writer. Product language: explains where the demand stands
/// in the Mohist pipeline and what happens next. The done comment will
/// grow a delivery summary and PR link in a later iteration.
/// </summary>
public static class GitHubWriteBackComments
{
    public static string WorkStarted(int issueNumber) =>
        $"Mohist 已开始处理该需求（Mohist issue #{issueNumber}），进度将在 GitHub 上同步。";

    public static string ApprovalRequested(int issueNumber) =>
        $"Mohist 已到达审批点，等待审批（Mohist issue #{issueNumber}）。";

    public static string Completed(int issueNumber) =>
        $"Mohist 已完成该需求（Mohist issue #{issueNumber}），GitHub issue 已关闭。";

    public static string Cancelled(int issueNumber) =>
        $"Mohist 已取消该需求（Mohist issue #{issueNumber}），GitHub issue 已关闭。";
}
