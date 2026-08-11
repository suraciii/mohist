using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static readonly ResourceDescriptor IssueListDescriptor = new(
        ResourceCardinality.Collection,
        ["number", "title", "status", "stage", "priority", "risk", "labels", "prereq", "epic", "createdAt", "updatedAt"]);

    internal static readonly ResourceDescriptor IssueDescriptor = new(
        ResourceCardinality.Single,
        ["number", "title", "status", "stage", "priority", "risk", "labels", "body", "repository", "repositoryName", "prereq", "epic", "workflowRunId", "createdAt", "updatedAt"]);

    internal static readonly ResourceDescriptor IssueViewDescriptor = new(
        ResourceCardinality.Single,
        ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.Issue)).Fields);

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List issues");
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var stageOpt = MohistCliCommands.StageOption();
        var labelOpt = MohistCliCommands.LabelFilterOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var repositoryOpt = new Option<string?>("--repo") { Description = "Filter by target repository name" };
        var parentOpt = new Option<int?>("--parent") { Description = "Filter by parent issue number" };
        var epicOpt = new Option<int?>("--epic") { Description = "Filter by epic number" };
        var allOpt = new Option<bool>("--all") { Description = "Show all issues (mutually exclusive with --archived)" };
        var archivedOpt = new Option<bool>("--archived") { Description = "Show archived issues (mutually exclusive with --all)" };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueListDescriptor);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(repositoryOpt);
        cmd.Options.Add(parentOpt);
        cmd.Options.Add(epicOpt);
        cmd.Options.Add(allOpt);
        cmd.Options.Add(archivedOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Validators.Add(result =>
        {
            if (result.GetResult(allOpt) is not null && result.GetResult(archivedOpt) is not null)
                result.AddError("--all and --archived are mutually exclusive.");
        });
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var stage = ctx.GetValue(stageOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var repository = ctx.GetValue(repositoryOpt);
            var parent = ctx.GetValue(parentOpt);
            var epic = ctx.GetValue(epicOpt);
            var all = ctx.GetValue(allOpt);
            var archived = ctx.GetValue(archivedOpt);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return ListAsync();

            async Task<int> ListAsync()
            {
                var selection = JsonSelection.Parse(IssueListDescriptor, jsonProvided, json);
                if (selection.Kind == JsonSelectionKind.Discovery || selection.Kind == JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueListDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;
                if (labels is { Length: > 0 })
                {
                    var labelError = LabelDelta.ValidateFilterTokens(labels);
                    if (labelError is not null)
                    {
                        api.Error.WriteLine(labelError);
                        return 1;
                    }
                }
                var query = MohistCliCommands.Query(
                    Stage: stage,
                    Labels: labels,
                    Priority: priority,
                    Repository: repository,
                    Parent: parent,
                    Epic: epic,
                    Archived: archived ? true : null,
                    All: all ? true : null);
                return await api.PrintResourceAsync(
                    ProjectIssuesPath(resolvedProjectId, "/issues") + query,
                    IssueListDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueList));
            }
        });
        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command("view", "Show issue details");
        var numberArg = NumberArg();
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueViewDescriptor);
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var json = ctx.GetValue(jsonOpt);
            var jsonProvided = ctx.GetResult(jsonOpt) is not null;
            return GetAsync();

            async Task<int> GetAsync()
            {
                var selection = JsonSelection.Parse(IssueViewDescriptor, jsonProvided, json);
                if (selection.Kind == JsonSelectionKind.Discovery || selection.Kind == JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueViewDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintResourceAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}"),
                    IssueViewDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.Issue));
            }
        });
        return cmd;
    }
}
