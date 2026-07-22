using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class RoutingCommands
{
    public static Command Build(MohistCliApi api)
    {
        var routing = new Command("routing", "Manage project event routing rules and dry-run evaluation.");
        routing.Subcommands.Add(BuildRules(api));
        routing.Subcommands.Add(BuildTest(api));
        return routing;
    }

    private static Command BuildRules(MohistCliApi api)
    {
        var rule = new Command("rule", "Manage the project's ordered routing rules.");
        rule.Subcommands.Add(BuildCreate(api));
        rule.Subcommands.Add(BuildList(api));
        rule.Subcommands.Add(BuildShow(api));
        rule.Subcommands.Add(BuildUpdate(api));
        rule.Subcommands.Add(BuildArchive(api));
        rule.Subcommands.Add(BuildMove(api));
        return rule;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var command = new Command("create", "Create a routing rule.");
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var name = RequiredOption("--name", "Rule name.");
        var match = RequiredOption("--match", "Event match expression.");
        var agent = RequiredOption("--agent", "Response Agent id or name.");
        var prompt = RequiredOption("--response-prompt", "Response prompt template.");
        var cont = new Option<bool>("--continue") { Description = "Continue evaluating rules after a match." };
        var before = new Option<string?>("--before") { Description = "Insert before this rule." };
        var after = new Option<string?>("--after") { Description = "Insert after this rule." };
        var output = MohistCliCommands.OutputOption();
        AddProjectOptions(command, project, projectId);
        command.Options.Add(name); command.Options.Add(match); command.Options.Add(agent); command.Options.Add(prompt);
        command.Options.Add(cont); command.Options.Add(before); command.Options.Add(after);
        command.Options.Add(output);
        command.SetAction(ctx => ExecuteAsync(ctx));
        return command;

        async Task<int> ExecuteAsync(ParseResult ctx)
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolution.Exit != 0) return resolution.Exit;
            var resolved = resolution.ProjectId;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            if (exit != 0) return exit;
            return await api.PrintPostWithOutputAsync(
                RulesPath(resolved) + PositionQuery(ctx.GetValue(before), ctx.GetValue(after)),
                new
                {
                    name = ctx.GetValue(name), match = ctx.GetValue(match), agentId = ctx.GetValue(agent),
                    responsePrompt = ctx.GetValue(prompt), @continue = ctx.GetValue(cont),
                }, mode, nameof(MohistCliApi.TableShape.RoutingRule));
        }
    }

    private static Command BuildList(MohistCliApi api)
    {
        var command = new Command("list", "List routing rules in table order.");
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption();
        AddProjectOptions(command, project, projectId); command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolution.Exit != 0) return resolution.Exit;
            var resolved = resolution.ProjectId;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0 ? exit : await api.PrintWithOutputAsync(RulesPath(resolved), mode, nameof(MohistCliApi.TableShape.RoutingRuleList));
        });
        return command;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var command = new Command("show", "Show a routing rule.");
        var target = new Argument<string>("rule") { Description = "Rule id or name." };
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption();
        command.Arguments.Add(target); AddProjectOptions(command, project, projectId); command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolution.Exit != 0) return resolution.Exit;
            var resolved = resolution.ProjectId;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0 ? exit : await api.PrintWithOutputAsync(RulePath(resolved, ctx.GetValue(target) ?? ""), mode, nameof(MohistCliApi.TableShape.RoutingRule));
        });
        return command;
    }

    private static Command BuildUpdate(MohistCliApi api)
    {
        var command = new Command("update", "Update a routing rule.");
        var target = new Argument<string>("rule");
        var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var name = new Option<string?>("--name"); var match = new Option<string?>("--match");
        var agent = new Option<string?>("--agent"); var prompt = new Option<string?>("--response-prompt");
        var cont = new Option<bool?>("--continue"); var output = MohistCliCommands.OutputOption();
        command.Arguments.Add(target); AddProjectOptions(command, project, projectId);
        command.Options.Add(name); command.Options.Add(match); command.Options.Add(agent); command.Options.Add(prompt); command.Options.Add(cont); command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId));
            if (resolution.Exit != 0) return resolution.Exit;
            var resolved = resolution.ProjectId;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output)); if (exit != 0) return exit;
            return await api.PrintPatchWithOutputAsync(RulePath(resolved, ctx.GetValue(target) ?? ""), new
            {
                name = ctx.GetValue(name), match = ctx.GetValue(match), agentId = ctx.GetValue(agent),
                responsePrompt = ctx.GetValue(prompt), @continue = ctx.GetValue(cont),
            }, mode, nameof(MohistCliApi.TableShape.RoutingRule));
        });
        return command;
    }

    private static Command BuildArchive(MohistCliApi api)
    {
        var command = new Command("archive", "Archive a routing rule.");
        var target = new Argument<string>("rule"); var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(); command.Arguments.Add(target); AddProjectOptions(command, project, projectId); command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId)); if (resolution.Exit != 0) return resolution.Exit;
            var resolved = resolution.ProjectId;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0 ? exit : await api.PrintPostWithOutputAsync(RulePath(resolved, ctx.GetValue(target) ?? "") + "/archive", new { }, mode, nameof(MohistCliApi.TableShape.RoutingRule));
        });
        return command;
    }

    private static Command BuildMove(MohistCliApi api)
    {
        var command = new Command("move", "Move a routing rule before or after another rule.");
        var target = new Argument<string>("rule"); var (project, projectId) = MohistCliCommands.ProjectRefOption();
        var before = new Option<string?>("--before"); var after = new Option<string?>("--after"); var output = MohistCliCommands.OutputOption();
        command.Arguments.Add(target); AddProjectOptions(command, project, projectId); command.Options.Add(before); command.Options.Add(after); command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId)); if (resolution.Exit != 0) return resolution.Exit;
            var resolved = resolution.ProjectId;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output)); if (exit != 0) return exit;
            return await api.PrintPostWithOutputAsync(RulePath(resolved, ctx.GetValue(target) ?? "") + "/move", new { before = ctx.GetValue(before), after = ctx.GetValue(after) }, mode, nameof(MohistCliApi.TableShape.RoutingRule));
        });
        return command;
    }

    private static Command BuildTest(MohistCliApi api)
    {
        var command = new Command("test", "Dry-run recent project events through the routing table.");
        var (project, projectId) = MohistCliCommands.ProjectRefOption(); var last = new Option<int?>("--last") { Description = "Number of recent events (default: 20)." }; var output = MohistCliCommands.OutputOption("table");
        AddProjectOptions(command, project, projectId); command.Options.Add(last); command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project), ctx.GetValue(projectId)); if (resolution.Exit != 0) return resolution.Exit;
            var resolved = resolution.ProjectId;
            var count = ctx.GetValue(last); var path = $"/api/projects/{Uri.EscapeDataString(resolved)}/routing/test" + (count.HasValue ? $"?last={count.Value}" : "");
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output)); if (exit != 0) return exit;
            var localExit = api.HandleLocalJsonSelection(mode, nameof(MohistCliApi.TableShape.RoutingRule));
            if (localExit is not null) return localExit.Value;
            var (status, data) = await api.GetDataOrPrintErrorAsync(path); if (status != 0 || data is null) return status == 0 ? 1 : status;
            if (mode.StartsWith("json:", StringComparison.Ordinal))
                return await api.WriteSelectedDataAsync(data, mode, nameof(MohistCliApi.TableShape.RoutingRule));
            RenderTrace(api.Output, data); return 0;
        });
        return command;
    }

    private static void RenderTrace(TextWriter output, JsonNode data)
    {
        if (data is JsonObject obj)
        {
            if (obj["message"] is JsonValue message) { output.WriteLine(message.GetValue<string>()); return; }
            if (obj["events"] is JsonArray events) { foreach (var item in events) RenderEvent(output, item); return; }
        }
        if (data is JsonArray array) foreach (var item in array) RenderEvent(output, item);
    }

    private static void RenderEvent(TextWriter output, JsonNode? item)
    {
        if (item is not JsonObject eventObject) return;
        output.WriteLine($"Event {eventObject["eventId"]?.GetValue<string>() ?? eventObject["id"]?.GetValue<string>() ?? "(unknown)"}");
        if (eventObject["outcomes"] is not JsonArray outcomes) return;
        foreach (var outcome in outcomes.OfType<JsonObject>())
            output.WriteLine($"  {outcome["ruleName"]?.GetValue<string>() ?? outcome["ruleId"]?.GetValue<string>() ?? "(rule)"}: {outcome["decision"]?.GetValue<string>() ?? outcome["outcome"]?.GetValue<string>() ?? "not matched"} -> {outcome["agentName"]?.GetValue<string>() ?? outcome["resolvedAgentName"]?.GetValue<string>() ?? "-"}");
    }

    private static Option<string> RequiredOption(string name, string description) => new(name) { Description = description };
    private static void AddProjectOptions(Command command, Option<string?> project, Option<string?> projectId) { command.Options.Add(project); command.Options.Add(projectId); }
    private static string RulesPath(string project) => $"/api/projects/{Uri.EscapeDataString(project)}/routing/rules";
    private static string RulePath(string project, string rule) => $"{RulesPath(project)}/{Uri.EscapeDataString(rule)}";
    private static string PositionQuery(string? before, string? after) => !string.IsNullOrWhiteSpace(before) ? $"?before={MohistCliCommands.Escape(before!)}" : !string.IsNullOrWhiteSpace(after) ? $"?after={MohistCliCommands.Escape(after!)}" : "";
}
