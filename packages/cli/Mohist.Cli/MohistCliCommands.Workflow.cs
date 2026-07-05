using System.CommandLine;

namespace Mohist.Cli;

internal static class WorkflowCommands
{
    public static Command Build(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Workflow run management");
        return workflow;
    }
}