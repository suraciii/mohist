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
            Description = "Show profile descriptions with suitable_for context"
        };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(describedOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var described = ctx.GetValue(describedOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                if (described)
                {
                    return await api.PrintWorkflowProfilesDescribedAsync();
                }

                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;

                return await api.PrintWithOutputAsync(
                    "/api/workflow-templates/system",
                    mode);
            }
        });
        return cmd;
    }
}
