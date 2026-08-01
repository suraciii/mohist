using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class WebhookCommands
{
    public static Command Build(MohistCliApi api)
    {
        var webhook = new Command("webhook", "Manage project outbound webhooks.");
        webhook.Subcommands.Add(BuildSubscriptions(api));
        webhook.Subcommands.Add(BuildEventTypes(api));
        return webhook;
    }

    private static Command BuildEventTypes(MohistCliApi api)
    {
        var command = new Command("event-types", "List the event types available for webhook subscriptions.");
        var project = MohistCliCommands.ProjectRefOption();
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            return await api.ListWebhookEventTypesAsync(resolution.ProjectId, "json");
        });
        return command;
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
        var targetUrl = RequiredOption("--target-url", "HTTP or HTTPS delivery target URL.");
        var evt = EventOption("--event", "Event type to deliver (repeatable). When set, only listed events are delivered; otherwise all events.");
        var match = new Option<string?>("--match") { Description = "Advanced CEL filter applied in addition to selected events." };
        var authType = new Option<string?>("--auth-type") { Description = "Endpoint authentication: none | bearer | basic | custom. Default: none." };
        var authToken = new Option<string?>("--auth-token") { Description = "Bearer token (with --auth-type bearer)." };
        var authUser = new Option<string?>("--auth-user") { Description = "Basic-auth username (with --auth-type basic)." };
        var authPassword = new Option<string?>("--auth-password") { Description = "Basic-auth password (with --auth-type basic)." };
        var authHeader = HeaderOption("--auth-header", "Custom header 'Name=Value' (repeatable, with --auth-type custom).");
        var secret = new Option<string?>("--secret") { Description = "Legacy HMAC signing secret. Prefer --auth-* for endpoint authentication." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(name);
        command.Options.Add(targetUrl);
        command.Options.Add(evt);
        command.Options.Add(match);
        command.Options.Add(authType);
        command.Options.Add(authToken);
        command.Options.Add(authUser);
        command.Options.Add(authPassword);
        command.Options.Add(authHeader);
        command.Options.Add(secret);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(output));
            if (exit != 0) return exit;

            var events = ctx.GetValue(evt) ?? Array.Empty<string>();
            var (selectionMode, eventTypes) = EventSelectionFrom(events);
            var type = ctx.GetValue(authType);
            var token = ctx.GetValue(authToken);
            var user = ctx.GetValue(authUser);
            var pass = ctx.GetValue(authPassword);
            var headers = ParseHeaders(ctx.GetValue(authHeader));
            (string, string)? basic = (type == "basic" && user is not null)
                ? (user, pass ?? string.Empty)
                : null;

            return await api.CreateWebhookSubscriptionAsync(
                resolution.ProjectId,
                ctx.GetValue(name)!,
                ctx.GetValue(match),
                ctx.GetValue(targetUrl)!,
                selectionMode,
                eventTypes,
                type,
                token,
                basic,
                headers,
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
        var targetUrl = new Option<string?>("--target-url") { Description = "Replacement HTTP or HTTPS delivery target URL." };
        var evt = EventOption("--event", "Replacement event types to deliver (repeatable). Sets event selection to 'selected'." );
        var match = new Option<string?>("--match") { Description = "Replacement advanced CEL filter." };
        var authType = new Option<string?>("--auth-type") { Description = "Replacement endpoint authentication: none | bearer | basic | custom." };
        var authToken = new Option<string?>("--auth-token") { Description = "Replacement bearer token (with --auth-type bearer)." };
        var authUser = new Option<string?>("--auth-user") { Description = "Replacement basic-auth username (with --auth-type basic)." };
        var authPassword = new Option<string?>("--auth-password") { Description = "Replacement basic-auth password (with --auth-type basic)." };
        var authHeader = HeaderOption("--auth-header", "Replacement custom header 'Name=Value' (repeatable, with --auth-type custom).");
        var name = new Option<string?>("--name") { Description = "Replacement subscription name." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = OutputOption(MohistCliApi.TableShape.WebhookSubscription);
        command.Arguments.Add(subscriptionId);
        command.Options.Add(targetUrl);
        command.Options.Add(evt);
        command.Options.Add(match);
        command.Options.Add(authType);
        command.Options.Add(authToken);
        command.Options.Add(authUser);
        command.Options.Add(authPassword);
        command.Options.Add(authHeader);
        command.Options.Add(name);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var updates = new JsonObject();
            if (ctx.GetValue(name) is { } replacementName) updates["name"] = replacementName;
            if (ctx.GetValue(match) is { } replacementMatch) updates["match"] = replacementMatch;
            if (ctx.GetValue(targetUrl) is { } replacementTargetUrl) updates["targetUrl"] = replacementTargetUrl;
            if (ctx.GetValue(evt) is { } events && events.Length > 0)
            {
                updates["eventSelectionMode"] = "selected";
                var arr = new JsonArray();
                foreach (var e in events) arr.Add(e);
                updates["eventTypes"] = arr;
            }
            if (ctx.GetValue(authType) is { } type)
            {
                updates["authType"] = type;
                if (type == "bearer" && ctx.GetValue(authToken) is { } token) updates["authToken"] = token;
                if (type == "basic" && ctx.GetValue(authUser) is { } user)
                {
                    updates["authBasic"] = new JsonObject { ["user"] = user, ["password"] = ctx.GetValue(authPassword) ?? string.Empty };
                }
                if (type == "custom")
                {
                    var headersObj = new JsonObject();
                    foreach (var (hn, hv) in ParseHeaders(ctx.GetValue(authHeader)) ?? new Dictionary<string, string>()) headersObj[hn] = hv;
                    updates["authHeaders"] = headersObj;
                }
            }
            if (updates.Count == 0)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "At least one update option is required (--name/--target-url/--event/--match/--auth-*).");

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
        var command = new Command("rotate-secret", "Rotate a webhook subscription legacy signing secret.");
        var subscriptionId = SubscriptionIdArgument();
        var secret = RequiredOption("--secret", "Replacement legacy shared secret used to sign deliveries.");
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

    private static (string mode, IReadOnlyList<string> types) EventSelectionFrom(string[] events)
    {
        var list = events.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? ("all", Array.Empty<string>()) : ("selected", list);
    }

    private static IReadOnlyDictionary<string, string>? ParseHeaders(string[]? headers)
    {
        if (headers is null || headers.Length == 0) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in headers)
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            dict[raw[..eq].Trim()] = raw[(eq + 1)..];
        }
        return dict.Count == 0 ? null : dict;
    }

    private static Argument<string> SubscriptionIdArgument() =>
        new("subscription-id") { Description = "Webhook subscription id." };

    private static Option<string> RequiredOption(string name, string description) =>
        new(name) { Description = description };

    private static Option<string[]?> EventOption(string name, string description) =>
        new(name) { Description = description, AllowMultipleArgumentsPerToken = true };

    private static Option<string[]?> HeaderOption(string name, string description) =>
        new(name) { Description = description, AllowMultipleArgumentsPerToken = true };

    private static Option<string?> OutputOption(MohistCliApi.TableShape shape) =>
        MohistCliCommands.OutputOption(ResourceOutputCatalog.For(shape.ToString()));
}
