using System.CommandLine;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static void AddJobViewAndObservation(Command job, MohistCliApi api)
    {
        job.Subcommands.Add(BuildJobView(api));
        job.Subcommands.Add(BuildJobObservation(api));
    }

    private static Command BuildJobObservation(MohistCliApi api)
    {
        var cmd = new Command(
            "observation",
            "Show the composite Agent launch observation, including recovering reason and deadline. GETs .../agent-jobs/{jobId}/launch-observation.");
        var jobIdArg = new Argument<string>("job-id") { Description = "Agent job id returned by launch" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentLaunchObservation)));

        cmd.Arguments.Add(jobIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var jobId = ctx.GetValue(jobIdArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ViewAsync();

            async Task<int> ViewAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                return await api.PrintWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, $"/agent-jobs/{MohistCliCommands.Escape(jobId!)}/launch-observation"),
                    mode,
                    nameof(MohistCliApi.TableShape.AgentLaunchObservation));
            }
        });
        return cmd;
    }
}
