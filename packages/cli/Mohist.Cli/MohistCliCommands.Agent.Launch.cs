using System.Net;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static Command BuildLaunch(MohistCliApi api)
    {
        var cmd = new Command(
            "launch",
            "Launch a generic AgentSession from an Agent profile. Returns both the AgentJob id (the work owner) and the AgentSession id (the conversation owner). Sends POST /api/projects/:projectId/agents/:agentId/sessions.");
        var agentRefArg = new Argument<string>("agent") { Description = "Agent name or id (resolves project-scoped)" };
        var promptOpt = new Option<string?>("--prompt") { Description = "Prompt text (mutually exclusive with --prompt-file)" };
        var promptFileOpt = new Option<string?>("--prompt-file") { Description = "Read prompt from a UTF-8 file path, or - for stdin (mutually exclusive with --prompt)" };
        var attachOpt = new Option<string[]?>("--attach")
        {
            Description = "Attach a local file to the input. Repeat for multiple files.",
            AllowMultipleArgumentsPerToken = true,
        };
        var issueRefOpt = new Option<int?>("--issue") { Description = "Optional context reference: record the issue number on the session metadata" };
        var epicRefOpt = new Option<string?>("--epic") { Description = "Optional context reference: record the epic number on the session metadata" };
        var repositoryRefOpt = new Option<string?>("--repo") { Description = "Optional context reference: record the repository on the session metadata" };
        var workspaceOpt = new Option<string?>("--workspace") { Description = "Bind to a named workspace" };
        var runtimeOpt = new Option<string?>("--runtime") { Description = "Execution runtime override (preview is fail-closed until capability admission)" };
        var modelOpt = new Option<string?>("--model") { Description = "Execution model override" };
        var variantOpt = new Option<string?>("--variant") { Description = "Execution variant override" };
        var reasoningEffortOpt = new Option<string?>("--reasoning-effort") { Description = "Execution reasoning effort override" };
        var previewOpt = new Option<bool>("--preview") { Description = "Resolve execution without creating an AgentJob or AgentSession" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var idempotencyKeyOpt = new Option<string?>("--idempotency-key") { Description = "Reuse this key to safely retry a launch after response loss" };
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentSessionLaunch)));

        cmd.Arguments.Add(agentRefArg);
        cmd.Options.Add(promptOpt);
        cmd.Options.Add(promptFileOpt);
        cmd.Options.Add(attachOpt);
        cmd.Options.Add(issueRefOpt);
        cmd.Options.Add(epicRefOpt);
        cmd.Options.Add(repositoryRefOpt);
        cmd.Options.Add(workspaceOpt);
        cmd.Options.Add(runtimeOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(variantOpt);
        cmd.Options.Add(reasoningEffortOpt);
        cmd.Options.Add(previewOpt);
        cmd.Options.Add(idempotencyKeyOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var agentRef = ctx.GetValue(agentRefArg);
            var prompt = ctx.GetValue(promptOpt);
            var promptFile = ctx.GetValue(promptFileOpt);
            var attachPaths = ctx.GetValue(attachOpt) ?? [];
            var issueRef = ctx.GetValue(issueRefOpt);
            var epicRef = ctx.GetValue(epicRefOpt);
            var repositoryRef = ctx.GetValue(repositoryRefOpt);
            var workspace = ctx.GetValue(workspaceOpt);
            var runtime = ctx.GetValue(runtimeOpt);
            var model = ctx.GetValue(modelOpt);
            var variant = ctx.GetValue(variantOpt);
            var reasoningEffort = ctx.GetValue(reasoningEffortOpt);
            var preview = ctx.GetValue(previewOpt);
            var suppliedIdempotencyKey = ctx.GetValue(idempotencyKeyOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return LaunchAsync();

            async Task<int> LaunchAsync()
            {
                var resolvedPrompt = attachPaths.Length > 0
                    && prompt is null
                    && string.IsNullOrWhiteSpace(promptFile)
                    ? new BodyInputResolver.Result.Success("")
                    : await BodyInputResolver.ResolveAsync(
                        prompt, promptFile,
                        new BodyInputResolver.SourceFlags("--prompt", "--prompt-file", "prompt"),
                        api.FileSystem, api.StandardInput, TextWriter.Null,
                        allowEmptyBody: attachPaths.Length > 0);
                if (resolvedPrompt is BodyInputResolver.Result.Failure promptFailure)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, promptFailure.Message);

                if (preview && attachPaths.Length > 0)
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--attach cannot be used with --preview because preview is read-only");

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var uploads = preview
                    ? []
                    : await AgentAttachmentInput.UploadAsync(api, resolvedProjectId, attachPaths, mode);
                if (uploads is null)
                    return 1;

                var promptText = ((BodyInputResolver.Result.Success)resolvedPrompt).Body;
                var idempotencyKey = string.IsNullOrWhiteSpace(suppliedIdempotencyKey)
                    ? Guid.NewGuid().ToString("N")
                    : suppliedIdempotencyKey;
                if (!preview && string.IsNullOrWhiteSpace(suppliedIdempotencyKey) && mode == "table")
                    api.Output.WriteLine($"Idempotency-Key: {idempotencyKey}");

                var contextRefs = BuildLaunchContext(issueRef, epicRef, repositoryRef, workspace);
                var attachmentIds = uploads.Select(attachment => attachment.Id).ToArray();
                var body = new JsonObject
                {
                    ["prompt"] = promptText,
                };
                if (contextRefs is not null)
                    body["context"] = JsonSerializer.SerializeToNode(contextRefs, JsonOptions);
                if (attachmentIds.Length > 0)
                    body["attachments"] = JsonSerializer.SerializeToNode(attachmentIds, JsonOptions);
                var execution = new JsonObject();
                AddIfProvided(execution, "runtime", runtime, runtime is not null);
                AddIfProvided(execution, "model", model, model is not null);
                AddIfProvided(execution, "variant", variant, variant is not null);
                AddIfProvided(execution, "reasoningEffort", reasoningEffort, reasoningEffort is not null);
                if (execution.Count > 0)
                    body["execution"] = execution;

                using var response = await api.SendAsync(
                    HttpMethod.Post,
                    ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agentRef!)}/sessions{(preview ? "/preview" : string.Empty)}"),
                    body,
                    printServerUnavailable: false,
                    headers: preview
                        ? new Dictionary<string, string> { ["X-Mohist-Launch-Origin"] = "cli" }
                        : new Dictionary<string, string>
                    {
                        ["Idempotency-Key"] = idempotencyKey!,
                        ["X-Mohist-Launch-Origin"] = "cli",
                    },
                    retries: 1);
                if (response is null)
                    return MohistCliApi.FailureExitCode(HttpStatusCode.ServiceUnavailable);

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    var node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);
                    if (node is JsonObject envelope
                        && envelope["code"]?.GetValue<string>() is "agent_not_configured" or "agent_not_executable")
                    {
                        await RenderExecutabilityRejectedAsync(api, envelope);
                        return Mohist.Cli.MohistCliApi.FailureExitCode(response);
                    }
                }

                if (preview || string.Equals(mode, "json", StringComparison.Ordinal))
                    return await api.PrintRawServerResponseAsync(response);
                return await api.PrintServerResponseAsync(
                    response,
                    mode: mode,
                    tableShape: nameof(MohistCliApi.TableShape.AgentSessionLaunch));
            }
        });
        return cmd;
    }
}
