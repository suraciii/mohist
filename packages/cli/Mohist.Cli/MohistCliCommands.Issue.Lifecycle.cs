using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    internal static readonly ResourceDescriptor ArchiveCompletedDescriptor = new(
        ResourceCardinality.Single,
        ["archived", "skipped", "skippedNumbers", "message"]);

    private static Command BuildAction(string name, string description, MohistCliApi api)
    {
        var cmd = new Command(name, $"{description} an issue");
        var numberArg = NumberArg();
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueDescriptor);
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return ActAsync();

            async Task<int> ActAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/{name}"),
                    new { },
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.Issue));
            }
        });
        return cmd;
    }

    private static Command BuildRebase(MohistCliApi api)
    {
        var cmd = new Command("rebase", "Rebase issue branch");
        var numberArg = NumberArg();
        var baseBranchOpt = new Option<string?>("--base-branch") { Description = "Base branch to rebase onto" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(projectOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var project = ctx.GetValue(projectOpt);
            return RebaseAsync();

            async Task<int> RebaseAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintPostAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/rebase"),
                    new { baseBranch });
            }
        });
        return cmd;
    }

    private static Command BuildArchive(MohistCliApi api)
    {
        var cmd = new Command("archive", "Archive issues");
        var numberArg = new Argument<string?>("number")
        {
            Description = "Issue number (omit with --all-completed)",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => null,
        };
        var allCompletedOpt = new Option<bool>("--all-completed") { Description = "Archive all completed issues" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueDescriptor);
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(allCompletedOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var allCompleted = ctx.GetValue(allCompletedOpt);
            var project = ctx.GetValue(projectOpt);
            var number = ctx.GetValue(numberArg);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return ArchiveAsync();

            async Task<int> ArchiveAsync()
            {
                if (allCompleted && number is not null)
                {
                    return CommandHelpHook.RenderUsageFailure(
                        ctx,
                        api.Error,
                        "<number> and --all-completed are mutually exclusive");
                }

                if (!allCompleted && string.IsNullOrWhiteSpace(number))
                {
                    return CommandHelpHook.RenderUsageFailure(
                        ctx,
                        api.Error,
                        "<number> is required unless --all-completed is used");
                }

                if (allCompleted)
                {
                    var selection = JsonSelection.Parse(ArchiveCompletedDescriptor, jsonProvided, json);
                    if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                        return api.WriteJsonSelectionResult(ArchiveCompletedDescriptor, selection);

                    var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                    if (resolveExit != 0) return resolveExit;
                    return await api.PrintMutationResourceAsync(
                        HttpMethod.Post,
                        ProjectIssuesPath(resolvedProjectId, "/issues/archive-completed"),
                        new { },
                        ArchiveCompletedDescriptor,
                        selection,
                        data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueArchiveCompleted));
                }

                var issueSelection = JsonSelection.Parse(IssueDescriptor, jsonProvided, json);
                if (issueSelection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, issueSelection);
                var (resolvedIssueProjectId, issueResolveExit) = await api.ResolveProject(project);
                if (issueResolveExit != 0) return issueResolveExit;
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectIssuesPath(resolvedIssueProjectId, $"/issues/{Uri.EscapeDataString(number!)}/archive"),
                    new { },
                    IssueDescriptor,
                    issueSelection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.Issue));
            }
        });
        return cmd;
    }

    private static Command BuildGetSub(string name, MohistCliApi api)
    {
        var cmd = new Command(name, $"Show issue {name}");
        var numberArg = NumberArg();
        var projectOpt = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.SetAction(ctx =>
                {
                    var number = ctx.GetValue(numberArg);
                    var project = ctx.GetValue(projectOpt);
                    return GetAsync();

                    async Task<int> GetAsync()
                    {
                        var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                        if (resolveExit != 0) return resolveExit;
                        return await api.PrintGetAsync(
                            ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/{name}"));
                    }
                });
        return cmd;
    }
}
