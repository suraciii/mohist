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
        var epicRefOpt = new Option<int?>("--epic") { Description = "Optional context reference: record the epic number on the session metadata" };
        var repositoryRefOpt = new Option<string?>("--repo") { Description = "Optional context reference: record the repository on the session metadata" };
        var workspaceOpt = new Option<string?>("--workspace") { Description = "Bind to a named workspace" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var idempotencyKeyOpt = new Option<string?>("--idempotency-key") { Description = "Reuse this key to safely retry after response loss" };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Confirm the server-resolved execution scope without prompting" };
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
        cmd.Options.Add(yesOpt);
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
            var confirmScope = ctx.GetValue(yesOpt);
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

                using var preflight = await api.SendAsync(
                    HttpMethod.Post,
                    ProjectAgentsPath(resolvedProjectId, "/agent-tasks/preflight"),
                    body,
                    printServerUnavailable: false,
                    headers: new Dictionary<string, string>
                    {
                        ["Idempotency-Key"] = idempotencyKey,
                        ["X-Mohist-Launch-Origin"] = "cli",
                    },
                    retries: 1);
                if (preflight is null)
                    return MohistCliApi.FailureExitCode(HttpStatusCode.ServiceUnavailable);

                var preflightRaw = await preflight.Content.ReadAsStringAsync();
                var preflightNode = string.IsNullOrWhiteSpace(preflightRaw) ? null : JsonNode.Parse(preflightRaw);
                var preflightEnvelope = MohistCliApi.ExtractEnvelope(preflightNode, preflight);
                var preflightData = preflightEnvelope.Data as JsonObject;
                var scopeFingerprint = preflightData is null
                    ? null
                    : preflightData["scopeFingerprint"]?.GetValue<string>();

                // Older servers did not expose preflight. Keep their response
                // behavior deterministic while the current server always
                // returns a scope fingerprint here.
                if (scopeFingerprint is null)
                {
                    using var legacyResponse = new HttpResponseMessage(preflight.StatusCode)
                    {
                        Content = new StringContent(preflightRaw),
                    };
                    return await PrintTaskLaunchResponseAsync(api, legacyResponse, mode, idempotencyKey);
                }

                if (!preflightEnvelope.Success || !preflight.IsSuccessStatusCode)
                {
                    if (!string.IsNullOrWhiteSpace(preflightEnvelope.Error))
                        api.Error.WriteLine($"{preflightEnvelope.Error} (code={preflightEnvelope.Code})");
                    WriteTaskLaunchGuidance(api, preflightEnvelope.Code, idempotencyKey);
                    return MohistCliApi.FailureExitCode(preflight.StatusCode);
                }

                if (mode == "table")
                    RenderTaskPreflight(api, preflightData!);
                if (!await ConfirmTaskPreflightAsync(api, confirmScope, mode))
                    return CliExitCode.For(CliExitOutcome.Cancelled);

                using var response = await api.SendAsync(
                    HttpMethod.Post,
                    ProjectAgentsPath(resolvedProjectId, "/agent-tasks"),
                    body,
                    printServerUnavailable: false,
                    headers: new Dictionary<string, string>
                    {
                        ["Idempotency-Key"] = idempotencyKey,
                        ["X-Mohist-Launch-Origin"] = "cli",
                        ["X-Mohist-Agent-Preflight"] = scopeFingerprint!,
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

    private static void RenderTaskPreflight(MohistCliApi api, JsonObject data)
    {
        var execution = data["execution"] as JsonObject;
        var repositories = data["workspaceRepositories"] as JsonArray;
        api.Output.WriteLine("Preflight scope:");
        api.Output.WriteLine($"  agent:              {Text(data, "agentName")}");
        api.Output.WriteLine($"  execution:          {Text(execution, "runtime")} · {Text(execution, "model")}{(string.IsNullOrWhiteSpace(Text(execution, "variant")) ? "" : $" · {Text(execution, "variant")}")}");
        api.Output.WriteLine($"  workspace:          {Text(data, "workspace")}");
        api.Output.WriteLine($"  repository:         {Text(data, "repository", "workspace repositories")}");
        api.Output.WriteLine($"  issue / epic:       {(Text(data, "issueNumber") is { Length: > 0 } issue ? $"#{issue}" : "none")}{(Text(data, "epicNumber") is { Length: > 0 } epic ? $" / #{epic}" : "")}");
        api.Output.WriteLine($"  permission scope:   {Text(data, "permissionScope")}");
        api.Output.WriteLine($"  expected impact:    {Text(data, "expectedImpact")}");
        if (repositories is { Count: > 0 })
            api.Output.WriteLine($"  workspace repos:    {string.Join(", ", repositories.Select(item => item?.GetValue<string>() ?? ""))}");
    }

    private static async Task<bool> ConfirmTaskPreflightAsync(MohistCliApi api, bool explicitConfirmation, string mode)
    {
        if (explicitConfirmation)
            return true;
        if (mode == "json")
        {
            await api.Error.WriteLineAsync("--yes is required with --json because the resolved execution scope cannot be prompted on the JSON stream.");
            return false;
        }
        if (!api.Invocation.PromptsEnabled)
        {
            await api.Error.WriteLineAsync("--yes is required when input is non-interactive to confirm the resolved execution scope.");
            return false;
        }

        await api.Output.WriteAsync("Launch with this scope? [y/N] ");
        var answer = await api.Invocation.Input.ReadLineAsync(api.Invocation.CancellationToken);
        await api.Output.WriteLineAsync();
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(JsonObject? node, string property, string fallback = "")
    {
        if (node is null || node[property] is null)
            return fallback;
        return node[property]!.ToString();
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
        if (mode == "json")
        {
            var rawResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                var responseNode = string.IsNullOrWhiteSpace(rawResponse) ? null : JsonNode.Parse(rawResponse);
                var envelope = MohistCliApi.ExtractEnvelope(responseNode, response);
                api.Output.Write(rawResponse);
                if (envelope.Success && response.IsSuccessStatusCode)
                    return 0;

                WriteTaskLaunchGuidance(api, envelope.Code, idempotencyKey);
                return MohistCliApi.FailureExitCode(response);
            }
            catch (JsonException)
            {
                api.Output.Write(rawResponse);
                return MohistCliApi.FailureExitCode(response);
            }
        }

        string? responseCode = null;
        if (!response.IsSuccessStatusCode)
        {
            var rawResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                var responseNode = string.IsNullOrWhiteSpace(rawResponse) ? null : JsonNode.Parse(rawResponse);
                responseCode = MohistCliApi.ExtractEnvelope(responseNode, response).Code;
            }
            catch (JsonException)
            {
                // The shared response printer will report a non-JSON response
                // using the same transport/status handling as other commands.
            }
        }

        var exit = await api.PrintServerResponseAsync(
            response,
            mode: mode,
            tableShape: nameof(MohistCliApi.TableShape.AgentSessionLaunch));
        WriteTaskLaunchGuidance(api, responseCode, idempotencyKey);
        return exit;
    }

    private static void WriteTaskLaunchGuidance(MohistCliApi api, string? responseCode, string idempotencyKey)
    {
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
    }
}
