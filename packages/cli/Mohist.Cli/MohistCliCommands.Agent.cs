using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class AgentCommands
{
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
        agent.Subcommands.Add(BuildShow(api));
        agent.Subcommands.Add(BuildUpdate(api));
        agent.Subcommands.Add(BuildArchive(api));
        agent.Subcommands.Add(BuildSession(api));
        agent.Subcommands.Add(BuildInstall(api));

        return agent;
    }

    private static Argument<string> NameOrIdArg() => new("name-or-id") { Description = "Agent name or id" };

    private static string ProjectAgentSessionsPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/agent-sessions{(path.StartsWith('/') ? path : "/" + path)}";
    }

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
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(preset);
        command.Options.Add(project);
        command.Options.Add(projectId);
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

            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
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
        var instructionsOpt = new Option<string?>("--instructions") { Description = "Agent instructions (literal text, @file, or - with --instructions-stdin)" };
        var instructionsStdinOpt = new Option<bool>("--instructions-stdin") { Description = "Read agent instructions from stdin" };
        var descriptionOpt = new Option<string?>("--description") { Description = "Agent description" };
        var agentConfigOpt = new Option<string?>("--agent-config") { Description = "Agent config JSON or @file" };
        var skillsOpt = new Option<string?>("--skills") { Description = "Comma-separated skill names" };
        var maxConcurrentRunsOpt = new Option<int?>("--max-concurrent-runs") { Description = "Maximum concurrent runs" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();

        cmd.Options.Add(nameOpt);
        cmd.Options.Add(instructionsOpt);
        cmd.Options.Add(instructionsStdinOpt);
        cmd.Options.Add(descriptionOpt);
        cmd.Options.Add(agentConfigOpt);
        cmd.Options.Add(skillsOpt);
        cmd.Options.Add(maxConcurrentRunsOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameOpt);
            var instructions = ctx.GetValue(instructionsOpt);
            var instructionsStdin = ctx.GetValue(instructionsStdinOpt);
            var description = ctx.GetValue(descriptionOpt);
            var agentConfig = ctx.GetValue(agentConfigOpt);
            var skills = ctx.GetValue(skillsOpt);
            var maxConcurrentRuns = ctx.GetValue(maxConcurrentRunsOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    api.Error.WriteLine("--name is required");
                    return 1;
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;

                var resolvedInstructions = await ResolveInstructionsAsync(instructions, instructionsStdin, api);
                if (resolvedInstructions is null)
                    return 1;

                var config = await ResolveJsonAsync(agentConfig, api);
                if (config is ResolveJsonResult.Invalid)
                    return 1;

                return await PrintAgentIdAsync(api, ProjectAgentsPath(resolvedProjectId, "/agents"), new
                {
                    name,
                    description,
                    instructions = resolvedInstructions,
                    agentConfig = ((ResolveJsonResult.Valid)config).Value,
                    skills = ParseSkills(skills),
                    maxConcurrentRuns,
                });
            }
        });
        return cmd;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List agents");
        cmd.Aliases.Add("ls");
        var allOpt = new Option<bool>("--all") { Description = "Include archived agents" };
        var statusOpt = new Option<string?>("--status") { Description = "Filter by status" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();

        cmd.Options.Add(allOpt);
        cmd.Options.Add(statusOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var all = ctx.GetValue(allOpt);
            var status = ctx.GetValue(statusOpt);
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

                var query = AgentQuery(all ? true : null, status);
                return await api.PrintWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, "/agents") + query,
                    mode,
                    nameof(MohistCliApi.TableShape.AgentList));
            }
        });
        return cmd;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show agent details");
        var nameOrIdArg = NameOrIdArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();

        cmd.Arguments.Add(nameOrIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var nameOrId = ctx.GetValue(nameOrIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                var agent = await ResolveAgentAsync(api, resolvedProjectId, nameOrId!);
                if (agent is null)
                    return 1;
                return await api.PrintWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}"),
                    mode,
                    nameof(MohistCliApi.TableShape.AgentShow));
            }
        });
        return cmd;
    }

    private static Command BuildUpdate(MohistCliApi api)
    {
        var cmd = new Command("update", "Update an agent");
        var nameOrIdArg = NameOrIdArg();
        var nameOpt = new Option<string?>("--name") { Description = "New agent name" };
        var descriptionOpt = new Option<string?>("--description") { Description = "New agent description" };
        var instructionsOpt = new Option<string?>("--instructions") { Description = "New agent instructions (literal text, @file, or - with --instructions-stdin)" };
        var instructionsStdinOpt = new Option<bool>("--instructions-stdin") { Description = "Read new agent instructions from stdin" };
        var agentConfigOpt = new Option<string?>("--agent-config") { Description = "New agent config JSON or @file" };
        var skillsOpt = new Option<string?>("--skills") { Description = "Comma-separated skill names" };
        var maxConcurrentRunsOpt = new Option<int?>("--max-concurrent-runs") { Description = "Maximum concurrent runs" };
        var clearDescriptionOpt = new Option<bool>("--clear-description") { Description = "Clear the agent description" };
        var clearAgentConfigOpt = new Option<bool>("--clear-agent-config") { Description = "Clear the agent config" };
        var clearSkillsOpt = new Option<bool>("--clear-skills") { Description = "Clear the agent skills" };
        var clearMaxConcurrentRunsOpt = new Option<bool>("--clear-max-concurrent-runs") { Description = "Clear the maximum concurrent runs" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();

        cmd.Arguments.Add(nameOrIdArg);
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(descriptionOpt);
        cmd.Options.Add(instructionsOpt);
        cmd.Options.Add(instructionsStdinOpt);
        cmd.Options.Add(agentConfigOpt);
        cmd.Options.Add(skillsOpt);
        cmd.Options.Add(maxConcurrentRunsOpt);
        cmd.Options.Add(clearDescriptionOpt);
        cmd.Options.Add(clearAgentConfigOpt);
        cmd.Options.Add(clearSkillsOpt);
        cmd.Options.Add(clearMaxConcurrentRunsOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var nameOrId = ctx.GetValue(nameOrIdArg);
            var name = ctx.GetValue(nameOpt);
            var description = ctx.GetValue(descriptionOpt);
            var instructions = ctx.GetValue(instructionsOpt);
            var instructionsStdin = ctx.GetValue(instructionsStdinOpt);
            var agentConfig = ctx.GetValue(agentConfigOpt);
            var skills = ctx.GetValue(skillsOpt);
            var maxConcurrentRuns = ctx.GetValue(maxConcurrentRunsOpt);
            var clearDescription = ctx.GetValue(clearDescriptionOpt);
            var clearAgentConfig = ctx.GetValue(clearAgentConfigOpt);
            var clearSkills = ctx.GetValue(clearSkillsOpt);
            var clearMaxConcurrentRuns = ctx.GetValue(clearMaxConcurrentRunsOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                if (!ValidateClearSetPair(api, "--description", description is not null, "--clear-description", clearDescription)
                    || !ValidateClearSetPair(api, "--agent-config", agentConfig is not null, "--clear-agent-config", clearAgentConfig)
                    || !ValidateClearSetPair(api, "--skills", skills is not null, "--clear-skills", clearSkills)
                    || !ValidateClearSetPair(api, "--max-concurrent-runs", maxConcurrentRuns is not null, "--clear-max-concurrent-runs", clearMaxConcurrentRuns))
                    return 1;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;

                var agent = await ResolveAgentAsync(api, resolvedProjectId, nameOrId!);
                if (agent is null)
                    return 1;

                string? resolvedInstructions = null;
                if (!string.IsNullOrWhiteSpace(instructions) || instructionsStdin)
                {
                    resolvedInstructions = await ResolveInstructionsAsync(instructions, instructionsStdin, api);
                    if (resolvedInstructions is null)
                        return 1;
                }

                var config = await ResolveJsonAsync(agentConfig, api);
                if (config is ResolveJsonResult.Invalid)
                    return 1;

                var body = new JsonObject();
                AddIfProvided(body, "name", name);
                AddIfProvided(body, "description", clearDescription ? null : description, clearDescription || description is not null);
                AddIfProvided(body, "instructions", resolvedInstructions);
                AddIfProvided(body, "agentConfig", clearAgentConfig ? null : ((ResolveJsonResult.Valid)config).Value, clearAgentConfig || agentConfig is not null);
                AddIfProvided(body, "skills", clearSkills ? null : JsonSerializer.SerializeToNode(ParseSkills(skills), JsonOptions), clearSkills || skills is not null);
                AddIfProvided(body, "maxConcurrentRuns", clearMaxConcurrentRuns ? null : maxConcurrentRuns, clearMaxConcurrentRuns || maxConcurrentRuns is not null);

                return await api.PrintPatchAsync(ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}"), body);
            }
        });
        return cmd;
    }

    private static bool ValidateClearSetPair(MohistCliApi api, string setFlag, bool setProvided, string clearFlag, bool clearProvided)
    {
        if (!setProvided || !clearProvided) return true;
        api.Error.WriteLine($"{setFlag} cannot be used with {clearFlag}");
        return false;
    }

    private static void AddIfProvided(JsonObject body, string property, string? value, bool provided = true)
    {
        if (provided) body[property] = value;
    }

    private static void AddIfProvided(JsonObject body, string property, int? value, bool provided)
    {
        if (provided) body[property] = value;
    }

    private static void AddIfProvided(JsonObject body, string property, JsonNode? value, bool provided)
    {
        if (provided) body[property] = value;
    }

    private static Command BuildArchive(MohistCliApi api)
    {
        var cmd = new Command("archive", "Archive an agent");
        cmd.Aliases.Add("delete");
        var nameOrIdArg = NameOrIdArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();

        cmd.Arguments.Add(nameOrIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var nameOrId = ctx.GetValue(nameOrIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return ArchiveAsync();

            async Task<int> ArchiveAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);

                if (resolveExit != 0) return resolveExit;

                var agent = await ResolveAgentAsync(api, resolvedProjectId, nameOrId!);
                if (agent is null)
                    return 1;

                var archived = await DeleteAgentAsync(api, ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}"));
                if (archived is null)
                    return 1;
                api.Output.WriteLine($"Agent {archived.Name} ({archived.Id}) archived");
                return 0;
            }
        });
        return cmd;
    }

    private static Command BuildSession(MohistCliApi api)
    {
        var session = new Command(
            "session",
            "Manage a generic AgentSession launched from an Agent profile. Subcommands: list <agent>, show <sessionId>, transcript <sessionId>, launch <agent>, compact <sessionId>, reset <sessionId>, followup <sessionId>, cancel <sessionId>.");

        session.Subcommands.Add(BuildSessionList(api));
        session.Subcommands.Add(BuildSessionShow(api));
        session.Subcommands.Add(BuildSessionTranscript(api));
        session.Subcommands.Add(BuildSessionLaunch(api));
        session.Subcommands.Add(BuildSessionCompact(api));
        session.Subcommands.Add(BuildSessionReset(api));
        session.Subcommands.Add(BuildSessionFollowup(api));
        session.Subcommands.Add(BuildSessionCancel(api));

        return session;
    }

    private static Command BuildSessionLaunch(MohistCliApi api)
    {
        var cmd = new Command(
            "launch",
            "Launch a generic AgentSession from an Agent profile. Sends POST /api/projects/:projectId/agents/:agentId/sessions.");
        var agentRefArg = new Argument<string>("agent") { Description = "Agent name or id (resolves project-scoped)" };
        var promptOpt = new Option<string?>("--prompt") { Description = "Prompt text (mutually exclusive with --prompt-file and --prompt-stdin)" };
        var promptFileOpt = new Option<string?>("--prompt-file") { Description = "Read prompt from a UTF-8 file path (recommended for long prompts; mutually exclusive with --prompt and --prompt-stdin)" };
        var promptStdinOpt = new Option<bool>("--prompt-stdin") { Description = "Read prompt from stdin (mutually exclusive with --prompt and --prompt-file)" };
        var issueRefOpt = new Option<int?>("--issue") { Description = "Optional context reference: record the issue number on the session metadata" };
        var epicRefOpt = new Option<string?>("--epic") { Description = "Optional context reference: record the epic number on the session metadata" };
        var repositoryRefOpt = new Option<string?>("--repository") { Description = "Optional context reference: record the repository on the session metadata" };
        var workspacePathOpt = new Option<string?>("--workspace-path") { Description = "Optional context reference: record the workspace path on the session metadata" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(agentRefArg);
        cmd.Options.Add(promptOpt);
        cmd.Options.Add(promptFileOpt);
        cmd.Options.Add(promptStdinOpt);
        cmd.Options.Add(issueRefOpt);
        cmd.Options.Add(epicRefOpt);
        cmd.Options.Add(repositoryRefOpt);
        cmd.Options.Add(workspacePathOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var agentRef = ctx.GetValue(agentRefArg);
            var prompt = ctx.GetValue(promptOpt);
            var promptFile = ctx.GetValue(promptFileOpt);
            var promptStdin = ctx.GetValue(promptStdinOpt);
            var issueRef = ctx.GetValue(issueRefOpt);
            var epicRef = ctx.GetValue(epicRefOpt);
            var repositoryRef = ctx.GetValue(repositoryRefOpt);
            var workspacePath = ctx.GetValue(workspacePathOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return LaunchAsync();

            async Task<int> LaunchAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;

                var resolvedPrompt = await BodyInputResolver.ResolveAsync(
                    prompt, promptFile, promptStdin,
                    new BodyInputResolver.SourceFlags("--prompt", "--prompt-file", "--prompt-stdin", "prompt"),
                    api.FileSystem, api.StandardInput, api.Error);
                if (resolvedPrompt is BodyInputResolver.Result.Failure)
                    return 1;
                var promptText = ((BodyInputResolver.Result.Success)resolvedPrompt).Body;

                var contextRefs = BuildLaunchContext(issueRef, epicRef, repositoryRef, workspacePath);
                object body = contextRefs is null
                    ? new { prompt = promptText }
                    : new { prompt = promptText, context = contextRefs };
                return await api.PrintPostWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agentRef!)}/sessions"),
                    body,
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionLaunch),
                    rawJson: true);
            }
        });
        return cmd;
    }

    private static Command BuildSessionCompact(MohistCliApi api) =>
        BuildSessionRecovery(api, "compact", "Compact the session in place");

    private static Command BuildSessionReset(MohistCliApi api) =>
        BuildSessionRecovery(api, "reset", "Reset the session in place");

    private static Command BuildSessionRecovery(MohistCliApi api, string operation, string description)
    {
        var cmd = new Command(operation, description);
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id returned by launch" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return RecoverAsync();

            async Task<int> RecoverAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/{operation}"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionRecovery),
                    headers: new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") });
            }
        });
        return cmd;
    }

    private static Command BuildSessionFollowup(MohistCliApi api)
    {
        var cmd = new Command(
            "followup",
            "Send follow-up text to an AgentSession. It joins an active turn or starts a user-initiated turn when idle without creating a TaskRun or AgentJob. Sends POST /api/projects/:projectId/agent-sessions/:sessionId/followup.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Agent session id returned by launch" };
        var textOpt = new Option<string?>("--text") { Description = "Followup text (mutually exclusive with --text-file and --text-stdin)" };
        var textFileOpt = new Option<string?>("--text-file") { Description = "Read followup text from a UTF-8 file path (recommended for long messages; mutually exclusive with --text and --text-stdin)" };
        var textStdinOpt = new Option<bool>("--text-stdin") { Description = "Read followup text from stdin (mutually exclusive with --text and --text-file)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(textOpt);
        cmd.Options.Add(textFileOpt);
        cmd.Options.Add(textStdinOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var text = ctx.GetValue(textOpt);
            var textFile = ctx.GetValue(textFileOpt);
            var textStdin = ctx.GetValue(textStdinOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return FollowupAsync();

            async Task<int> FollowupAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;

                var resolvedText = await BodyInputResolver.ResolveAsync(
                    text, textFile, textStdin,
                    new BodyInputResolver.SourceFlags("--text", "--text-file", "--text-stdin", "text"),
                    api.FileSystem, api.StandardInput, api.Error);
                if (resolvedText is BodyInputResolver.Result.Failure)
                    return 1;
                var textValue = ((BodyInputResolver.Result.Success)resolvedText).Body;
                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/followup"),
                    new { text = textValue },
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionFollowup),
                    rawJson: true);
            }
        });
        return cmd;
    }

    private static Command BuildSessionCancel(MohistCliApi api)
    {
        var cmd = new Command(
            "cancel",
            "Request cancellation of a running generic AgentSession. Sends POST /api/projects/:projectId/agent-sessions/:sessionId/cancel and prints the resulting session state honestly (cancelled / not-cancellable / terminal-state).");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Agent session id returned by launch" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return CancelAsync();

            async Task<int> CancelAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;
                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/cancel"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionCancel),
                    rawJson: true);
            }
        });
        return cmd;
    }

    private static Command BuildSessionList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List agent sessions for a given Agent profile. Resolves the agent ref client-side, then GETs .../agents/{agentId}/sessions.");
        cmd.Aliases.Add("ls");
        var agentRefArg = new Argument<string>("agent") { Description = "Agent name or id (resolves project-scoped)" };
        var statusOpt = new Option<string?>("--status") { Description = "Filter by session status (running, completed, failed, stopped)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(agentRefArg);
        cmd.Options.Add(statusOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var agentRef = ctx.GetValue(agentRefArg);
            var status = ctx.GetValue(statusOpt);
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

                var agent = await ResolveAgentAsync(api, resolvedProjectId, agentRef!);
                if (agent is null)
                    return 1;

                var query = string.IsNullOrWhiteSpace(status)
                    ? ""
                    : $"?status={Uri.EscapeDataString(status)}";
                return await api.PrintWithOutputAsync(
                    ProjectAgentsPath(resolvedProjectId, $"/agents/{MohistCliCommands.Escape(agent.Id)}/sessions") + query,
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionList));
            }
        });
        return cmd;
    }

    private static Command BuildSessionShow(MohistCliApi api)
    {
        var cmd = new Command(
            "show",
            "Show the summary of a generic AgentSession. GETs .../agent-sessions/{sessionId}.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Agent session id returned by launch" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;
                return await api.PrintWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}"),
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionShow));
            }
        });
        return cmd;
    }

    private static Command BuildSessionTranscript(MohistCliApi api)
    {
        var cmd = new Command(
            "transcript",
            "Show transcript summary of a generic AgentSession. GETs .../agent-sessions/{sessionId}/transcript.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Agent session id returned by launch" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return TranscriptAsync();

            async Task<int> TranscriptAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);

                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);


                if (resolveExit != 0) return resolveExit;
                return await api.PrintWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/transcript"),
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionTranscript));
            }
        });
        return cmd;
    }

    private static object? BuildLaunchContext(int? issue, string? epic, string? repository, string? workspacePath)
    {
        if (issue is null && string.IsNullOrWhiteSpace(epic) && string.IsNullOrWhiteSpace(repository) && string.IsNullOrWhiteSpace(workspacePath))
            return null;

        return new
        {
            issueNumber = issue,
            epicNumber = string.IsNullOrWhiteSpace(epic) ? null : epic,
            repository = string.IsNullOrWhiteSpace(repository) ? null : repository,
            workspacePath = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath,
        };
    }

    private static async Task<string?> ResolveInstructionsAsync(string? instructions, bool instructionsStdin, MohistCliApi api)
    {
        string? inline = null;
        string? file = null;
        var stdin = instructionsStdin;

        if (string.Equals(instructions, "-", StringComparison.Ordinal))
        {
            return await api.StandardInput.ReadToEndAsync();
        }
        else if (instructions?.StartsWith('@') == true)
        {
            instructions = instructions[1..];
            file = instructions;
        }
        else
        {
            inline = instructions;
        }

        var resolved = await BodyInputResolver.ResolveAsync(inline, file, stdin, api.FileSystem, api.StandardInput, api.Error);
        return resolved is BodyInputResolver.Result.Success success ? success.Body : null;
    }

    private static async Task<ResolveJsonResult> ResolveJsonAsync(string? value, MohistCliApi api)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new ResolveJsonResult.Valid(null);

        var text = value;
        if (value.StartsWith('@'))
        {
            try
            {
                text = await api.FileSystem.ReadAllTextAsync(value[1..]);
            }
            catch (Exception ex)
            {
                api.Error.WriteLine($"could not read agent config file: {value[1..]} ({ex.Message})");
                return new ResolveJsonResult.Invalid();
            }
        }

        try
        {
            return new ResolveJsonResult.Valid(JsonNode.Parse(text));
        }
        catch (JsonException ex)
        {
            api.Error.WriteLine($"--agent-config must be valid JSON ({ex.Message})");
            return new ResolveJsonResult.Invalid();
        }
    }

    private static string[]? ParseSkills(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string AgentQuery(bool? all = null, string? status = null)
    {
        var parts = new List<string>();
        if (all is true)
            parts.Add("all=true");
        if (!string.IsNullOrWhiteSpace(status))
            parts.Add($"status={Uri.EscapeDataString(status)}");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
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

    private abstract record ResolveJsonResult
    {
        private ResolveJsonResult() { }

        public sealed record Valid(JsonNode? Value) : ResolveJsonResult;

        public sealed record Invalid : ResolveJsonResult;
    }

    internal sealed record AgentRef(string Id, string Name)
    {
        public static AgentRef? From(JsonNode? node)
        {
            var id = node?["id"]?.GetValue<string>();
            var name = node?["name"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ? null : new AgentRef(id, name);
        }
    }
}
