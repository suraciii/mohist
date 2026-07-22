using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static readonly ResourceDescriptor FeedbackDescriptor = new(
        ResourceCardinality.Single,
        ["id", "issueNumber", "stage", "body", "createdAt", "updatedAt"]);

    private static Command BuildFeedback(MohistCliApi api)
    {
        var feedback = new Command("feedback", "Issue approval feedback");
        feedback.Subcommands.Add(BuildFeedbackList(api));
        feedback.Subcommands.Add(BuildFeedbackShow(api));
        feedback.Subcommands.Add(BuildFeedbackCreate(api));
        return feedback;
    }

    private static Command BuildFeedbackCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create an approval feedback record");
        var numberArg = NumberArg();
        var stageOpt = new Option<string?>("--stage", "-s") { Description = "Workflow stage (e.g. plan, build, check)" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Feedback body text (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read feedback body from a UTF-8 file path (recommended for long Markdown; mutually exclusive with --body)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var stage = ctx.GetValue(stageOpt);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var selection = JsonSelection.Parse(FeedbackDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            var stageProvided = ctx.GetResult(stageOpt) is not null;
            var bodyProvided = ctx.GetResult(bodyOpt) is not null;
            var bodyFileProvided = ctx.GetResult(bodyFileOpt) is not null;
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                if (!stageProvided || string.IsNullOrWhiteSpace(stage))
                {
                    api.Error.WriteLine("--stage is required");
                    return 1;
                }
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(FeedbackDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var resolved = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, false, api.FileSystem, api.StandardInput, api.Error);
                if (resolved is BodyInputResolver.Result.Failure)
                    return 1;
                var bodyText = ((BodyInputResolver.Result.Success)resolved).Body;
                var payload = new { stage, body = bodyText };
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/feedback");
                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    path,
                    payload,
                    FeedbackDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.FeedbackShow));
            }
        });
        return cmd;
    }

    private static Command BuildFeedbackList(MohistCliApi api)
    {
        var cmd = new Command("list", "List approval feedback records for an issue");
        var numberArg = NumberArg();
        var stageOpt = MohistCliCommands.StageOption();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var stage = ctx.GetValue(stageOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/feedback");
                if (!string.IsNullOrWhiteSpace(stage))
                    path += $"?stage={Uri.EscapeDataString(stage!)}";
                return await api.PrintWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.FeedbackList));
            }
        });
        return cmd;
    }

    private static Command BuildFeedbackShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show approval feedback record");
        var numberArg = NumberArg();
        var feedbackOpt = new Option<string?>("--feedback") { Description = "Feedback id" };
        var latestOpt = new Option<bool>("--latest") { Description = "Show the most recent feedback record" };
        var stageOpt = MohistCliCommands.StageOption();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(feedbackOpt);
        cmd.Options.Add(latestOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var feedbackId = ctx.GetValue(feedbackOpt);
            var latest = ctx.GetValue(latestOpt);
            var stage = ctx.GetValue(stageOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                if (string.IsNullOrWhiteSpace(feedbackId) && !latest)
                {
                    api.Error.WriteLine("Either --feedback <id> or --latest is required");
                    return 1;
                }
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var basePath = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/feedback");

                if (!string.IsNullOrWhiteSpace(feedbackId))
                {
                    var path = $"{basePath}/{Uri.EscapeDataString(feedbackId!)}";
                    return await api.PrintWithOutputAsync(
                        path,
                        mode,
                        nameof(MohistCliApi.TableShape.FeedbackShow));
                }

                var listPath = basePath;
                if (!string.IsNullOrWhiteSpace(stage))
                    listPath += $"?stage={Uri.EscapeDataString(stage!)}";

                var latestData = await api.GetDataSafeAsync(listPath);
                if (latestData is null)
                    return 1;
                var latestId = ExtractLatestId(latestData, stage);
                if (latestId is null)
                {
                    api.Error.WriteLine("No feedback records found");
                    return 1;
                }
                var detailPath = $"{basePath}/{Uri.EscapeDataString(latestId)}";
                return await api.PrintWithOutputAsync(
                    detailPath,
                    mode,
                    nameof(MohistCliApi.TableShape.FeedbackShow));
            }
        });
        return cmd;
    }

    private static string? ExtractLatestId(System.Text.Json.Nodes.JsonNode? data, string? stage)
    {
        if (data is not System.Text.Json.Nodes.JsonArray arr || arr.Count == 0)
            return null;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not System.Text.Json.Nodes.JsonObject obj) continue;
            if (!string.IsNullOrWhiteSpace(stage))
            {
                var recordStage = obj["stage"]?.GetValue<string>();
                if (!string.Equals(recordStage, stage, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var id = obj["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        return null;
    }
}
