using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static Command BuildSubscriptions(MohistCliApi api)
    {
        var group = new Command("subscription", "Manage an Agent's event subscriptions.");
        group.Subcommands.Add(BuildSubscriptionList(api));
        group.Subcommands.Add(BuildSubscriptionCreate(api));
        group.Subcommands.Add(BuildSubscriptionEdit(api));
        group.Subcommands.Add(BuildSubscriptionDelete(api));
        return group;
    }

    private static Command BuildSubscriptionList(MohistCliApi api)
    {
        var command = new Command("list", "List an Agent's subscriptions and availability state.");
        var target = NameOrIdArg();
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentSubscriptionList)));
        command.Arguments.Add(target);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async context =>
        {
            var resolution = await api.ResolveProject(context.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var agent = await ResolveAgentAsync(api, resolution.ProjectId, context.GetValue(target) ?? string.Empty);
            if (agent is null) return 1;
            var (mode, exit) = api.ResolveOutputMode(context.GetValue(output));
            return exit != 0 ? exit : await api.PrintWithOutputAsync(
                SubscriptionsPath(resolution.ProjectId, agent.Id), mode,
                nameof(MohistCliApi.TableShape.AgentSubscriptionList));
        });
        return command;
    }

    private static Command BuildSubscriptionCreate(MohistCliApi api)
    {
        var command = new Command("create", "Create an Agent subscription.");
        var target = NameOrIdArg();
        var project = MohistCliCommands.ProjectRefOption();
        var name = new Option<string>("--name") { Description = "Subscription name." };
        var match = new Option<string>("--match") { Description = "Event match expression." };
        var prompt = new Option<string>("--response-prompt") { Description = "Response prompt template." };
        var continueOption = new Option<bool>("--continue") { Description = "Continue evaluating later routing rules." };
        var idempotency = new Option<string?>("--idempotency-key") { Description = "Stable key for safe retries." };
        var output = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentSubscription)));
        command.Arguments.Add(target);
        command.Options.Add(project); command.Options.Add(name); command.Options.Add(match); command.Options.Add(prompt);
        command.Options.Add(continueOption); command.Options.Add(idempotency); command.Options.Add(output);
        command.SetAction(async context =>
        {
            var nameValue = context.GetValue(name);
            if (string.IsNullOrWhiteSpace(nameValue))
                return CommandHelpHook.RenderUsageFailure(context, api.Error, "--name is required.");
            var matchValue = context.GetValue(match);
            if (string.IsNullOrWhiteSpace(matchValue))
                return CommandHelpHook.RenderUsageFailure(context, api.Error, "--match is required.");
            var promptValue = context.GetValue(prompt);
            if (string.IsNullOrWhiteSpace(promptValue))
                return CommandHelpHook.RenderUsageFailure(context, api.Error, "--response-prompt is required.");

            var resolution = await api.ResolveProject(context.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var agent = await ResolveAgentAsync(api, resolution.ProjectId, context.GetValue(target) ?? string.Empty);
            if (agent is null) return 1;
            var (mode, exit) = api.ResolveOutputMode(context.GetValue(output));
            if (exit != 0) return exit;
            var suppliedKey = context.GetValue(idempotency);
            var key = string.IsNullOrWhiteSpace(suppliedKey)
                ? Guid.NewGuid().ToString("N")
                : suppliedKey;
            if (string.IsNullOrWhiteSpace(suppliedKey))
            {
                if (mode == "table")
                    api.Output.WriteLine($"Idempotency-Key: {key}");
                else
                {
                    api.Error.WriteLine($"Idempotency-Key: {key}");
                    api.Error.WriteLine($"If the outcome is unknown, retry with --idempotency-key {key}.");
                }
            }

            return await api.PrintPostWithOutputAsync(
                SubscriptionsPath(resolution.ProjectId, agent.Id),
                new
                {
                    name = nameValue,
                    match = matchValue,
                    responsePrompt = promptValue,
                    @continue = context.GetValue(continueOption),
                },
                mode,
                nameof(MohistCliApi.TableShape.AgentSubscription),
                headers: new Dictionary<string, string> { ["Idempotency-Key"] = key },
                retries: 1);
        });
        return command;
    }

    private static Command BuildSubscriptionEdit(MohistCliApi api)
    {
        var command = new Command("edit", "Update an Agent subscription.");
        var target = new Argument<string>("subscription-id") { Description = "Subscription id." };
        var agentTarget = NameOrIdArg();
        var project = MohistCliCommands.ProjectRefOption();
        var name = new Option<string?>("--name") { Description = "Replacement subscription name." };
        var match = new Option<string?>("--match") { Description = "Replacement event match expression." };
        var prompt = new Option<string?>("--response-prompt") { Description = "Replacement response prompt." };
        var continueOption = new Option<bool?>("--continue") { Description = "Whether to continue evaluating later rules." };
        var output = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.AgentSubscription)));
        command.Arguments.Add(agentTarget); command.Arguments.Add(target);
        command.Options.Add(project); command.Options.Add(name); command.Options.Add(match); command.Options.Add(prompt);
        command.Options.Add(continueOption); command.Options.Add(output);
        command.SetAction(async context =>
        {
            var resolution = await api.ResolveProject(context.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var agent = await ResolveAgentAsync(api, resolution.ProjectId, context.GetValue(agentTarget) ?? string.Empty);
            if (agent is null) return 1;
            var body = new JsonObject();
            if (context.GetResult(name) is not null) body["name"] = JsonValue.Create(context.GetValue(name));
            if (context.GetResult(match) is not null) body["match"] = JsonValue.Create(context.GetValue(match));
            if (context.GetResult(prompt) is not null) body["responsePrompt"] = JsonValue.Create(context.GetValue(prompt));
            if (context.GetResult(continueOption) is not null) body["continue"] = JsonValue.Create(context.GetValue(continueOption));
            if (body.Count == 0)
            {
                api.Error.WriteLine("At least one editable option is required.");
                return 1;
            }
            var (mode, exit) = api.ResolveOutputMode(context.GetValue(output));
            return exit != 0 ? exit : await api.PrintPatchWithOutputAsync(
                SubscriptionPath(resolution.ProjectId, context.GetValue(target) ?? string.Empty, agent.Id),
                body, mode, nameof(MohistCliApi.TableShape.AgentSubscription));
        });
        return command;
    }

    private static Command BuildSubscriptionDelete(MohistCliApi api)
    {
        var command = new Command("delete", "Delete an Agent subscription.");
        var agentTarget = NameOrIdArg();
        var target = new Argument<string>("subscription-id") { Description = "Subscription id." };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(agentTarget); command.Arguments.Add(target); command.Options.Add(project);
        command.SetAction(async context =>
        {
            var resolution = await api.ResolveProject(context.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var agent = await ResolveAgentAsync(api, resolution.ProjectId, context.GetValue(agentTarget) ?? string.Empty);
            if (agent is null) return 1;
            return await api.PrintDeleteAsync(SubscriptionPath(resolution.ProjectId, context.GetValue(target) ?? string.Empty, agent.Id));
        });
        return command;
    }

    private static string SubscriptionsPath(string projectId, string agentId) =>
        $"/api/projects/{MohistCliCommands.Escape(projectId)}/agents/{MohistCliCommands.Escape(agentId)}/subscriptions";

    private static string SubscriptionPath(string projectId, string subscriptionId, string agentId) =>
        $"{SubscriptionsPath(projectId, agentId)}/{MohistCliCommands.Escape(subscriptionId)}";
}
