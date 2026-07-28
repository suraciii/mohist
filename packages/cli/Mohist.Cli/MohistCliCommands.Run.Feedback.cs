using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class RunCommands
{
    private static readonly ResourceDescriptor FeedbackListDescriptor = new(
        ResourceCardinality.Collection,
        ["id", "issueNumber", "workflowRunId", "stage", "status", "body", "createdAt", "resolution", "updatedAt"]);

    private static readonly ResourceDescriptor FeedbackViewDescriptor = new(
        ResourceCardinality.Single,
        ["id", "issueNumber", "workflowRunId", "stage", "status", "body", "createdAt", "resolution", "updatedAt"]);

    internal static void RegisterFeedback(Command runCommand, MohistCliApi api)
    {
        var feedback = new Command("feedback", "Read approval feedback for a workflow run");
        feedback.Subcommands.Add(BuildFeedbackList(api));
        feedback.Subcommands.Add(BuildFeedbackView(api));
        runCommand.Subcommands.Add(feedback);
    }

    private static Command BuildFeedbackList(MohistCliApi api)
    {
        var command = new Command("list", "List approval feedback records");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var stageOpt = MohistCliCommands.StageOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(FeedbackListDescriptor);
        command.Arguments.Add(runIdArg);
        command.Options.Add(issueOpt);
        command.Options.Add(projectOpt);
        command.Options.Add(stageOpt);
        command.Options.Add(jsonOpt);
        command.SetAction(ctx =>
        {
            var selection = JsonSelection.Parse(
                FeedbackListDescriptor,
                ctx.GetResult(jsonOpt) is not null,
                ctx.GetValue(jsonOpt));
            return ListFeedbackAsync();

            async Task<int> ListFeedbackAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(FeedbackListDescriptor, selection);

                var (projectId, issueNumber, resolveExit) = await ResolveFeedbackIssueAsync(
                    api,
                    ctx.GetValue(runIdArg),
                    ctx.GetValue(issueOpt),
                    ctx.GetValue(projectOpt)).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                var path = FeedbackPath(projectId!, issueNumber!, ctx.GetValue(stageOpt));
                return await api.PrintResourceAsync(
                    path,
                    FeedbackListDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.FeedbackList)).ConfigureAwait(false);
            }
        });
        return command;
    }

    private static Command BuildFeedbackView(MohistCliApi api)
    {
        var command = new Command("view", "Show one approval feedback record");
        var runIdArg = RunIdArg();
        var issueOpt = IssueOption();
        var projectOpt = ProjectOptions();
        var feedbackOpt = new Option<string?>("--feedback") { Description = "Feedback id (mutually exclusive with --latest)" };
        var latestOpt = new Option<bool>("--latest") { Description = "Show the most recent feedback record (mutually exclusive with --feedback)" };
        var stageOpt = MohistCliCommands.StageOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(FeedbackViewDescriptor);
        command.Arguments.Add(runIdArg);
        command.Options.Add(issueOpt);
        command.Options.Add(projectOpt);
        command.Options.Add(feedbackOpt);
        command.Options.Add(latestOpt);
        command.Options.Add(stageOpt);
        command.Options.Add(jsonOpt);
        command.Validators.Add(result =>
        {
            if (result.GetResult(feedbackOpt) is not null && result.GetResult(latestOpt) is not null)
                result.AddError("--feedback and --latest cannot be used together.");
        });
        command.SetAction(ctx =>
        {
            var feedbackId = ctx.GetValue(feedbackOpt);
            var latest = ctx.GetValue(latestOpt);
            var selection = JsonSelection.Parse(
                FeedbackViewDescriptor,
                ctx.GetResult(jsonOpt) is not null,
                ctx.GetValue(jsonOpt));
            return ViewFeedbackAsync();

            async Task<int> ViewFeedbackAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(FeedbackViewDescriptor, selection);

                if (string.IsNullOrWhiteSpace(feedbackId) && !latest)
                {
                    await api.Error.WriteLineAsync("--feedback <id> or --latest is required.").ConfigureAwait(false);
                    return 1;
                }

                var (projectId, issueNumber, resolveExit) = await ResolveFeedbackIssueAsync(
                    api,
                    ctx.GetValue(runIdArg),
                    ctx.GetValue(issueOpt),
                    ctx.GetValue(projectOpt)).ConfigureAwait(false);
                if (resolveExit != 0)
                    return resolveExit;

                var basePath = FeedbackPath(projectId!, issueNumber!, stage: null);
                string detailPath;
                if (!string.IsNullOrWhiteSpace(feedbackId))
                {
                    detailPath = $"{basePath}/{Uri.EscapeDataString(feedbackId!)}";
                }
                else
                {
                    var (latestExit, latestData) = await api.GetDataOrPrintErrorAsync(
                        FeedbackPath(projectId!, issueNumber!, ctx.GetValue(stageOpt))).ConfigureAwait(false);
                    if (latestExit != 0)
                        return latestExit;

                    var latestId = LatestFeedbackId(latestData);
                    if (latestId is null)
                    {
                        await api.Error.WriteLineAsync("No feedback records found").ConfigureAwait(false);
                        return 1;
                    }

                    detailPath = $"{basePath}/{Uri.EscapeDataString(latestId)}";
                }

                return await api.PrintResourceAsync(
                    detailPath,
                    FeedbackViewDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.FeedbackShow)).ConfigureAwait(false);
            }
        });
        return command;
    }

    private static async Task<(string? ProjectId, string? IssueNumber, int Exit)> ResolveFeedbackIssueAsync(
        MohistCliApi api,
        string? runId,
        string? issueNumber,
        string? project)
    {
        var (resolvedRunId, resolveExit) = await ResolveRunTargetAsync(
            api, runId, issueNumber, project).ConfigureAwait(false);
        if (resolveExit != 0)
            return (null, null, resolveExit);

        if (!string.IsNullOrWhiteSpace(issueNumber))
        {
            var (projectId, projectExit) = await api.ResolveProject(project).ConfigureAwait(false);
            return projectExit == 0
                ? (projectId, issueNumber, 0)
                : (null, null, projectExit);
        }

        var (detailExit, detail) = await api.GetDataOrPrintErrorAsync(
            WorkflowRunPath(resolvedRunId!, "")).ConfigureAwait(false);
        if (detailExit != 0)
            return (null, null, detailExit);

        var issueRef = detail?["issueRef"] as JsonObject;
        var projectIdFromRun = issueRef?["projectId"]?.GetValue<string>();
        var numberFromRun = issueRef?["number"]?.ToString();
        if (string.IsNullOrWhiteSpace(projectIdFromRun) || string.IsNullOrWhiteSpace(numberFromRun))
        {
            await api.Error.WriteLineAsync(
                $"Workflow run {resolvedRunId} has no associated issue reference.").ConfigureAwait(false);
            return (null, null, 1);
        }

        return (projectIdFromRun, numberFromRun, 0);
    }

    private static string FeedbackPath(string projectId, string issueNumber, string? stage)
    {
        var path = $"/api/projects/{MohistCliCommands.Escape(projectId)}/issues/{MohistCliCommands.Escape(issueNumber)}/feedback";
        return string.IsNullOrWhiteSpace(stage)
            ? path
            : $"{path}?stage={Uri.EscapeDataString(stage)}";
    }

    private static string? LatestFeedbackId(JsonNode? data)
    {
        if (data is not JsonArray records)
            return null;

        foreach (var record in records)
        {
            var id = (record as JsonObject)?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }

        return null;
    }
}
