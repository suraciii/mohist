using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    internal static readonly ResourceDescriptor AgentDescriptor =
        ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentShow));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static Command Build(MohistCliApi api)
    {
        var agent = new Command("agent", "Agent management");

        agent.Subcommands.Add(BuildCreate(api));
        agent.Subcommands.Add(BuildList(api));
        agent.Subcommands.Add(BuildView(api));
        agent.Subcommands.Add(BuildEdit(api));
        agent.Subcommands.Add(BuildArchive(api));
        agent.Subcommands.Add(BuildLaunch(api));
        agent.Subcommands.Add(BuildSpawn(api));
        agent.Subcommands.Add(BuildJob(api));
        agent.Subcommands.Add(BuildInstall(api));
        agent.Subcommands.Add(BuildSubscriptions(api));
        agent.Subcommands.Add(AgentModelCommands.Build(api));

        return agent;
    }

    private static Argument<string> NameOrIdArg() => new("name-or-id") { Description = "Agent name or id" };

    private static string ProjectAgentsPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static Command BuildInstall(MohistCliApi api)
    {
        var command = new Command("install", "Install a built-in agent preset.");
        var preset = new Argument<string>("preset") { Description = "Built-in preset name." };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(preset);
        command.Options.Add(project);
        command.SetAction(ctx => InstallAsync(ctx));

        async Task<int> InstallAsync(ParseResult ctx)
        {
            var catalog = PresetCatalog.CreateDefault(api.FileSystem, api.GetUserHome);
            var resolvedPreset = catalog.Resolve(ctx.GetValue(preset) ?? string.Empty);
            if (!resolvedPreset.Found || resolvedPreset.Preset is null)
            {
                api.Error.WriteLine(resolvedPreset.Error);
                return 1;
            }

            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0)
                return resolution.Exit;

            var projectPath = ProjectAgentsPath(resolution.ProjectId, "/agents");
            var agent = await EnsureAgentAsync(api, projectPath, resolution.ProjectId, resolvedPreset.Preset);
            if (agent is null)
                return 1;

            foreach (var rule in resolvedPreset.Preset.Rules)
            {
                if (!await EnsureRuleAsync(api, resolution.ProjectId, agent.Id, rule))
                    return 1;
            }

            await RunPreflightAsync(api, resolution.ProjectId);
            return 0;
        }

        return command;
    }

    private static async Task RunPreflightAsync(MohistCliApi api, string projectId)
    {
        var defaultRepo = await TryResolveDefaultRepositoryAsync(api, projectId);
        var preflight = BuildPreflight(api);
        var result = preflight.Run(api.FileSystem.CurrentDirectory, defaultRepo);
        foreach (var notice in result.Notices)
            await api.Output.WriteLineAsync(notice).ConfigureAwait(false);
        foreach (var warning in result.Warnings)
            await api.Output.WriteLineAsync(warning).ConfigureAwait(false);
    }

    internal static AgentInstallPreflight BuildPreflight(MohistCliApi api)
    {
        return new AgentInstallPreflight(api.FileSystem, NotifyCommands.ConfigPathOverride);
    }

    internal static async Task<DefaultRepository> TryResolveDefaultRepositoryAsync(
        MohistCliApi api, string projectId)
    {
        JsonNode? projectInfo;
        try
        {
            projectInfo = await api.GetDataAsync($"/api/projects/{Uri.EscapeDataString(projectId)}");
        }
        catch (HttpRequestException)
        {
            return DefaultRepository.Unresolved;
        }
        catch (MohistCliApi.ApiResponseException)
        {
            return DefaultRepository.Unresolved;
        }

        var repositories = projectInfo?["repositories"] as JsonArray;
        if (repositories is null)
            return DefaultRepository.Unresolved;

        foreach (var entry in repositories)
        {
            if (entry is not JsonObject repository)
                continue;
            if (repository["isDefault"]?.GetValue<bool>() != true)
                continue;
            var name = repository["name"]?.GetValue<string>();
            return DefaultRepository.Named(string.IsNullOrWhiteSpace(name) ? null : name);
        }

        return DefaultRepository.Unresolved;
    }

    private static async Task<AgentRef?> EnsureAgentAsync(MohistCliApi api, string path, string projectId, AgentPreset preset)
    {
        try
        {
            var existing = await api.GetDataAsync(path + "?all=true");
            if (existing is JsonArray agents)
            {
                foreach (var item in agents)
                {
                    var agent = AgentRef.From(item);
                    if (agent is not null && string.Equals(agent.Name, preset.Name, StringComparison.Ordinal))
                    {
                        api.Output.WriteLine($"exists, skipped: agent {preset.Name}");
                        return agent;
                    }
                }
            }

            using var response = await api.SendAsync(HttpMethod.Post, path, new
            {
                name = preset.Name,
                instructions = preset.Instructions,
                agentConfig = (object?)null,
                skills = (string[]?)null,
                maxConcurrentRuns = (int?)null,
            }, printServerUnavailable: false);
            if (response is null)
                return null;

            var data = await ReadDataOrPrintErrorAsync(api, response);
            if (data is null)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    // Concurrent install won the create race (409 AGENT_NAME_CONFLICT).
                    // Re-resolve by name against the real project id so the rules can
                    // bind to the now-existing agent. (Previously this sliced the URL
                    // path and passed it as projectId, producing a malformed URL.)
                    api.Output.WriteLine($"exists, skipped: agent {preset.Name}");
                    return await ResolveAgentAsync(api, projectId, preset.Name);
                }
                return null;
            }

            var created = AgentRef.From(data);
            if (created is null)
            {
                api.Error.WriteLine("Server returned an invalid agent response");
                return null;
            }
            api.Output.WriteLine($"created agent: {preset.Name}");
            return created;
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return null;
        }
    }

    private static async Task<bool> EnsureRuleAsync(MohistCliApi api, string projectId, string agentId, PresetRule rule)
    {
        var path = $"/api/projects/{Uri.EscapeDataString(projectId)}/routing/rules";
        try
        {
            var existing = await api.GetDataAsync(path);
            if (existing is JsonArray rules)
            {
                foreach (var item in rules)
                {
                    if (string.Equals(item?["name"]?.GetValue<string>(), rule.Name, StringComparison.Ordinal))
                    {
                        api.Output.WriteLine($"exists, skipped: routing rule {rule.Name}");
                        return true;
                    }
                }
            }

            using var response = await api.SendAsync(HttpMethod.Post, path, new
            {
                name = rule.Name,
                match = rule.Match,
                agentId,
                responsePrompt = rule.ResponsePrompt,
                @continue = (bool?)null,
            }, printServerUnavailable: false);
            if (response is null)
                return false;

            var data = await ReadDataOrPrintErrorAsync(api, response);
            if (data is null)
            {
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    api.Output.WriteLine($"exists, skipped: routing rule {rule.Name}");
                    return true;
                }
                return false;
            }

            api.Output.WriteLine($"created routing rule: {rule.Name}");
            return true;
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return false;
        }
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new agent");
        var nameOpt = new Option<string?>("--name") { Description = "Agent name" };
        var instructionsOpt = new Option<string?>("--instructions") { Description = "Agent instructions as literal text" };
        var instructionsFileOpt = new Option<string?>("--instructions-file") { Description = "Read Agent instructions from a UTF-8 file path, or - for stdin" };
        var descriptionOpt = new Option<string?>("--description") { Description = "Agent description" };
        var purposeOpt = new Option<string?>("--purpose") { Description = "Task purpose shown before starting the agent" };
        var agentConfigOpt = new Option<string?>("--agent-config") { Description = "Retired: use typed Agent configuration options" };
        agentConfigOpt.Hidden = true;
        var runtimeOpt = new Option<string?>("--runtime") { Description = "Execution runtime: opencode or pi" };
        var modelOpt = new Option<string?>("--model") { Description = "Model identifier, usually provider/model" };
        var reasoningEffortOpt = new Option<string?>("--reasoning-effort") { Description = "Canonical reasoning effort for the selected model" };
        var variantOpt = new Option<string?>("--variant") { Description = "Runtime-specific model variant" };
        var avatarFileOpt = new Option<string?>("--avatar-file") { Description = "Read the avatar URL or data URI from a UTF-8 file" };
        var skillsOpt = new Option<string?>("--skills") { Description = "Comma-separated skill names; include at least one non-empty name" };
        var permissionsOpt = new Option<string?>("--permissions") { Description = "Comma-separated declared permission terms" };
        var allowedSubagentOpt = AllowedSubagentOption();
        var maxConcurrentRunsOpt = new Option<int?>("--max-concurrent-runs") { Description = "Maximum concurrent runs; positive integer, omit for no limit" };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(AgentDescriptor);
        var projectOpt = MohistCliCommands.ProjectRefOption();

        cmd.Options.Add(nameOpt);
        cmd.Options.Add(instructionsOpt);
        cmd.Options.Add(instructionsFileOpt);
        cmd.Options.Add(descriptionOpt);
        cmd.Options.Add(purposeOpt);
        cmd.Options.Add(agentConfigOpt);
        cmd.Options.Add(runtimeOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(reasoningEffortOpt);
        cmd.Options.Add(variantOpt);
        cmd.Options.Add(avatarFileOpt);
        cmd.Options.Add(skillsOpt);
        cmd.Options.Add(permissionsOpt);
        cmd.Options.Add(allowedSubagentOpt);
        cmd.Options.Add(maxConcurrentRunsOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Options.Add(projectOpt);
        cmd.SetAction(ctx =>
                {
                    var name = ctx.GetValue(nameOpt);
                    var instructions = ctx.GetValue(instructionsOpt);
                    var instructionsFile = ctx.GetValue(instructionsFileOpt);
                    var description = ctx.GetValue(descriptionOpt);
                    var purpose = ctx.GetValue(purposeOpt);
                    var agentConfig = ctx.GetValue(agentConfigOpt);
                    var runtime = ctx.GetValue(runtimeOpt);
                    var model = ctx.GetValue(modelOpt);
                    var reasoningEffort = ctx.GetValue(reasoningEffortOpt);
                    var variant = ctx.GetValue(variantOpt);
                    var avatarFile = ctx.GetValue(avatarFileOpt);
                    var skills = ctx.GetValue(skillsOpt);
                    var permissions = ctx.GetValue(permissionsOpt);
                    var allowedSubagentAgentIds = ctx.GetValue(allowedSubagentOpt);
                    var maxConcurrentRuns = ctx.GetValue(maxConcurrentRunsOpt);
                    var selection = JsonSelection.Parse(AgentDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
                    var project = ctx.GetValue(projectOpt);
                    return CreateAsync();

                    async Task<int> CreateAsync()
                    {
                        if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                            return api.WriteJsonSelectionResult(AgentDescriptor, selection);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--name is required");
                        }

                        var config = ResolveTypedAgentConfig(
                            current: null,
                            agentConfig,
                            runtime,
                            model,
                            reasoningEffort,
                            variant,
                            clearRuntime: false,
                            clearModel: false,
                            clearReasoningEffort: false,
                            clearVariant: false);
                        if (config.Error is not null)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, config.Error);
                        if (maxConcurrentRuns is <= 0)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--max-concurrent-runs must be a positive integer; omit it for no limit");
                        var skillsError = ValidateSkills(skills);
                        if (skillsError is not null)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, skillsError);

                        var instructionsResult = await BodyInputResolver.ResolveAsync(
                            instructions,
                            instructionsFile,
                            new BodyInputResolver.SourceFlags("--instructions", "--instructions-file", "Agent instructions"),
                            api.FileSystem,
                            api.StandardInput,
                            TextWriter.Null);
                        if (instructionsResult is BodyInputResolver.Result.Failure instructionsFailure)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, instructionsFailure.Message);

                        BodyInputResolver.Result? avatarResult = null;
                        if (avatarFile is not null)
                        {
                            avatarResult = await BodyInputResolver.ResolveAsync(
                                null,
                                avatarFile,
                                new BodyInputResolver.SourceFlags("--avatar-file", "--avatar-file", "avatar value"),
                                api.FileSystem,
                                api.StandardInput,
                                TextWriter.Null);
                            if (avatarResult is BodyInputResolver.Result.Failure avatarFailure)
                                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, avatarFailure.Message);
                        }

                        var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                        if (resolveExit != 0) return resolveExit;

                        var resolvedInstructions = ((BodyInputResolver.Result.Success)instructionsResult).Body;

                        var path = ProjectAgentsPath(resolvedProjectId, "/agents");
                        var body = new
                        {
                            name,
                            description,
                            purpose,
                            instructions = resolvedInstructions,
                            agentConfig = config.Config,
                            avatar = avatarResult is BodyInputResolver.Result.Success avatarSuccess ? avatarSuccess.Body : null,
                            skills = ParseSkills(skills),
                            permissions = ParsePermissions(permissions) ?? Array.Empty<string>(),
                            allowedSubagentAgentIds,
                            maxConcurrentRuns,
                        };
                        return selection.Kind == JsonSelectionKind.Selected
                            ? await api.PrintMutationResourceAsync(HttpMethod.Post, path, body, AgentDescriptor, selection, data => api.RenderTableAsync(data, MohistCliApi.TableShape.AgentShow))
                            : await PrintAgentIdAsync(api, path, body);
                    }
                });
        AddInstructionsInputValidation(cmd, instructionsOpt, instructionsFileOpt);
        return cmd;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List agents");
        var allOpt = new Option<bool>("--all") { Description = "Include archived agents" };
        var statusOpt = new Option<string?>("--status") { Description = "Filter by status" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentList)));

        cmd.Options.Add(allOpt);
        cmd.Options.Add(statusOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var all = ctx.GetValue(allOpt);
            var status = ctx.GetValue(statusOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var localJsonExit = api.HandleLocalJsonSelection(mode, nameof(MohistCliApi.TableShape.AgentList));
                if (localJsonExit is not null) return localJsonExit.Value;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);

                if (resolveExit != 0) return resolveExit;

                var query = AgentQuery(all ? true : null, status);
                return await api.PrintWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, "/agents") + query,
                    mode,
                    nameof(MohistCliApi.TableShape.AgentList));
            }
        });
        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command("view", "Show agent details (Server-authoritative Executability, Availability, and waiting work)");
        var nameOrIdArg = NameOrIdArg();
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(AgentDescriptor);

        cmd.Arguments.Add(nameOrIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var nameOrId = ctx.GetValue(nameOrIdArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var localJsonExit = api.HandleLocalJsonSelection(mode, nameof(MohistCliApi.TableShape.AgentShow));
                if (localJsonExit is not null) return localJsonExit.Value;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);

                if (resolveExit != 0) return resolveExit;

                var agent = await ResolveAgentAsync(api, resolvedProjectId, nameOrId!);
                if (agent is null)
                    return 1;
                var agentPath = ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}");
                var statusPath = ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}/status");

                using var agentResponse = await api.SendAsync(HttpMethod.Get, agentPath, body: null, printServerUnavailable: false);
                if (agentResponse is null)
                    return MohistCliApi.FailureExitCode(HttpStatusCode.ServiceUnavailable);
                if (!agentResponse.IsSuccessStatusCode)
                {
                    await api.PrintServerResponseAsync(agentResponse);
                    return MohistCliApi.FailureExitCode(agentResponse);
                }

                await using var agentStream = await agentResponse.Content.ReadAsStreamAsync();
                var agentNode = agentStream.Length == 0 ? null : await JsonNode.ParseAsync(agentStream);
                if (agentNode is null)
                {
                    api.Output.WriteLine($"Server returned an empty agent response.");
                    return 1;
                }
                var agentData = (agentNode as JsonObject)?["data"] as JsonNode ?? agentNode;

                JsonNode? statusData = null;
                using var statusResponse = await api.SendAsync(HttpMethod.Get, statusPath, body: null, printServerUnavailable: false);
                if (statusResponse is { IsSuccessStatusCode: true })
                {
                    await using var statusStream = await statusResponse.Content.ReadAsStreamAsync();
                    var statusNode = statusStream.Length == 0 ? null : await JsonNode.ParseAsync(statusStream);
                    if ((statusNode as JsonObject)?["data"] is JsonNode dataNode)
                    {
                        statusData = dataNode;
                    }
                }

                if (string.Equals(mode, "json", StringComparison.Ordinal)
                    || mode.StartsWith("json:", StringComparison.Ordinal))
                {
                    var envelope = new JsonObject
                    {
                        ["success"] = true,
                        ["data"] = agentData.DeepClone(),
                    };
                    if (statusData is not null)
                        envelope["status"] = statusData.DeepClone();
                    api.Output.WriteLine(envelope.ToJsonString(JsonOptions));
                    return 0;
                }

                await api.RenderAgentShowAsync(agentData);
                if (statusData is not null)
                    await api.RenderAgentShowAvailabilityAsync(statusData);
                return 0;
            }
        });
        return cmd;
    }

    private static Command BuildEdit(MohistCliApi api)
    {
        var cmd = new Command("edit", "Update an agent");
        var nameOrIdArg = NameOrIdArg();
        var nameOpt = new Option<string?>("--name") { Description = "New agent name" };
        var descriptionOpt = new Option<string?>("--description") { Description = "New agent description" };
        var purposeOpt = new Option<string?>("--purpose") { Description = "Set task purpose; mutually exclusive with --clear-purpose" };
        var instructionsOpt = new Option<string?>("--instructions") { Description = "New Agent instructions as literal text" };
        var instructionsFileOpt = new Option<string?>("--instructions-file") { Description = "Read new Agent instructions from a UTF-8 file path, or - for stdin" };
        var agentConfigOpt = new Option<string?>("--agent-config") { Description = "Retired: use typed Agent configuration options" };
        agentConfigOpt.Hidden = true;
        var runtimeOpt = new Option<string?>("--runtime") { Description = "Set runtime: opencode or pi; mutually exclusive with --clear-runtime" };
        var modelOpt = new Option<string?>("--model") { Description = "Set model (usually provider/model); mutually exclusive with --clear-model" };
        var reasoningEffortOpt = new Option<string?>("--reasoning-effort") { Description = "Set canonical reasoning effort; mutually exclusive with --clear-reasoning-effort" };
        var variantOpt = new Option<string?>("--variant") { Description = "Set runtime-specific variant; mutually exclusive with --clear-variant" };
        var avatarFileOpt = new Option<string?>("--avatar-file") { Description = "Read avatar URL or data URI from UTF-8 file; mutually exclusive with --clear-avatar" };
        var skillsOpt = new Option<string?>("--skills") { Description = "Comma-separated skill names; include at least one non-empty name; use --clear-skills to clear" };
        var permissionsOpt = new Option<string?>("--permissions") { Description = "Set comma-separated declared permission terms; mutually exclusive with --clear-permissions" };
        var allowedSubagentOpt = AllowedSubagentOption();
        var maxConcurrentRunsOpt = new Option<int?>("--max-concurrent-runs") { Description = "Set positive maximum; omit for no limit; mutually exclusive with --clear-max-concurrent-runs" };
        var clearDescriptionOpt = new Option<bool>("--clear-description") { Description = "Clear the agent description" };
        var clearPurposeOpt = new Option<bool>("--clear-purpose") { Description = "Clear the task purpose; mutually exclusive with --purpose" };
        var clearAgentConfigOpt = new Option<bool>("--clear-agent-config") { Description = "Retired: use the typed clear options" };
        clearAgentConfigOpt.Hidden = true;
        var clearRuntimeOpt = new Option<bool>("--clear-runtime") { Description = "Clear runtime; mutually exclusive with --runtime" };
        var clearModelOpt = new Option<bool>("--clear-model") { Description = "Clear model; mutually exclusive with --model" };
        var clearReasoningEffortOpt = new Option<bool>("--clear-reasoning-effort") { Description = "Clear reasoning effort; mutually exclusive with --reasoning-effort" };
        var clearVariantOpt = new Option<bool>("--clear-variant") { Description = "Clear variant; mutually exclusive with --variant" };
        var clearAvatarOpt = new Option<bool>("--clear-avatar") { Description = "Clear avatar; mutually exclusive with --avatar-file" };
        var clearSkillsOpt = new Option<bool>("--clear-skills") { Description = "Clear skills; mutually exclusive with --skills" };
        var clearPermissionsOpt = new Option<bool>("--clear-permissions") { Description = "Clear declared permissions; mutually exclusive with --permissions" };
        var clearAllowedSubagentOpt = new Option<bool>("--clear-allowed-subagents") { Description = "Clear the allowed subagent agent ids" };
        var clearMaxConcurrentRunsOpt = new Option<bool>("--clear-max-concurrent-runs") { Description = "Clear maximum concurrent runs; mutually exclusive with --max-concurrent-runs" };
        var jsonOpt = MohistCliCommands.JsonSelectionOption(AgentDescriptor);
        var projectOpt = MohistCliCommands.ProjectRefOption();

        cmd.Arguments.Add(nameOrIdArg);
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(descriptionOpt);
        cmd.Options.Add(purposeOpt);
        cmd.Options.Add(instructionsOpt);
        cmd.Options.Add(instructionsFileOpt);
        cmd.Options.Add(agentConfigOpt);
        cmd.Options.Add(runtimeOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(reasoningEffortOpt);
        cmd.Options.Add(variantOpt);
        cmd.Options.Add(avatarFileOpt);
        cmd.Options.Add(skillsOpt);
        cmd.Options.Add(permissionsOpt);
        cmd.Options.Add(allowedSubagentOpt);
        cmd.Options.Add(maxConcurrentRunsOpt);
        cmd.Options.Add(clearDescriptionOpt);
        cmd.Options.Add(clearPurposeOpt);
        cmd.Options.Add(clearAgentConfigOpt);
        cmd.Options.Add(clearRuntimeOpt);
        cmd.Options.Add(clearModelOpt);
        cmd.Options.Add(clearReasoningEffortOpt);
        cmd.Options.Add(clearVariantOpt);
        cmd.Options.Add(clearAvatarOpt);
        cmd.Options.Add(clearSkillsOpt);
        cmd.Options.Add(clearPermissionsOpt);
        cmd.Options.Add(clearAllowedSubagentOpt);
        cmd.Options.Add(clearMaxConcurrentRunsOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Options.Add(projectOpt);
        cmd.SetAction(ctx =>
                {
                    var nameOrId = ctx.GetValue(nameOrIdArg);
                    var name = ctx.GetValue(nameOpt);
                    var description = ctx.GetValue(descriptionOpt);
                    var purpose = ctx.GetValue(purposeOpt);
                    var instructions = ctx.GetValue(instructionsOpt);
                    var instructionsFile = ctx.GetValue(instructionsFileOpt);
                    var agentConfig = ctx.GetValue(agentConfigOpt);
                    var runtime = ctx.GetValue(runtimeOpt);
                    var model = ctx.GetValue(modelOpt);
                    var reasoningEffort = ctx.GetValue(reasoningEffortOpt);
                    var variant = ctx.GetValue(variantOpt);
                    var avatarFile = ctx.GetValue(avatarFileOpt);
                    var skills = ctx.GetValue(skillsOpt);
                    var permissions = ctx.GetValue(permissionsOpt);
                    var allowedSubagentAgentIds = ctx.GetValue(allowedSubagentOpt);
                    var maxConcurrentRuns = ctx.GetValue(maxConcurrentRunsOpt);
                    var clearDescription = ctx.GetValue(clearDescriptionOpt);
                    var clearPurpose = ctx.GetValue(clearPurposeOpt);
                    var clearAgentConfig = ctx.GetValue(clearAgentConfigOpt);
                    var clearRuntime = ctx.GetValue(clearRuntimeOpt);
                    var clearModel = ctx.GetValue(clearModelOpt);
                    var clearReasoningEffort = ctx.GetValue(clearReasoningEffortOpt);
                    var clearVariant = ctx.GetValue(clearVariantOpt);
                    var clearAvatar = ctx.GetValue(clearAvatarOpt);
                    var clearSkills = ctx.GetValue(clearSkillsOpt);
                    var clearPermissions = ctx.GetValue(clearPermissionsOpt);
                    var clearAllowedSubagents = ctx.GetValue(clearAllowedSubagentOpt);
                    var clearMaxConcurrentRuns = ctx.GetValue(clearMaxConcurrentRunsOpt);
                    var selection = JsonSelection.Parse(AgentDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
                    var project = ctx.GetValue(projectOpt);
                    return UpdateAsync();

                    async Task<int> UpdateAsync()
                    {
                        if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                            return api.WriteJsonSelectionResult(AgentDescriptor, selection);
                        var clearSetConflict = ValidateClearSetPair("--description", description is not null, "--clear-description", clearDescription)
                            ?? ValidateClearSetPair("--purpose", purpose is not null, "--clear-purpose", clearPurpose)
                            ?? ValidateClearSetPair("--runtime", runtime is not null, "--clear-runtime", clearRuntime)
                            ?? ValidateClearSetPair("--model", model is not null, "--clear-model", clearModel)
                            ?? ValidateClearSetPair("--reasoning-effort", reasoningEffort is not null, "--clear-reasoning-effort", clearReasoningEffort)
                            ?? ValidateClearSetPair("--variant", variant is not null, "--clear-variant", clearVariant)
                            ?? ValidateClearSetPair("--avatar-file", avatarFile is not null, "--clear-avatar", clearAvatar)
                            ?? ValidateClearSetPair("--skills", skills is not null, "--clear-skills", clearSkills)
                            ?? ValidateClearSetPair("--permissions", permissions is not null, "--clear-permissions", clearPermissions)
                            ?? ValidateClearSetPair("--allowed-subagent", allowedSubagentAgentIds is not null, "--clear-allowed-subagents", clearAllowedSubagents)
                            ?? ValidateClearSetPair("--max-concurrent-runs", maxConcurrentRuns is not null, "--clear-max-concurrent-runs", clearMaxConcurrentRuns);
                        if (clearSetConflict is not null)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, clearSetConflict);
                        if (maxConcurrentRuns is <= 0)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--max-concurrent-runs must be a positive integer; omit it or use --clear-max-concurrent-runs");
                        var skillsError = ValidateSkills(skills);
                        if (skillsError is not null)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, skillsError);
                        if (clearAgentConfig)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--clear-agent-config is retired; use --clear-runtime, --clear-model, --clear-reasoning-effort, and --clear-variant");

                        BodyInputResolver.Result? instructionsResult = null;
                        if (instructions is not null || instructionsFile is not null)
                        {
                            instructionsResult = await BodyInputResolver.ResolveAsync(
                                instructions,
                                instructionsFile,
                                new BodyInputResolver.SourceFlags("--instructions", "--instructions-file", "Agent instructions"),
                                api.FileSystem,
                                api.StandardInput,
                                TextWriter.Null);
                            if (instructionsResult is BodyInputResolver.Result.Failure instructionsFailure)
                                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, instructionsFailure.Message);
                        }

                        if (agentConfig is not null)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--agent-config is retired; use --runtime, --model, --reasoning-effort, and --variant");

                        var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                        if (resolveExit != 0) return resolveExit;

                        var agent = await ResolveAgentAsync(api, resolvedProjectId, nameOrId!);
                        if (agent is null)
                            return 1;

                        var config = ResolveTypedAgentConfig(
                            agent.AgentConfig,
                            agentConfig,
                            runtime,
                            model,
                            reasoningEffort,
                            variant,
                            clearRuntime,
                            clearModel,
                            clearReasoningEffort,
                            clearVariant);
                        if (config.Error is not null)
                            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, config.Error);

                        BodyInputResolver.Result? avatarResult = null;
                        if (avatarFile is not null)
                        {
                            avatarResult = await BodyInputResolver.ResolveAsync(
                                null,
                                avatarFile,
                                new BodyInputResolver.SourceFlags("--avatar-file", "--avatar-file", "avatar value"),
                                api.FileSystem,
                                api.StandardInput,
                                TextWriter.Null);
                            if (avatarResult is BodyInputResolver.Result.Failure avatarFailure)
                                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, avatarFailure.Message);
                        }

                        var resolvedInstructions = instructionsResult is BodyInputResolver.Result.Success instructionsSuccess
                            ? instructionsSuccess.Body
                            : null;

                        var body = new JsonObject();
                        AddIfProvided(body, "name", name, name is not null);
                        AddIfProvided(body, "description", clearDescription ? null : description, clearDescription || description is not null);
                        AddIfProvided(body, "purpose", clearPurpose ? null : purpose, clearPurpose || purpose is not null);
                        AddIfProvided(body, "instructions", resolvedInstructions, instructionsResult is BodyInputResolver.Result.Success);
                        AddIfProvided(body, "agentConfig", config.Config, config.Config is not null || runtime is not null || model is not null || reasoningEffort is not null || variant is not null || clearRuntime || clearModel || clearReasoningEffort || clearVariant);
                        AddIfProvided(body, "avatar", clearAvatar ? null : avatarResult is BodyInputResolver.Result.Success avatarSuccess ? avatarSuccess.Body : null, clearAvatar || avatarResult is BodyInputResolver.Result.Success);
                        AddIfProvided(body, "skills", clearSkills ? null : JsonSerializer.SerializeToNode(ParseSkills(skills), JsonOptions), clearSkills || skills is not null);
                        AddIfProvided(body, "permissions", JsonSerializer.SerializeToNode(clearPermissions ? Array.Empty<string>() : ParsePermissions(permissions) ?? Array.Empty<string>(), JsonOptions), clearPermissions || permissions is not null);
                        AddIfProvided(body, "allowedSubagentAgentIds", clearAllowedSubagents ? null : JsonSerializer.SerializeToNode(allowedSubagentAgentIds, JsonOptions), clearAllowedSubagents || allowedSubagentAgentIds is not null);
                        AddIfProvided(body, "maxConcurrentRuns", clearMaxConcurrentRuns ? null : maxConcurrentRuns, clearMaxConcurrentRuns || maxConcurrentRuns is not null);

                        var path = ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}");
                        return selection.Kind == JsonSelectionKind.Selected
                            ? await api.PrintMutationResourceAsync(HttpMethod.Patch, path, body, AgentDescriptor, selection, data => api.RenderTableAsync(data, MohistCliApi.TableShape.AgentShow))
                            : await api.PrintPatchAsync(path, body);
                    }
                });
        AddInstructionsInputValidation(cmd, instructionsOpt, instructionsFileOpt);
        return cmd;
    }

    private static Command BuildArchive(MohistCliApi api)
    {
        var cmd = new Command("archive", "Archive an agent");
        var nameOrIdArg = NameOrIdArg();
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var jsonOpt = MohistCliCommands.JsonSelectionOption(AgentDescriptor);

        cmd.Arguments.Add(nameOrIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(jsonOpt);
        cmd.SetAction(ctx =>
        {
            var nameOrId = ctx.GetValue(nameOrIdArg);
            var project = ctx.GetValue(projectOpt);
            var selection = JsonSelection.Parse(AgentDescriptor, ctx.GetResult(jsonOpt) is not null, ctx.GetValue(jsonOpt));
            return ArchiveAsync();

            async Task<int> ArchiveAsync()
            {
                if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                    return api.WriteJsonSelectionResult(AgentDescriptor, selection);
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);

                if (resolveExit != 0) return resolveExit;

                var agent = await ResolveAgentAsync(api, resolvedProjectId, nameOrId!);
                if (agent is null)
                    return 1;

                var archivePath = ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}");
                if (selection.Kind == JsonSelectionKind.Selected)
                    return await api.PrintMutationResourceAsync(HttpMethod.Delete, archivePath, null, AgentDescriptor, selection, data => api.RenderTableAsync(data, MohistCliApi.TableShape.AgentShow));

                var archived = await DeleteAgentAsync(api, archivePath);
                if (archived is null)
                    return 1;
                api.Output.WriteLine($"Agent {archived.Name} ({archived.Id}) archived");
                return 0;
            }
        });
        return cmd;
    }

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

                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var uploads = await AgentAttachmentInput.UploadAsync(api, resolvedProjectId, attachPaths, mode);
                if (uploads is null)
                    return 1;

                var promptText = ((BodyInputResolver.Result.Success)resolvedPrompt).Body;
                var idempotencyKey = string.IsNullOrWhiteSpace(suppliedIdempotencyKey)
                    ? Guid.NewGuid().ToString("N")
                    : suppliedIdempotencyKey;
                if (string.IsNullOrWhiteSpace(suppliedIdempotencyKey) && mode == "table")
                    api.Output.WriteLine($"Idempotency-Key: {idempotencyKey}");

                var contextRefs = BuildLaunchContext(issueRef, epicRef, repositoryRef, workspace);
                var attachmentIds = uploads.Select(attachment => attachment.Id).ToArray();
                object body = contextRefs is null && attachmentIds.Length == 0
                    ? new { prompt = promptText }
                    : contextRefs is null
                        ? new { prompt = promptText, attachments = attachmentIds }
                        : attachmentIds.Length == 0
                            ? new { prompt = promptText, context = contextRefs }
                            : new { prompt = promptText, context = contextRefs, attachments = attachmentIds };

                using var response = await api.SendAsync(
                    HttpMethod.Post,
                    ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agentRef!)}/sessions"),
                    body,
                    printServerUnavailable: false,
                    headers: new Dictionary<string, string>
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
                        return MohistCliApi.FailureExitCode(response);
                    }
                }

                if (string.Equals(mode, "json", StringComparison.Ordinal))
                    return await api.PrintRawServerResponseAsync(response);
                return await api.PrintServerResponseAsync(
                    response,
                    mode: mode,
                    tableShape: nameof(MohistCliApi.TableShape.AgentSessionLaunch));
            }
        });
        return cmd;
    }

    private static Command BuildSpawn(MohistCliApi api)
    {
        var cmd = new Command(
            "spawn",
            "Spawn an allowed child AgentSession from a parent session. Sends targetAgentRef and prompt to the Server spawn endpoint; the child always inherits the parent's workdir.");
        var agentRefArg = new Argument<string>("agent-ref") { Description = "Stable target Agent ref" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var parentSessionOpt = new Option<string?>("--parent-session") { Description = "Parent AgentSession id" };
        var promptOpt = new Option<string?>("--prompt") { Description = "Child session prompt" };
        var idempotencyKeyOpt = new Option<string?>("--idempotency-key") { Description = "Required stable retry key" };
        var workspaceOpt = new Option<string?>("--workspace")
        {
            Description = "Retired: the workspace-mode concept was removed; child sessions always inherit the parent workdir",
        };
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentSessionSpawn)));

        cmd.Arguments.Add(agentRefArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(parentSessionOpt);
        cmd.Options.Add(promptOpt);
        cmd.Options.Add(idempotencyKeyOpt);
        cmd.Options.Add(workspaceOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx => SpawnAsync(ctx));

        async Task<int> SpawnAsync(ParseResult ctx)
        {
            var project = ctx.GetValue(projectOpt);
            var parentSessionId = ctx.GetValue(parentSessionOpt);
            var prompt = ctx.GetValue(promptOpt);
            var idempotencyKey = ctx.GetValue(idempotencyKeyOpt);
            var agentRef = ctx.GetValue(agentRefArg);
            var workspace = ctx.GetValue(workspaceOpt);
            var output = ctx.GetValue(outputOpt);

            if (string.IsNullOrWhiteSpace(project))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--project is required");
            if (string.IsNullOrWhiteSpace(parentSessionId))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--parent-session is required");
            if (string.IsNullOrWhiteSpace(prompt))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--prompt is required");
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--idempotency-key is required");
            if (workspace is not null)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--workspace was retired: child sessions always inherit the parent workdir");

            var (mode, exit) = api.ResolveOutputMode(output);
            if (exit != 0)
                return exit;

            var body = new { targetAgentRef = agentRef, prompt };
            return await api.PrintPostWithOutputAsync(
                $"/api/projects/{MohistCliCommands.Escape(project)}/agent-sessions/{MohistCliCommands.Escape(parentSessionId)}/spawns",
                body,
                mode,
                nameof(MohistCliApi.TableShape.AgentSessionSpawn),
                rawJson: true,
                headers: new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey },
                retries: 1);
        }

        return cmd;
    }

    private static object? BuildLaunchContext(int? issue, string? epic, string? repository, string? workspace)
    {
        if (issue is null && string.IsNullOrWhiteSpace(epic) && string.IsNullOrWhiteSpace(repository) && string.IsNullOrWhiteSpace(workspace))
            return null;

        return new
        {
            issueNumber = issue,
            epicNumber = string.IsNullOrWhiteSpace(epic) ? null : epic,
            repository = string.IsNullOrWhiteSpace(repository) ? null : repository,
            workspace = string.IsNullOrWhiteSpace(workspace) ? null : workspace,
        };
    }

    private static void AddInstructionsInputValidation(
        Command command,
        Option<string?> instructions,
        Option<string?> instructionsFile)
    {
        command.Validators.Add(result =>
        {
            if (string.Equals(result.GetValue(instructions), "-", StringComparison.Ordinal))
            {
                result.AddError("--instructions - is not supported; use --instructions-file - for stdin.");
                return;
            }

            if (result.GetResult(instructions) is not null && result.GetResult(instructionsFile) is not null)
                result.AddError("--instructions and --instructions-file are mutually exclusive.");
        });
    }

    private static string AgentQuery(bool? all = null, string? status = null)
    {
        var parts = new List<string>();
        if (all is true)
            parts.Add("all=true");
        if (!string.IsNullOrWhiteSpace(status))
            parts.Add($"status={Uri.EscapeDataString(status)}");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static Command BuildJob(MohistCliApi api)
    {
        var job = new Command(
            "job",
            "Read an Agent's work-result owner (issue-479). Subcommands: list <agent>, view <job-id>, observation <job-id>.");

        job.Subcommands.Add(BuildJobList(api));
        AddJobViewAndObservation(job, api);

        return job;
    }

    private static Command BuildJobList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List AgentJobs for a given Agent profile. Resolves the agent ref client-side, then GETs .../agents/{agentId}/jobs.");
        var agentRefArg = new Argument<string>("agent") { Description = "Agent name or id (resolves project-scoped)" };
        var statusOpt = new Option<string?>("--status") { Description = "Filter by job status (pending, running, completed, failed)" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentJobList)));

        cmd.Arguments.Add(agentRefArg);
        cmd.Options.Add(statusOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var agentRef = ctx.GetValue(agentRefArg);
            var status = ctx.GetValue(statusOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var agent = await ResolveAgentAsync(api, resolvedProjectId, agentRef!);
                if (agent is null)
                    return 1;

                var query = string.IsNullOrWhiteSpace(status)
                    ? ""
                    : $"?status={Uri.EscapeDataString(status)}";
                return await api.PrintWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}/jobs") + query,
                    mode,
                    nameof(MohistCliApi.TableShape.AgentJobList));
            }
        });
        return cmd;
    }

    private static Command BuildJobView(MohistCliApi api)
    {
        var cmd = new Command(
            "view",
            "Show an AgentJob's current status and terminal result. GETs .../agent-jobs/{jobId}.");
        var jobIdArg = new Argument<string>("job-id") { Description = "Agent job id returned by launch" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentJobView)));

        cmd.Arguments.Add(jobIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var jobId = ctx.GetValue(jobIdArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ViewAsync();

            async Task<int> ViewAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                return await api.PrintWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, $"/agent-jobs/{MohistCliCommands.Escape(jobId!)}"),
                    mode,
                    nameof(MohistCliApi.TableShape.AgentJobView));
            }
        });
        return cmd;
    }

    // Shared across command groups (e.g. `mo issue watch add --agent <name>`),
    // mirrors the agent-resolution shape every other command uses. Returns
    // null and writes to Error on a missing agent or transport failure so the
    // caller can return a non-zero exit code.
    internal static async Task<AgentRef?> ResolveAgentAsync(MohistCliApi api, string projectId, string nameOrId)
    {
        try
        {
            if (nameOrId.StartsWith("agent_", StringComparison.Ordinal))
            {
                var (exitCode, data) = await api.GetDataOrPrintErrorAsync(
                    ProjectAgentsPath(projectId, $"/agents/{MohistCliCommands.Escape(nameOrId)}"));
                if (exitCode != 0)
                    return null;
                return AgentRef.From(data);
            }

            var list = await api.GetDataAsync(ProjectAgentsPath(projectId, "/agents?all=true"));
            if (list is JsonArray agents)
            {
                foreach (var item in agents)
                {
                    var agent = AgentRef.From(item);
                    if (agent is not null && string.Equals(agent.Name, nameOrId, StringComparison.Ordinal))
                        return agent;
                }
            }

            api.Error.WriteLine($"Agent '{nameOrId}' not found");
            return null;
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            api.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private static async Task<int> PrintAgentIdAsync(MohistCliApi api, string path, object body)
    {
        try
        {
            using var response = await api.SendAsync(HttpMethod.Post, path, body);
            if (response is null)
                return 1;

            var data = await ReadDataOrPrintErrorAsync(api, response);
            if (data is null)
                return MohistCliApi.FailureExitCode(response);
            var id = data["id"]?.GetValue<string>();
            api.Output.WriteLine(string.IsNullOrWhiteSpace(id) ? data.ToJsonString(JsonOptions) : id);
            return 0;
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return 1;
        }
    }

    private static async Task<AgentRef?> DeleteAgentAsync(MohistCliApi api, string path)
    {
        try
        {
            using var response = await api.SendAsync(HttpMethod.Delete, path, body: null);
            if (response is null)
                return null;

            var data = await ReadDataOrPrintErrorAsync(api, response);
            return AgentRef.From(data);
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return null;
        }
    }

    private static async Task<JsonNode?> ReadDataOrPrintErrorAsync(MohistCliApi api, HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        JsonNode? node = stream.Length == 0 ? null : await JsonNode.ParseAsync(stream);

        var envelope = MohistCliApi.ExtractEnvelope(node, response);
        if (envelope.Success)
            return envelope.Data;

        api.Error.WriteLine(envelope.Code is null ? envelope.Error : $"{envelope.Error} ({envelope.Code})");
        return null;
    }

    internal sealed record AgentRef(string Id, string Name, JsonNode? AgentConfig = null, string? Avatar = null)
    {
        public static AgentRef? From(JsonNode? node)
        {
            var id = node?["id"]?.GetValue<string>();
            var name = node?["name"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)
                ? null
                : new AgentRef(id, name, node?["agentConfig"]?.DeepClone(), node?["avatar"]?.GetValue<string>());
        }
    }
}
