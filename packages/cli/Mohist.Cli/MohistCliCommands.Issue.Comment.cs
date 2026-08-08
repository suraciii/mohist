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
        var displayNameOpt = new Option<string?>("--display-name") { Description = "Display alias shown with the comment (1-100 characters); the author is the authenticated identity" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Comment body text (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read comment body from a UTF-8 file path, or - for stdin (recommended for long Markdown; mutually exclusive with --body)" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.CommentShow)));
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(displayNameOpt);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var displayName = ctx.GetValue(displayNameOpt);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return AddAsync();

            async Task<int> AddAsync()
            {
                var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
                if (normalizedDisplayName?.Length > 100)
                {
                    return CommandHelpHook.RenderUsageFailure(
                        ctx, api.Error, "--display-name must be 100 characters or fewer.");
                }
                var resolved = await BodyInputResolver.ResolveAsync(
                    body, bodyFile,
                    new BodyInputResolver.SourceFlags("--body", "--body-file", "comment body"),
                    api.FileSystem, api.StandardInput, TextWriter.Null);
                if (resolved is BodyInputResolver.Result.Failure bodyFailure)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, bodyFailure.Message);

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var bodyText = ((BodyInputResolver.Result.Success)resolved).Body;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/comments");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { displayName = normalizedDisplayName, body = bodyText },
                    mode,
                    nameof(MohistCliApi.TableShape.CommentShow));
            }
        });
        return cmd;
    }
}
