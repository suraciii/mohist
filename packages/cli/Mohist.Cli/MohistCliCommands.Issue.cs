using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    public static Command Build(MohistCliApi api)
    {
        var issue = new Command("issue", "Issue management");

        issue.Subcommands.Add(BuildList(api));
        issue.Subcommands.Add(BuildCreate(api));
        issue.Subcommands.Add(BuildShow(api));
        issue.Subcommands.Add(BuildUpdate(api));
        issue.Subcommands.Add(BuildAction("start", "Start workflow", api));
        issue.Subcommands.Add(BuildAction("approve", "Approve workflow", api));
        issue.Subcommands.Add(BuildAction("close", "Close issue", api));
        issue.Subcommands.Add(BuildAction("reopen", "Reopen issue", api));
        issue.Subcommands.Add(BuildAction("retry", "Retry issue", api));
        issue.Subcommands.Add(BuildAction("rerun", "Rerun issue", api));
        issue.Subcommands.Add(BuildRerunFromStage(api));
        issue.Subcommands.Add(BuildAction("force-stop", "Force stop workflow", api));
        issue.Subcommands.Add(BuildAction("resume", "Resume workflow", api));
        issue.Subcommands.Add(BuildReject(api));
        issue.Subcommands.Add(BuildStop(api));
        issue.Subcommands.Add(BuildRebase(api));
        issue.Subcommands.Add(BuildArchive(api));
        issue.Subcommands.Add(BuildAction("unarchive", "Unarchive issue", api));
        issue.Subcommands.Add(BuildGetSub("logs", api));
        issue.Subcommands.Add(BuildGetSub("events", api));
        issue.Subcommands.Add(BuildGetSub("diff", api));
        issue.Subcommands.Add(BuildGetSub("commits", api));
        issue.Subcommands.Add(BuildSessions(api));
        issue.Subcommands.Add(BuildSession(api));
        issue.Subcommands.Add(BuildWorkflow(api));
        issue.Subcommands.Add(BuildFeedback(api));
        issue.Subcommands.Add(BuildPrereq(api));
        issue.Subcommands.Add(BuildComment(api));
        issue.Subcommands.Add(BuildTemplate(api));

        return issue;
    }

    private static Argument<string> NumberArg() => new("number") { Description = "Issue number" };

    private static string ProjectIssuesPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static bool IsOptionProvided(ParseResult ctx, Option option)
    {
        var result = ctx.GetResult(option);
        if (result is null) return false;
        return !result.Implicit;
    }

    private static (string Mode, int Exit) ValidateOutput(MohistCliApi api, string? output)
    {
        var validation = MohistCliApi.ValidateOutputMode(output);
        if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
        {
            api.Error.WriteLine(invalid.Message);
            return ("json", 1);
        }
        return (((MohistCliApi.OutputModeResult.Valid)validation).Mode, 0);
    }

    private static async Task<(string ProjectId, int Exit)> ResolveProjectId(
        MohistCliApi api, string? project, string? projectId)
    {
        var resolved = await api.ResolveProjectIdAsync(project, projectId);
        if (resolved is null)
            return ("", 1);
        return (resolved, 0);
    }

    private static string IssueTemplatesPath(string? projectId, string path)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        var suffix = path == "/" ? string.Empty : (path.StartsWith('/') ? path : "/" + path);
        return $"/api/issue-templates{suffix}?projectId={MohistCliCommands.Escape(projectId)}";
    }
}