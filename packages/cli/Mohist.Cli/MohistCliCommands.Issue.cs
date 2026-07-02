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

    private static Command BuildPrereq(MohistCliApi api)
    {
        var prereq = new Command("prereq", "Manage issue start prerequisites");
        prereq.Subcommands.Add(BuildPrereqAdd(api));
        prereq.Subcommands.Add(BuildPrereqRemove(api));
        return prereq;
    }

    private static Command BuildPrereqAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a start prerequisite to an issue");
        var numberArg = NumberArg();
        var prereqNumberArg = new Argument<int>("prereq-number")
        {
            Description = "Prerequisite issue number",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return AddAsync();

            async Task<int> AddAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { prerequisiteNumber = prereqNumber },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildPrereqRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a start prerequisite from an issue");
        var numberArg = NumberArg();
        var prereqNumberArg = new Argument<int>("prereq-number")
        {
            Description = "Prerequisite issue number",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites/{prereqNumber}");
                return await api.PrintDeleteWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildComment(MohistCliApi api)
    {
        var comment = new Command("comment", "Manage issue comments");
        comment.Subcommands.Add(BuildCommentAdd(api));
        return comment;
    }

    private static Command BuildCommentAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a comment to an issue");
        var numberArg = NumberArg();
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Comment body text (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read comment body from a UTF-8 file path (recommended for long Markdown; mutually exclusive with --body)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var bodyProvided = ctx.GetResult(bodyOpt) is not null;
            var bodyFileProvided = ctx.GetResult(bodyFileOpt) is not null;
            return AddAsync();

            async Task<int> AddAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var resolved = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, false, api.FileSystem, api.StandardInput, api.Error);
                if (resolved is BodyInputResolver.Result.Failure)
                    return 1;
                var bodyText = ((BodyInputResolver.Result.Success)resolved).Body;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/comments");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { body = bodyText },
                    mode,
                    nameof(MohistCliApi.TableShape.FeedbackShow));
            }
        });
        return cmd;
    }

    private static Command BuildTemplate(MohistCliApi api)
    {
        var template = new Command("template", "Issue template management");
        template.Subcommands.Add(BuildTemplateList(api));
        template.Subcommands.Add(BuildTemplateGet(api));
        return template;
    }

    private static Command BuildTemplateList(MohistCliApi api)
    {
        var cmd = new Command("list", "List available issue templates for the active project");
        cmd.Aliases.Add("ls");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    IssueTemplatesPath(resolvedProjectId, "/"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueTemplateList));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Show a single issue template by name");
        var nameArg = new Argument<string>("name") { Description = "Template name or id (e.g. feature)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    api.Error.WriteLine("Template name is required");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    IssueTemplatesPath(resolvedProjectId, $"/{name}"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueTemplateShow));
            }
        });
        return cmd;
    }

    private static string IssueTemplatesPath(string? projectId, string path)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        var suffix = path == "/" ? string.Empty : (path.StartsWith('/') ? path : "/" + path);
        return $"/api/issue-templates{suffix}?projectId={MohistCliCommands.Escape(projectId)}";
    }
}
