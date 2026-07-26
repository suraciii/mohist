using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildComment(MohistCliApi api)
    {
        var comment = new Command("comment", "Manage issue comments");
        comment.Subcommands.Add(BuildCommentCreate(api));
        return comment;
    }

    private static Command BuildCommentCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a comment on an issue");
        var numberArg = NumberArg();
        var authorOpt = new Option<string?>("--author") { Description = "Declared comment author (1-100 characters)" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Comment body text (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read comment body from a UTF-8 file path (recommended for long Markdown; mutually exclusive with --body)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(authorOpt);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var author = ctx.GetValue(authorOpt);
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
                if (string.IsNullOrWhiteSpace(author))
                {
                    api.Error.WriteLine("--author is required and must not be blank.");
                    return 1;
                }
                var normalizedAuthor = author.Trim();
                if (normalizedAuthor.Length > 100)
                {
                    api.Error.WriteLine("--author must be 100 characters or fewer.");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var resolved = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, api.FileSystem, api.StandardInput, api.Error);
                if (resolved is BodyInputResolver.Result.Failure)
                    return 1;
                var bodyText = ((BodyInputResolver.Result.Success)resolved).Body;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/comments");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { author = normalizedAuthor, body = bodyText },
                    mode,
                    nameof(MohistCliApi.TableShape.CommentShow));
            }
        });
        return cmd;
    }
}
