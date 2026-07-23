using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildWorkflowConfigSet(MohistCliApi api)
    {
        var cmd = new Command("set", "Set the issue workflow template override");
        var numberArg = NumberArg();
        var templateOpt = new Option<string?>("--template")
        {
            Description = "Inline YAML template body, or '@<file>' to read UTF-8 from a file (PUT /workflow-profile/template)",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(templateOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var template = ctx.GetValue(templateOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var templateProvided = ctx.GetResult(templateOpt) is not null;
            return SetAsync();

            async Task<int> SetAsync()
            {
                if (!templateProvided)
                {
                    api.Error.WriteLine("nothing to change — pass --template");
                    return 1;
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var issuePath = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile");

                var expanded = await api.ExpandAtFileAsync(template, "--template");
                if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                    return 1;
                var templateText = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;

                var putExit = await api.PrintPutWithOutputAsync(
                    issuePath + "/template",
                    new { yaml = templateText },
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowProfile));
                if (putExit != 0)
                    return putExit;

                return 0;
            }
        });
        return cmd;
    }
}
