using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Net.Http.Json;
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
        agent.Subcommands.Add(BuildDelete(api));

        return agent;
    }

    private static Argument<string> NameOrIdArg() => new("name-or-id") { Description = "Agent name or id" };

    private static string ProjectAgentsPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}{(path.StartsWith('/') ? path : "/" + path)}";
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

                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

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
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }

                var query = AgentQuery(all ? true : null, status);
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
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
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }

                var agent = await ResolveAgentAsync(api, resolvedProjectId, nameOrId!);
                if (agent is null)
                    return 1;

                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
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

                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

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

    private static Command BuildDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Archive an agent");
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
            return DeleteAsync();

            async Task<int> DeleteAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;

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

    private static async Task<AgentRef?> ResolveAgentAsync(MohistCliApi api, string projectId, string nameOrId)
    {
        try
        {
            if (nameOrId.StartsWith("agent_", StringComparison.Ordinal))
            {
                var data = await api.GetDataAsync(ProjectAgentsPath(projectId, $"/agents/{MohistCliCommands.Escape(nameOrId)}"));
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
            using var response = await api.Http.PostAsJsonAsync(path, body, JsonOptions);
            var data = await ReadDataOrPrintErrorAsync(api, response);
            if (data is null)
                return response.StatusCode == HttpStatusCode.NotFound ? 4 : 1;
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
            using var response = await api.Http.DeleteAsync(path);
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
        if (node is null)
        {
            if (!response.IsSuccessStatusCode)
                api.Error.WriteLine(response.ReasonPhrase ?? "Request failed");
            return response.IsSuccessStatusCode ? null : null;
        }

        var success = node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode;
        if (success)
            return node["data"];

        var error = node["error"]?.GetValue<string>() ?? response.ReasonPhrase ?? "Request failed";
        var code = node["code"]?.GetValue<string>();
        api.Error.WriteLine(code is null ? error : $"{error} ({code})");
        return null;
    }

    private abstract record ResolveJsonResult
    {
        private ResolveJsonResult() { }

        public sealed record Valid(JsonNode? Value) : ResolveJsonResult;

        public sealed record Invalid : ResolveJsonResult;
    }

    private sealed record AgentRef(string Id, string Name)
    {
        public static AgentRef? From(JsonNode? node)
        {
            var id = node?["id"]?.GetValue<string>();
            var name = node?["name"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ? null : new AgentRef(id, name);
        }
    }
}
