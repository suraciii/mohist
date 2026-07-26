using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildWatch(MohistCliApi api)
    {
        var watch = new Command(
            "watch",
            "Watch an issue: when its approval gate or terminal failure fires, the named Agent responds automatically. Nesting disambiguates from `mo run watch` (which polls a run).");
        watch.Subcommands.Add(BuildWatchAdd(api));
        watch.Subcommands.Add(BuildWatchRemove(api));
        watch.Subcommands.Add(BuildWatchList(api));
        return watch;
    }

    private static Command BuildWatchAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a watching declaration for an Agent on an issue (idempotent)");
        var numberArg = NumberArg();
        var agentOpt = new Option<string?>("--agent") { Description = "Agent name or id" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(agentOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var agent = ctx.GetValue(agentOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return AddAsync();

            async Task<int> AddAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                if (string.IsNullOrWhiteSpace(agent))
                {
                    api.Error.WriteLine("--agent is required");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var resolvedAgent = await AgentCommands.ResolveAgentAsync(api, resolvedProjectId, agent!);
                if (resolvedAgent is null)
                    return 1;

                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/watch");
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    path,
                    new { agentId = resolvedAgent.Id },
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildWatchRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a watching declaration; mutes the Agent on this issue if no other declaration covers it");
        var numberArg = NumberArg();
        var agentOpt = new Option<string?>("--agent") { Description = "Agent name or id" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(agentOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var agent = ctx.GetValue(agentOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                if (string.IsNullOrWhiteSpace(agent))
                {
                    api.Error.WriteLine("--agent is required");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var resolvedAgent = await AgentCommands.ResolveAgentAsync(api, resolvedProjectId, agent!);
                if (resolvedAgent is null)
                    return 1;

                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/watch");
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Delete,
                    path,
                    new { agentId = resolvedAgent.Id },
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildWatchList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List the issue's watching and muted Agents as two distinct groups");
        var numberArg = NumberArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.IssueWatchList)));
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}");
                return await api.PrintWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.IssueWatchList));
            }
        });
        return cmd;
    }
}
