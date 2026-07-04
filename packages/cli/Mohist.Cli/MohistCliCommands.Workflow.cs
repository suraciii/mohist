using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class WorkflowCommands
{
    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Workflow profile management");
        workflow.Subcommands.Add(BuildList(api));
        return workflow;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List workflow profiles");
        cmd.Aliases.Add("ls");
        var describedOpt = new Option<bool>("--described")
        {
            Description = "Show profile descriptions"
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(describedOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var described = ctx.GetValue(describedOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var hasProject = !string.IsNullOrWhiteSpace(project);
                var hasProjectId = !string.IsNullOrWhiteSpace(projectId);

                if (described)
                {
                    if (hasProject && hasProjectId && !string.Equals(project, projectId, StringComparison.Ordinal))
                    {
                        var (_, conflictExit) = await api.ResolveProject(project, projectId);
                        return conflictExit != 0 ? conflictExit : 1;
                    }

                    var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                    if (resolveExit != 0)
                    {
                        api.Error.WriteLine("No project specified or active; showing all workflow profiles (degraded).");
                        return await api.PrintWorkflowProfilesDescribedAsync();
                    }

                    return await api.PrintWorkflowProfilesDescribedAsync(resolvedProjectId);
                }

                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                if (hasProject && hasProjectId && !string.Equals(project, projectId, StringComparison.Ordinal))
                {
                    var (_, conflictExit) = await api.ResolveProject(project, projectId);
                    return conflictExit != 0 ? conflictExit : 1;
                }

                var plainResolvedProjectId = hasProject || hasProjectId
                    ? (await api.ResolveProject(project, projectId)).ProjectId
                    : await api.TryReadActiveProjectIdAsync();
                string path;
                if (string.IsNullOrWhiteSpace(plainResolvedProjectId))
                {
                    if (!string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase))
                        api.Error.WriteLine("No project specified or active; showing all workflow profiles (degraded).");
                    path = "/api/workflow-templates/system";
                }
                else
                {
                    path = $"/api/workflow-templates/system?project={MohistCliCommands.Escape(plainResolvedProjectId)}";
                }

                return await api.PrintWithOutputAsync(path, mode);
            }
        });
        return cmd;
    }
}
