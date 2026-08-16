using System.CommandLine;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static Command BuildStart(MohistCliApi api)
    {
        var cmd = new Command(
            "start",
            "Create an Agent from a task and launch its first AgentSession. The task-first path does not take an Agent argument.");
        var promptOpt = new Option<string?>("--prompt") { Description = "Task prompt (mutually exclusive with --prompt-file)" };
        var promptFileOpt = new Option<string?>("--prompt-file") { Description = "Read the task prompt from a UTF-8 file path, or - for stdin (mutually exclusive with --prompt)" };
        var attachOpt = new Option<string[]?>("--attach")
        {
            Description = "Attach a local file to the task. Repeat for multiple files.",
            AllowMultipleArgumentsPerToken = true,
        };
        var nameOpt = new Option<string?>("--name") { Description = "Optional Agent name; otherwise derive one from the task" };
        var runtimeOpt = new Option<string?>("--runtime") { Description = "Execution runtime: opencode or pi" };
        var modelOpt = new Option<string?>("--model") { Description = "Model identifier in provider/model form" };
        var variantOpt = new Option<string?>("--variant") { Description = "Runtime-specific model variant" };
        var issueRefOpt = new Option<int?>("--issue") { Description = "Optional context reference: record the issue number on the session metadata" };
        var epicRefOpt = new Option<string?>("--epic") { Description = "Optional context reference: record the epic number on the session metadata" };
        var repositoryRefOpt = new Option<string?>("--repo") { Description = "Optional context reference: record the repository on the session metadata" };
        var workspaceOpt = new Option<string?>("--workspace") { Description = "Bind to a named workspace" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var idempotencyKeyOpt = new Option<string?>("--idempotency-key") { Description = "Reuse this key to safely retry after response loss" };
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentSessionLaunch)));

        cmd.Options.Add(promptOpt);
        cmd.Options.Add(promptFileOpt);
        cmd.Options.Add(attachOpt);
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(runtimeOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(variantOpt);
        cmd.Options.Add(issueRefOpt);
        cmd.Options.Add(epicRefOpt);
        cmd.Options.Add(repositoryRefOpt);
        cmd.Options.Add(workspaceOpt);
        cmd.Options.Add(idempotencyKeyOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var prompt = ctx.GetValue(promptOpt);
            var promptFile = ctx.GetValue(promptFileOpt);
            var attachPaths = ctx.GetValue(attachOpt) ?? [];
            var name = ctx.GetValue(nameOpt);
            var runtime = ctx.GetValue(runtimeOpt);
            var model = ctx.GetValue(modelOpt);
            var variant = ctx.GetValue(variantOpt);
            var issueRef = ctx.GetValue(issueRefOpt);
            var epicRef = ctx.GetValue(epicRefOpt);
            var repositoryRef = ctx.GetValue(repositoryRefOpt);
            var workspace = ctx.GetValue(workspaceOpt);
            var suppliedIdempotencyKey = ctx.GetValue(idempotencyKeyOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            var outputWasProvided = MohistCliCommands.OutputOptionState.Explicit;
            return StartAsync();

            async Task<int> StartAsync()
            {
                var executionHintError = ValidateStartExecutionHints(runtime, model, variant);
                if (executionHintError is not null)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, executionHintError);

                var resolvedPrompt = attachPaths.Length > 0
                    && prompt is null
                    && string.IsNullOrWhiteSpace(promptFile)
                    ? new BodyInputResolver.Result.Success("")
                    : await BodyInputResolver.ResolveAsync(
                        prompt,
                        promptFile,
                        new BodyInputResolver.SourceFlags("--prompt", "--prompt-file", "prompt"),
                        api.FileSystem,
                        api.StandardInput,
                        TextWriter.Null,
                        allowEmptyBody: attachPaths.Length > 0);
                if (resolvedPrompt is BodyInputResolver.Result.Failure promptFailure)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, promptFailure.Message);

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0)
                    return exit;

                // Task-first JSON is the complete Server projection. Bare
                // --json has no field-selection value, so treat it as raw JSON;
                // field subsets are deliberately not a second task contract.
                if (outputWasProvided && (string.IsNullOrWhiteSpace(output)
                    || string.Equals(output, "table", StringComparison.Ordinal)))
                    mode = "json";
                if (mode.StartsWith("json:", StringComparison.Ordinal))
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "mo agent start supports raw JSON only; omit the field selection after --json");

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0)
                    return resolveExit;

                var idempotencyKey = string.IsNullOrWhiteSpace(suppliedIdempotencyKey)
                    ? Guid.NewGuid().ToString("N")
                    : suppliedIdempotencyKey!;
                if (string.IsNullOrWhiteSpace(suppliedIdempotencyKey) && mode == "table")
                    api.Output.WriteLine($"Idempotency-Key: {idempotencyKey}");

                var uploads = await AgentAttachmentInput.UploadAsync(api, resolvedProjectId, attachPaths, mode);
                if (uploads is null)
                    return 1;

                var promptText = ((BodyInputResolver.Result.Success)resolvedPrompt).Body;
                var contextRefs = BuildLaunchContext(issueRef, epicRef, repositoryRef, workspace);
                var attachmentIds = uploads.Select(attachment => attachment.Id).ToArray();
                var body = new JsonObject
                {
                    ["prompt"] = promptText,
                };
                if (attachmentIds.Length > 0)
                    body["attachments"] = JsonSerializer.SerializeToNode(attachmentIds);
                if (contextRefs is not null)
                    body["context"] = JsonSerializer.SerializeToNode(contextRefs);
                if (name is not null)
                    body["name"] = name;
                if (runtime is not null)
                    body["runtime"] = runtime;
                if (model is not null)
                    body["model"] = model;
                if (variant is not null)
                    body["variant"] = variant;

                using var response = await api.SendAsync(
                    HttpMethod.Post,
                    ProjectAgentsPath(resolvedProjectId, "/agent-tasks"),
                    body,
                    printServerUnavailable: false,
                    headers: new Dictionary<string, string>
                    {
                        ["Idempotency-Key"] = idempotencyKey,
                        ["X-Mohist-Launch-Origin"] = "cli",
                    },
                    retries: 1);
                if (response is null)
                    return MohistCliApi.FailureExitCode(HttpStatusCode.ServiceUnavailable);

                return await PrintTaskLaunchResponseAsync(api, response, mode, idempotencyKey);
            }
        });
        AddStartPromptInputValidation(cmd, promptOpt, promptFileOpt);
        return cmd;
    }

    private static string? ValidateStartExecutionHints(string? runtime, string? model, string? variant)
    {
        if (runtime is not null && runtime is not ("opencode" or "pi"))
            return $"--runtime '{runtime}' is invalid; use opencode or pi";
        if (model is not null && string.IsNullOrWhiteSpace(model))
            return "--model must not be empty; use provider/model";
        if (model is not null)
        {
            var separator = model.IndexOf('/');
            if (separator <= 0 || separator == model.Length - 1)
                return "--model must use the provider/model form";
        }
        if (variant is not null && string.IsNullOrWhiteSpace(variant))
            return "--variant must not be empty; use the variant supported by the selected runtime";
        return null;
    }

    private static void AddStartPromptInputValidation(
        Command command,
        Option<string?> prompt,
        Option<string?> promptFile)
    {
        command.Validators.Add(result =>
        {
            if (result.GetResult(prompt) is not null && result.GetResult(promptFile) is not null)
                result.AddError("--prompt and --prompt-file are mutually exclusive.");
        });
    }

    private static async Task<int> PrintTaskLaunchResponseAsync(
        MohistCliApi api,
        HttpResponseMessage response,
        string mode,
        string idempotencyKey)
    {
        string? responseCode = null;
        string? rawResponse = null;
        JsonNode? responseNode = null;
        if (mode == "json" || !response.IsSuccessStatusCode)
        {
            rawResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                responseNode = string.IsNullOrWhiteSpace(rawResponse) ? null : JsonNode.Parse(rawResponse);
                responseCode = MohistCliApi.ExtractEnvelope(responseNode, response).Code;
            }
            catch (JsonException)
            {
                // The shared response printer will report a non-JSON response
                // using the same transport/status handling as other commands.
            }
        }

        if (mode == "json" && responseNode is not null
            && MohistCliApi.ExtractEnvelope(responseNode, response).Success)
        {
            api.Output.Write(rawResponse);
            if (rawResponse is null || !rawResponse.EndsWith('\n', StringComparison.Ordinal))
                api.Output.WriteLine();
            return 0;
        }

        var exit = await api.PrintServerResponseAsync(
            response,
            mode: mode,
            tableShape: nameof(MohistCliApi.TableShape.AgentSessionLaunch));
        if (responseCode == "execution_config_unresolvable")
        {
            api.Error.WriteLine(
                "Hint: pass --runtime/--model/--variant, or configure the Project default; "
                + "run 'mo agent model list' to view available models.");
        }
        else if (responseCode == "launch_setup_pending")
        {
            api.Error.WriteLine($"Hint: retry with the same --idempotency-key {idempotencyKey}.");
        }
        return exit;
    }
}
