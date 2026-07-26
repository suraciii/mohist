using System.CommandLine;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new issue");
        var titleArg = new Argument<string>("title") { Description = "Issue title" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Issue body (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read issue body from a UTF-8 file path, or - for stdin (mutually exclusive with --body)" };
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var parentOpt = new Option<int?>("--parent") { Description = "Parent issue number" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var modelOpt = new Option<string?>("--model") { Description = "Model to use" };
        var modelVariantOpt = new Option<string?>("--model-variant") { Description = "Reasoning variant bound to --model (e.g. low/medium/high/max)" };
        var workflowProfileOpt = new Option<string?>("--workflow-profile") { Description = "Workflow profile ID" };
        var riskOpt = new Option<string?>("--risk") { Description = "Risk level (low, medium, high); overrides frontmatter risk" };
        var repositoryOpt = new Option<string?>("--repo") { Description = "Target repository name in multi-repository projects" };
        var stageModelsOpt = new Option<string?>("--stage-models") { Description = "Per-stage model map as inline JSON or @<file> (e.g. '{\"plan\":\"anthropic/claude-sonnet\"}')" };
        var stageModelVariantsOpt = new Option<string?>("--stage-model-variants") { Description = "Per-stage model variant map as inline JSON or @<file> (e.g. '{\"plan\":\"max\"}')" };
        var (readyOpt, draftOpt) = MohistCliCommands.IsDraftFlags("creating");
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueDescriptor);
        cmd.Arguments.Add(titleArg);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(parentOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(modelVariantOpt);
        cmd.Options.Add(workflowProfileOpt);
        cmd.Options.Add(riskOpt);
        cmd.Options.Add(repositoryOpt);
        cmd.Options.Add(stageModelsOpt);
        cmd.Options.Add(stageModelVariantsOpt);
        cmd.Options.Add(readyOpt);
        cmd.Options.Add(draftOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var title = ctx.GetValue(titleArg);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var parent = ctx.GetValue(parentOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var model = ctx.GetValue(modelOpt);
            var modelVariant = ctx.GetValue(modelVariantOpt);
            var workflowProfile = ctx.GetValue(workflowProfileOpt);
            var risk = ctx.GetValue(riskOpt);
            var repository = ctx.GetValue(repositoryOpt);
            var stageModels = ctx.GetValue(stageModelsOpt);
            var stageModelVariants = ctx.GetValue(stageModelVariantsOpt);
            var ready = ctx.GetValue(readyOpt);
            var draft = ctx.GetValue(draftOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            var workflowProfileProvided = ctx.GetResult(workflowProfileOpt) is not null;
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var draftState = MohistCliCommands.ResolveDraftFlagState(ready, draft);
                if (draftState == MohistCliCommands.DraftFlagState.Conflicting)
                    return CommandHelpHook.RenderUsageFailure(
                        ctx, api.Error, "--ready and --draft are mutually exclusive");
                var isDraft = draftState switch
                {
                    MohistCliCommands.DraftFlagState.Draft => true,
                    MohistCliCommands.DraftFlagState.Ready => false,
                    _ => true,
                };
                var labelParse = LabelDelta.Parse(labels);
                if (!labelParse.IsValid)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, labelParse.Error!);
                var resolvedBody = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, api.FileSystem, api.StandardInput, TextWriter.Null);
                if (resolvedBody is BodyInputResolver.Result.Failure bodyFailure)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, bodyFailure.Message);
                var bodyText = ((BodyInputResolver.Result.Success)resolvedBody).Body;

                var (effectiveBody, effectiveWorkflow, effectiveRisk) =
                    ApplyFrontmatter(api.Error, bodyText, bodyFile, workflowProfile, risk);

                object? stageModelsPayload = null;
                if (ctx.GetResult(stageModelsOpt) is not null)
                {
                    var sm = await JsonInputResolver.ResolveAsync(stageModels, api.FileSystem, TextWriter.Null, "--stage-models");
                    if (sm is JsonInputResolver.Result.Failure failure)
                        return CommandHelpHook.RenderUsageFailure(ctx, api.Error, failure.Message);
                    stageModelsPayload = ((JsonInputResolver.Result.Success)sm).Value;
                }

                object? stageModelVariantsPayload = null;
                if (ctx.GetResult(stageModelVariantsOpt) is not null)
                {
                    var smv = await JsonInputResolver.ResolveAsync(stageModelVariants, api.FileSystem, TextWriter.Null, "--stage-model-variants");
                    if (smv is JsonInputResolver.Result.Failure failure)
                        return CommandHelpHook.RenderUsageFailure(ctx, api.Error, failure.Message);
                    stageModelVariantsPayload = ((JsonInputResolver.Result.Success)smv).Value;
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                Dictionary<string, string>? labelMap = null;
                foreach (var entry in labelParse.Entries)
                {
                    if (!entry.IsSet) continue;
                    labelMap ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    labelMap[entry.Key] = entry.Value!;
                }

                var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["title"] = title,
                    ["body"] = effectiveBody,
                    ["labels"] = labelMap,
                    ["priority"] = priority,
                    ["model"] = model,
                    ["modelVariant"] = modelVariant,
                    ["risk"] = effectiveRisk,
                    ["isDraft"] = isDraft,
                    ["parentIssueNumber"] = parent,
                };
                if (workflowProfileProvided || effectiveWorkflow is not null)
                    payload["workflowProfileId"] = workflowProfileProvided
                        ? (string.IsNullOrWhiteSpace(workflowProfile) ? null : workflowProfile)
                        : effectiveWorkflow;
                if (ctx.GetResult(repositoryOpt) is not null)
                    payload["repositoryName"] = repository;
                if (stageModelsPayload is not null)
                    payload["stageModels"] = stageModelsPayload;
                if (stageModelVariantsPayload is not null)
                    payload["stageModelVariants"] = stageModelVariantsPayload;

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Post,
                    ProjectIssuesPath(resolvedProjectId, "/issues"),
                    payload,
                    IssueDescriptor,
                    selection,
                    data =>
                    {
                        PrintCreateGuidance(data, api.Error);
                        return api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow);
                    });
            }
        });
        return cmd;
    }

    private static (string Body, string? Workflow, string? Risk) ApplyFrontmatter(
        TextWriter error,
        string bodyText,
        string? bodyFile,
        string? workflowFlag,
        string? riskFlag)
    {
        var parsed = FrontmatterParser.Parse(bodyText);

        switch (parsed)
        {
            case FrontmatterParser.Result.Parsed ok:
                var workflow = workflowFlag ?? ok.RecommendedWorkflow;
                var risk = riskFlag ?? ok.Risk;
                if (workflowFlag is not null
                    && ok.RecommendedWorkflow is not null
                    && !string.Equals(workflowFlag, ok.RecommendedWorkflow, StringComparison.Ordinal))
                {
                    error.WriteLine(
                        $"note: --workflow-profile '{workflowFlag}' overrides frontmatter recommended_workflow '{ok.RecommendedWorkflow}'");
                }

                if (riskFlag is not null
                    && ok.Risk is not null
                    && !string.Equals(riskFlag, ok.Risk, StringComparison.Ordinal))
                {
                    error.WriteLine(
                        $"note: --risk '{riskFlag}' overrides frontmatter risk '{ok.Risk}'");
                }

                return (ok.Body, workflow, risk);
            case FrontmatterParser.Result.Malformed:
                if (!string.IsNullOrWhiteSpace(bodyFile))
                    error.WriteLine(
                        $"warning: malformed YAML frontmatter in '{bodyFile}'; sending full body text without parsing metadata");
                else
                    error.WriteLine(
                        "warning: malformed YAML frontmatter; sending full body text without parsing metadata");
                return (bodyText, workflowFlag, riskFlag);
            default:
                if (!string.IsNullOrWhiteSpace(bodyFile))
                    error.WriteLine(
                        "warning: no frontmatter found in body file. Consider including recommended_workflow and risk.");
                return (bodyText, workflowFlag, riskFlag);
        }
    }

    private static Command BuildEdit(MohistCliApi api)
    {
        var cmd = new Command("edit", "Update an issue");
        var numberArg = NumberArg();
        var titleOpt = new Option<string?>("--title") { Description = "New title" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "New body (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read new body from a UTF-8 file path, or - for stdin (mutually exclusive with --body)" };
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var parentOpt = new Option<string?>("--parent") { Description = "Parent issue number or none" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var modelOpt = new Option<string?>("--model") { Description = "Model to use" };
        var modelVariantOpt = new Option<string?>("--model-variant") { Description = "Reasoning variant bound to --model (e.g. low/medium/high/max)" };
        var workflowProfileOpt = new Option<string?>("--workflow-profile") { Description = "Workflow profile ID" };
        var inheritWorkflowProfileOpt = new Option<bool>("--inherit-workflow-profile") { Description = "Clear the explicit Profile and inherit the Project default" };
        var repositoryOpt = new Option<string?>("--repo") { Description = "Target repository name for an eligible reassignment" };
        var stageModelsOpt = new Option<string?>("--stage-models") { Description = "Per-stage model map as inline JSON or @<file> (e.g. '{\"plan\":\"anthropic/claude-sonnet\"}')" };
        var stageModelVariantsOpt = new Option<string?>("--stage-model-variants") { Description = "Per-stage model variant map as inline JSON or @<file> (e.g. '{\"plan\":\"max\"}')" };
        var (readyOpt, draftOpt) = MohistCliCommands.IsDraftFlags("updating");
        var jsonOpt = MohistCliCommands.JsonSelectionOption(IssueDescriptor);
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(titleOpt);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(parentOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(modelVariantOpt);
        cmd.Options.Add(workflowProfileOpt);
        cmd.Options.Add(inheritWorkflowProfileOpt);
        cmd.Options.Add(repositoryOpt);
        cmd.Options.Add(stageModelsOpt);
        cmd.Options.Add(stageModelVariantsOpt);
        cmd.Options.Add(readyOpt);
        cmd.Options.Add(draftOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var title = ctx.GetValue(titleOpt);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var parent = ctx.GetValue(parentOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var model = ctx.GetValue(modelOpt);
            var modelVariant = ctx.GetValue(modelVariantOpt);
            var workflowProfile = ctx.GetValue(workflowProfileOpt);
            var inheritWorkflowProfile = ctx.GetValue(inheritWorkflowProfileOpt);
            var repository = ctx.GetValue(repositoryOpt);
            var ready = ctx.GetValue(readyOpt);
            var draft = ctx.GetValue(draftOpt);
            var selection = JsonSelection.Parse(IssueDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            var stageModels = ctx.GetValue(stageModelsOpt);
            var stageModelVariants = ctx.GetValue(stageModelVariantsOpt);
            var titleProvided = ctx.GetResult(titleOpt) is not null;
            var bodyProvided = ctx.GetResult(bodyOpt) is not null;
            var bodyFileProvided = ctx.GetResult(bodyFileOpt) is not null;
            var labelsProvided = ctx.GetResult(labelOpt) is not null;
            var priorityProvided = ctx.GetResult(priorityOpt) is not null;
            var parentProvided = ctx.GetResult(parentOpt) is not null;
            var modelProvided = ctx.GetResult(modelOpt) is not null;
            var workflowProfileProvided = ctx.GetResult(workflowProfileOpt) is not null;
            var inheritWorkflowProfileProvided = IsOptionProvided(ctx, inheritWorkflowProfileOpt);
            var repositoryProvided = ctx.GetResult(repositoryOpt) is not null;
            var stageModelsProvided = ctx.GetResult(stageModelsOpt) is not null;
            var stageModelVariantsProvided = ctx.GetResult(stageModelVariantsOpt) is not null;
            var readyProvided = IsOptionProvided(ctx, readyOpt);
            var draftProvided = IsOptionProvided(ctx, draftOpt);
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                if (workflowProfileProvided && inheritWorkflowProfileProvided)
                    return CommandHelpHook.RenderUsageFailure(
                        ctx, api.Error, "--workflow-profile and --inherit-workflow-profile are mutually exclusive");
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(IssueDescriptor, selection);
                var draftState = MohistCliCommands.ResolveDraftFlagState(ready, draft);
                if (draftState == MohistCliCommands.DraftFlagState.Conflicting)
                    return CommandHelpHook.RenderUsageFailure(
                        ctx, api.Error, "--ready and --draft are mutually exclusive");
                var bodySourceCount =
                    (bodyProvided ? 1 : 0)
                    + (bodyFileProvided ? 1 : 0);
                if (bodySourceCount > 1)
                    return CommandHelpHook.RenderUsageFailure(
                        ctx, api.Error, "the following options are mutually exclusive: --body, --body-file; pass only one");
                string? resolvedBody = null;
                var bodyWillBeSent = bodySourceCount > 0;
                if (bodyWillBeSent)
                {
                    var resolved = await BodyInputResolver.ResolveAsync(
                        body, bodyFile, api.FileSystem, api.StandardInput, TextWriter.Null);
                    if (resolved is BodyInputResolver.Result.Failure bodyFailure)
                        return CommandHelpHook.RenderUsageFailure(ctx, api.Error, bodyFailure.Message);
                    resolvedBody = ((BodyInputResolver.Result.Success)resolved).Body;
                }

                object? parentIssueNumber = null;
                if (parentProvided)
                {
                    if (string.Equals(parent, "none", StringComparison.OrdinalIgnoreCase))
                        parentIssueNumber = null;
                    else if (int.TryParse(parent, out var parentNumber) && parentNumber > 0)
                        parentIssueNumber = parentNumber;
                    else
                        return CommandHelpHook.RenderUsageFailure(
                            ctx, api.Error, "--parent expects a positive issue number or none");
                }

                object? stageModelsPayload = null;
                if (stageModelsProvided)
                {
                    var sm = await JsonInputResolver.ResolveAsync(stageModels, api.FileSystem, TextWriter.Null, "--stage-models");
                    if (sm is JsonInputResolver.Result.Failure failure)
                        return CommandHelpHook.RenderUsageFailure(ctx, api.Error, failure.Message);
                    stageModelsPayload = ((JsonInputResolver.Result.Success)sm).Value;
                }

                object? stageModelVariantsPayload = null;
                if (stageModelVariantsProvided)
                {
                    var smv = await JsonInputResolver.ResolveAsync(stageModelVariants, api.FileSystem, TextWriter.Null, "--stage-model-variants");
                    if (smv is JsonInputResolver.Result.Failure failure)
                        return CommandHelpHook.RenderUsageFailure(ctx, api.Error, failure.Message);
                    stageModelVariantsPayload = ((JsonInputResolver.Result.Success)smv).Value;
                }

                var labelParse = LabelDelta.Parse(labels);
                if (!labelParse.IsValid)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, labelParse.Error!);

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

                if (titleProvided)
                    payload["title"] = title;
                if (bodyWillBeSent)
                    payload["body"] = resolvedBody;
                if (priorityProvided)
                    payload["priority"] = priority;
                if (parentProvided)
                    payload["parentIssueNumber"] = parentIssueNumber;
                if (modelProvided)
                    payload["model"] = model;
                if (repositoryProvided)
                    payload["repositoryName"] = repository;

                if (workflowProfileProvided)
                {
                    payload["workflowProfileId"] = string.IsNullOrWhiteSpace(workflowProfile) ? null : workflowProfile;
                }
                else if (inheritWorkflowProfile)
                {
                    payload["workflowProfileId"] = null;
                }

                if (stageModelsProvided)
                    payload["stageModels"] = stageModelsPayload;

                if (stageModelVariantsProvided)
                    payload["stageModelVariants"] = stageModelVariantsPayload;

                if (labelsProvided)
                {
                    var issuePath = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/issues/{MohistCliCommands.Escape(number!)}";
                    var (loadExit, current) = await LoadCurrentLabelsAsync(api, issuePath);
                    if (loadExit != 0)
                        return loadExit;
                    payload["labels"] = LabelDelta.Apply(labelParse.Entries, current);
                }

                if (readyProvided || draftProvided)
                {
                    payload["isDraft"] = draftState == MohistCliCommands.DraftFlagState.Draft;
                }

                return await api.PrintMutationResourceAsync(
                    HttpMethod.Patch,
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}"),
                    payload,
                    IssueDescriptor,
                    selection,
                    data => api.RenderTableAsync(data, MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static async Task<(int ExitCode, IReadOnlyDictionary<string, string> Labels)> LoadCurrentLabelsAsync(
        MohistCliApi api, string issuePath)
    {
        try
        {
            var data = await api.GetDataAsync(issuePath);
            if (data is null) return (0, new Dictionary<string, string>(StringComparer.Ordinal));
            return (0, ParseLabelsFromIssue(data));
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return (1, new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    private static IReadOnlyDictionary<string, string> ParseLabelsFromIssue(System.Text.Json.Nodes.JsonNode? data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data is null) return result;
        var labels = data["labels"];
        if (labels is null) return result;
        if (labels is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                if (kvp.Value is null) continue;
                var str = kvp.Value.GetValue<string>();
                if (str is not null)
                    result[kvp.Key] = str;
            }
        }
        return result;
    }

    private static void PrintCreateGuidance(System.Text.Json.Nodes.JsonNode? data, TextWriter output)
    {
        if (data is null) return;
        var blocker = data["blocker"];
        var isDraft = data["isDraft"]?.GetValue<bool>() ?? false;
        var number = data["number"]?.GetValue<int?>();
        if (isDraft && number is int draftNumber)
        {
            output.WriteLine($"Mark the issue ready with 'mo issue edit {draftNumber} --ready' before starting.");
            return;
        }
        if (isDraft)
        {
            output.WriteLine("Mark the issue ready with 'mo issue edit <number> --ready' before starting.");
            return;
        }
        if (blocker is System.Text.Json.Nodes.JsonObject blockerObj)
        {
            var kind = blockerObj["kind"]?.GetValue<string>();
            if (kind == "waiting-for")
            {
                var issue = blockerObj["issue"] as System.Text.Json.Nodes.JsonObject;
                var blockedNumber = issue?["number"]?.GetValue<int?>();
                if (blockedNumber is int n)
                {
                    output.WriteLine($"Waiting for #{n} to be delivered before this issue can start.");
                    return;
                }
                output.WriteLine("Waiting for a prerequisite issue to be delivered before this issue can start.");
                return;
            }
        }
        if (number is int n2)
            output.WriteLine($"Tip: Run 'mo issue start {n2}' to begin processing");
    }
}
