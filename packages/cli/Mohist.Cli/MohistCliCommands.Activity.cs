using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class ActivityCommands
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 200;

    internal static readonly ResourceDescriptor ActivityListDescriptor = new(
        ResourceCardinality.Collection,
        ["id", "provenance", "scope", "kind", "time", "title", "description", "eventType", "issueNumber", "workflowRunId", "sessionId", "runnerId", "status"]);

    public static Command Build(MohistCliApi api)
    {
        var activity = new Command(
            "activity",
            "Read the persistent, Project-scoped Activity evidence collection. Returns recorded Issue/WorkflowRun/AgentSession history plus Project-bound AgentSession/waiting snapshots and global Runner context; entries carry provenance (recorded/snapshot) and scope (project/global).");
        activity.Subcommands.Add(BuildList(api));
        return activity;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List bounded, read-only Activity evidence for the resolved Project. Includes recorded Issue/WorkflowRun/AgentSession history, current AgentSession/waiting and global Runner snapshots, and labels provenance (recorded/snapshot) and scope (project/global). Re-readable after exit; not a subscription.");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var limitOpt = new Option<int>("--limit")
        {
            Description = $"Maximum entries to return (1-{MaxLimit})",
            DefaultValueFactory = _ => DefaultLimit,
        };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(ActivityListDescriptor);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var limit = ctx.GetValue(limitOpt);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return ListAsync();

            async Task<int> ListAsync()
            {
                var selection = JsonSelection.Parse(ActivityListDescriptor, jsonProvided, json);
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(ActivityListDescriptor, selection);

                if (limit is < 1 or > MaxLimit)
                {
                    await api.Error.WriteLineAsync(
                        $"--limit must be between 1 and {MaxLimit}").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.UsageFailure);
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                var path = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/activity?limit={limit}";
                return await api.PrintResourceAsync(
                    path,
                    ActivityListDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.ActivityList)).ConfigureAwait(false);
            }
        });
        return cmd;
    }
}
