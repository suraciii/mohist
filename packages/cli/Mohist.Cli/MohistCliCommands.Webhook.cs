using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class WebhookCommands
{
    public static Command Build(MohistCliApi api)
    {
        var webhook = new Command("webhook", "Manage project outbound webhooks.");
        webhook.Subcommands.Add(BuildSubscriptions(api));
        return webhook;
    }

    private static Command BuildSubscriptions(MohistCliApi api)
    {
        var subscription = new Command("subscription", "Manage project webhook subscriptions.");
        subscription.Subcommands.Add(BuildCreate(api));
        subscription.Subcommands.Add(BuildList(api));
        subscription.Subcommands.Add(BuildView(api));
        subscription.Subcommands.Add(BuildEdit(api));
        subscription.Subcommands.Add(BuildEnable(api));
        subscription.Subcommands.Add(BuildDisable(api));
        subscription.Subcommands.Add(BuildDelete(api));
        subscription.Subcommands.Add(BuildRotateSecret(api));
        subscription.Subcommands.Add(BuildFailures(api));
        return subscription;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var command = new Command("create", "Create a webhook subscription.");
        var name = new Argument<string>("name") { Description = "Subscription name." };
        var match = RequiredOption("--match", "Event match expression.");
        var targetUrl = RequiredOption("--target-url", "HTTP or HTTPS delivery target URL.");
        var secret = new Option<string?>("--secret") { Description = "Optional shared secret used to sign deliveries." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(name);
        command.Options.Add(match);
        command.Options.Add(targetUrl);
        command.Options.Add(secret);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.CreateWebhookSubscriptionAsync(
                    resolution.ProjectId,
                    ctx.GetValue(name)!,
                    ctx.GetValue(match)!,
                    ctx.GetValue(targetUrl)!,
                    ctx.GetValue(secret),
                    mode);
        });
        return command;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var command = new Command("list", "List webhook subscriptions.");
        var all = new Option<bool>("--all") { Description = "Include archived subscriptions." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscriptionList);
        command.Options.Add(all);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.ListWebhookSubscriptionsAsync(resolution.ProjectId, mode, ctx.GetValue(all));
        });
        return command;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var command = new Command("view", "Show a webhook subscription.");
        var subscriptionId = SubscriptionIdArgument();
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(subscriptionId);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.ViewWebhookSubscriptionAsync(resolution.ProjectId, ctx.GetValue(subscriptionId)!, mode);
        });
        return command;
    }

    private static Command BuildEdit(MohistCliApi api)
    {
        var command = new Command("edit", "Update a webhook subscription.");
        var subscriptionId = SubscriptionIdArgument();
        var name = new Option<string?>("--name") { Description = "Replacement subscription name." };
        var match = new Option<string?>("--match") { Description = "Replacement event match expression." };
        var targetUrl = new Option<string?>("--target-url") { Description = "Replacement HTTP or HTTPS delivery target URL." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(subscriptionId);
        command.Options.Add(name);
        command.Options.Add(match);
        command.Options.Add(targetUrl);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var updates = new JsonObject();
            if (ctx.GetValue(name) is { } replacementName) updates["name"] = replacementName;
            if (ctx.GetValue(match) is { } replacementMatch) updates["match"] = replacementMatch;
            if (ctx.GetValue(targetUrl) is { } replacementTargetUrl) updates["targetUrl"] = replacementTargetUrl;
            if (updates.Count == 0)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "At least one of --name, --match, or --target-url is required.");

            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.UpdateWebhookSubscriptionAsync(resolution.ProjectId, ctx.GetValue(subscriptionId)!, updates, mode);
        });
        return command;
    }

    private static Command BuildEnable(MohistCliApi api) => BuildStatusCommand(api, "enable", "Enable a webhook subscription.");

    private static Command BuildDisable(MohistCliApi api) => BuildStatusCommand(api, "disable", "Disable a webhook subscription.");

    private static Command BuildArchive(MohistCliApi api) => BuildStatusCommand(api, "archive", "Archive a webhook subscription.");

    private static Command BuildStatusCommand(MohistCliApi api, string action, string description)
    {
        var command = new Command(action, description);
        var subscriptionId = SubscriptionIdArgument();
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(subscriptionId);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.ChangeWebhookSubscriptionStatusAsync(
                    resolution.ProjectId,
                    ctx.GetValue(subscriptionId)!,
                    action,
                    mode);
        });
        return command;
    }

    private static Command BuildDelete(MohistCliApi api)
    {
        var command = new Command("delete", "Archive a webhook subscription.");
        var subscriptionId = SubscriptionIdArgument();
        var yes = new Option<bool>("--yes") { Description = "Confirm archival." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(subscriptionId);
        command.Options.Add(yes);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            if (!ctx.GetValue(yes))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--yes is required to delete a webhook subscription.");
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.ChangeWebhookSubscriptionStatusAsync(resolution.ProjectId, ctx.GetValue(subscriptionId)!, "archive", mode);
        });
        return command;
    }

    private static Command BuildRotateSecret(MohistCliApi api)
    {
        var command = new Command("rotate-secret", "Rotate a webhook subscription shared secret.");
        var subscriptionId = SubscriptionIdArgument();
        var secret = RequiredOption("--secret", "Replacement shared secret used to sign deliveries.");
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(subscriptionId);
        command.Options.Add(secret);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var replacementSecret = ctx.GetValue(secret);
            if (string.IsNullOrEmpty(replacementSecret))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--secret is required.");

            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.RotateWebhookSubscriptionSecretAsync(
                    resolution.ProjectId,
                    ctx.GetValue(subscriptionId)!,
                    replacementSecret,
                    mode);
        });
        return command;
    }

    private static Command BuildFailures(MohistCliApi api)
    {
        var command = new Command("failures", "List webhook delivery failures.");
        var subscriptionId = new Option<string?>("--subscription-id") { Description = "Limit failures to one webhook subscription." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookDeliveryFailureList);
        command.Options.Add(subscriptionId);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            return exit != 0
                ? exit
                : await api.ListWebhookDeliveryFailuresAsync(resolution.ProjectId, mode, ctx.GetValue(subscriptionId));
        });
        return command;
    }

    private static Argument<string> SubscriptionIdArgument() =>
        new("subscription-id") { Description = "Webhook subscription id." };

    private static Option<string> RequiredOption(string name, string description) =>
        new(name) { Description = description };

    private static Option<string?> OutputOption(MohistCliApi.TableShape shape) =>
        MohistCliCommands.OutputOption(ResourceOutputCatalog.For(shape.ToString()));
}
