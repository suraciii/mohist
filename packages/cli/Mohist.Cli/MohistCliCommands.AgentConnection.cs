using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class AgentConnectionCommands
{
    public static Command Build(MohistCliApi api)
    {
        var group = new Command("connection", "Manage Slack Agent Connections");
        group.Subcommands.Add(BuildCreate(api));
        group.Subcommands.Add(BuildConfigure(api));
        group.Subcommands.Add(BuildClaimOwner(api));
        group.Subcommands.Add(BuildView(api));
        group.Subcommands.Add(BuildList(api));
        group.Subcommands.Add(BuildEdit(api));
        group.Subcommands.Add(BuildDelete(api));
        return group;
    }

    private static string Path(string projectId, string suffix = "") =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/slack-connections{suffix}";

    private static async Task<(string? ProjectId, int Exit)> ProjectAsync(MohistCliApi api, string? project)
    {
        var resolved = await api.ResolveProject(project).ConfigureAwait(false);
        return (resolved.ProjectId, resolved.Exit);
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var command = new Command("create", "Create a Slack Connection");
        var agent = new Argument<string>("agent") { Description = "Agent name or id" };
        var provider = new Option<string>("--provider") { DefaultValueFactory = _ => "slack" };
        var workspace = new Option<string>("--workspace-team-id") { Description = "Slack workspace Team ID" };
        var app = new Option<string>("--app-id") { Description = "Slack App ID" };
        var bot = new Option<string>("--bot-user-id") { Description = "Slack Bot user ID" };
        var botName = new Option<string?>("--bot-name");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(agent);
        command.Options.Add(provider);
        command.Options.Add(workspace);
        command.Options.Add(app);
        command.Options.Add(bot);
        command.Options.Add(botName);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            if (!string.Equals(ctx.GetValue(provider), "slack", StringComparison.OrdinalIgnoreCase))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "Only --provider slack is supported.");
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var agentId = await ResolveAgentIdAsync(api, projectId, ctx.GetValue(agent));
            if (agentId is null) return 1;
            var workspaceId = ctx.GetValue(workspace);
            var appId = ctx.GetValue(app);
            var botId = ctx.GetValue(bot);
            if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(botId))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--workspace-team-id, --app-id, and --bot-user-id are required.");
            var result = await api.PostAndReadAsync(Path(projectId), new
            {
                agentId,
                workspaceTeamId = workspaceId,
                appId,
                botUserId = botId,
                botName = ctx.GetValue(botName),
            });
            return result.ExitCode;
        });
        return command;
    }

    private static Command BuildConfigure(MohistCliApi api)
    {
        var command = new Command("configure", "Store Slack App and Bot credentials");
        var id = new Argument<string>("connection-id");
        var file = new Option<string?>("--credentials-file")
        {
            Description = "UTF-8 JSON file containing exactly appToken and botToken",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(file);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var credentials = await ReadCredentialsAsync(api, ctx.GetValue(file));
            if (credentials is null) return 2;
            var result = await api.PostAndReadAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/configure"), credentials);
            return result.ExitCode;
        });
        return command;
    }

    private static Command BuildClaimOwner(MohistCliApi api)
    {
        var command = new Command("claim-owner", "Generate a one-time Slack owner claim code");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var result = await api.PostAndReadAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/claim-owner"), new { });
            return result.ExitCode;
        });
        return command;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var command = new Command("view", "View a Slack Connection");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            return exit != 0 || projectId is null ? exit : await api.PrintGetAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!) }"));
        });
        return command;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var command = new Command("list", "List Slack Connections");
        var project = MohistCliCommands.ProjectRefOption();
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            return exit != 0 || projectId is null ? exit : await api.PrintGetAsync(Path(projectId));
        });
        return command;
    }

    private static Command BuildEdit(MohistCliApi api)
    {
        var command = new Command("edit", "Edit Slack Connection presentation fields");
        var id = new Argument<string>("connection-id");
        var botName = new Option<string?>("--bot-name");
        var avatar = new Option<string?>("--avatar-hash");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(botName);
        command.Options.Add(avatar);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            if (ctx.GetValue(botName) is null && ctx.GetValue(avatar) is null)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--bot-name or --avatar-hash is required.");
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            return await api.PrintPatchAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!) }"), new
            {
                botName = ctx.GetValue(botName),
                avatarHash = ctx.GetValue(avatar),
            });
        });
        return command;
    }

    private static Command BuildDelete(MohistCliApi api)
    {
        var command = new Command("delete", "Delete a Slack Connection");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            return exit != 0 || projectId is null ? exit : await api.PrintDeleteAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!) }"));
        });
        return command;
    }

    private static async Task<string?> ResolveAgentIdAsync(MohistCliApi api, string projectId, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            api.Error.WriteLine("agent is required");
            return null;
        }
        var agents = await api.GetDataAsync($"/api/projects/{Uri.EscapeDataString(projectId)}/agents?all=true");
        if (agents is JsonArray list)
        {
            foreach (var item in list.OfType<JsonObject>())
            {
                if (string.Equals(item["id"]?.GetValue<string>(), reference, StringComparison.Ordinal)
                    || string.Equals(item["name"]?.GetValue<string>(), reference, StringComparison.Ordinal))
                    return item["id"]?.GetValue<string>();
            }
        }
        api.Error.WriteLine($"Agent '{reference}' not found.");
        return null;
    }

    private static async Task<CredentialPair?> ReadCredentialsAsync(MohistCliApi api, string? path)
    {
        try
        {
            string json;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!IsProtectedFile(api.FileSystem, path))
                    throw new InvalidOperationException("Credential file must be a regular, non-symlink file readable and writable only by the current user.");
                json = await api.FileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            else
            {
                if (!await api.Invocation.RequirePromptAsync("Slack credentials", "--credentials-file <path>", () => Task.FromResult(true)).ConfigureAwait(false))
                    return null;
                await api.Error.WriteLineAsync("App token:").ConfigureAwait(false);
                var appToken = await api.Invocation.Terminal.ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken).ConfigureAwait(false);
                await api.Error.WriteLineAsync("Bot token:").ConfigureAwait(false);
                var botToken = await api.Invocation.Terminal.ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(appToken) || string.IsNullOrWhiteSpace(botToken))
                    throw new InvalidOperationException("Both Slack credentials are required.");
                return new CredentialPair(appToken, botToken);
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("appToken", out var app) || !root.TryGetProperty("botToken", out var bot)
                || app.ValueKind != JsonValueKind.String || bot.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(app.GetString()) || string.IsNullOrWhiteSpace(bot.GetString()))
                throw new InvalidOperationException("Credential file must contain exactly non-empty appToken and botToken strings.");
            return new CredentialPair(app.GetString()!, bot.GetString()!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            api.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private static bool IsProtectedFile(IFileSystem fileSystem, string path)
    {
        if (!fileSystem.Exists(path) || fileSystem.DirectoryExists(path)) return false;
        return !fileSystem.IsSymbolicLink(path) && fileSystem.IsUserOnlyFile(path);
    }

    private sealed record CredentialPair(string AppToken, string BotToken);
}
