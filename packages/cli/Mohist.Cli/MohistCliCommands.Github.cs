using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class GithubCommands
{
    private static readonly ResourceDescriptor ConnectDescriptor = new(
        ResourceCardinality.Single,
        ["id", "projectId", "owner", "repo", "repositoryName", "feedMode", "approvers", "status", "webhookSecret", "ingressUrl"]);

    public static Command Build(MohistCliApi api)
    {
        var github = new Command("github", "Connect GitHub repositories to projects.");
        github.Subcommands.Add(BuildConnect(api));
        github.Subcommands.Add(BuildUpdate(api));
        return github;
    }

    private static Command BuildConnect(MohistCliApi api)
    {
        var command = new Command("connect", "Connect a GitHub repository to the project and print the webhook configuration for GitHub.");
        var ownerRepo = new Argument<string>("owner/repo") { Description = "GitHub repository coordinates, e.g. octocat/hello-world." };
        var feedMode = new Option<string?>("--feed-mode") { Description = "Feed mode: start (default, intake starts the issue) or backlog (intake only)." };
        var approver = new Option<string[]?>("--approver") { Description = "GitHub login whose PR review counts as approval (repeatable)." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ConnectDescriptor);
        command.Arguments.Add(ownerRepo);
        command.Options.Add(feedMode);
        command.Options.Add(approver);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, ConnectDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(ConnectDescriptor, selection);

            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;

            var coordinates = ctx.GetValue(ownerRepo) ?? string.Empty;
            if (!TryParseOwnerRepo(coordinates, out var owner, out var repo))
            {
                await api.Error.WriteLineAsync($"Invalid 'owner/repo' value '{coordinates}'; expected the form 'owner/repo'.").ConfigureAwait(false);
                return CliExitCode.For(CliExitOutcome.UsageFailure);
            }

            var (data, exit) = await api.ConnectGitHubRepositoryAsync(
                resolution.ProjectId, owner, repo, ctx.GetValue(feedMode), ctx.GetValue(approver)).ConfigureAwait(false);
            if (exit != 0 || data is null) return exit;
            if (selection.Kind == JsonSelectionKind.Selected)
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(
                    selection.Project(data, ConnectDescriptor.Cardinality)).ConfigureAwait(false);

            await PrintChecklistAsync(api, data).ConfigureAwait(false);
            return 0;
        });
        return command;
    }

    private static Command BuildUpdate(MohistCliApi api)
    {
        var command = new Command("update", "Update a GitHub connection's approver list.");
        var id = new Argument<string>("connection-id") { Description = "GitHub connection id (see 'mo github connect --json id')." };
        var approver = new Option<string[]?>("--approver") { Description = "GitHub login whose PR review counts as approval (repeatable; replaces the list)." };
        var noApprovers = new Option<bool>("--no-approvers") { Description = "Clear the approver list (disables PR-review approval)." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ConnectDescriptor);
        command.Arguments.Add(id);
        command.Options.Add(approver);
        command.Options.Add(noApprovers);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, ConnectDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(ConnectDescriptor, selection);

            var approvers = ctx.GetValue(approver);
            var clear = ctx.GetValue(noApprovers);
            if (clear && approvers is { Length: > 0 })
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--no-approvers cannot be combined with --approver.");
            if (!clear && approvers is not { Length: > 0 })
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "Specify --approver or --no-approvers.");

            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;

            var connectionId = ctx.GetValue(id) ?? string.Empty;
            var (data, exit) = await api.UpdateGitHubConnectionApproversAsync(
                resolution.ProjectId, connectionId, approvers, clear).ConfigureAwait(false);
            if (exit != 0 || data is null) return exit;
            if (selection.Kind == JsonSelectionKind.Selected)
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(
                    selection.Project(data, ConnectDescriptor.Cardinality)).ConfigureAwait(false);

            var owner = data["owner"]?.GetValue<string>() ?? string.Empty;
            var repo = data["repo"]?.GetValue<string>() ?? string.Empty;
            var stored = data["approvers"]?.AsArray().Select(a => a?.GetValue<string>()).Where(a => a is not null).ToArray() ?? [];
            await api.Output.WriteLineAsync(
                $"GitHub connection {connectionId} updated: {owner}/{repo} approvers = {(stored.Length == 0 ? "(none)" : string.Join(", ", stored))}").ConfigureAwait(false);
            return 0;
        });
        return command;
    }

    private static async Task PrintChecklistAsync(MohistCliApi api, JsonNode data)
    {
        var owner = data["owner"]?.GetValue<string>() ?? string.Empty;
        var repo = data["repo"]?.GetValue<string>() ?? string.Empty;
        var repositoryName = data["repositoryName"]?.GetValue<string>() ?? string.Empty;
        var feedMode = data["feedMode"]?.GetValue<string>() ?? string.Empty;
        var secret = data["webhookSecret"]?.GetValue<string>() ?? string.Empty;
        var ingressUrl = data["ingressUrl"]?.GetValue<string>() ?? string.Empty;
        var id = data["id"]?.GetValue<string>() ?? string.Empty;

        await api.Output.WriteLineAsync($"GitHub connection {id} created: {owner}/{repo} → repository {repositoryName} (feed mode: {feedMode})").ConfigureAwait(false);
        await api.Output.WriteLineAsync().ConfigureAwait(false);
        await api.Output.WriteLineAsync("In GitHub, add a webhook to the repository:").ConfigureAwait(false);
        await api.Output.WriteLineAsync("  Settings → Webhooks → Add webhook").ConfigureAwait(false);
        await api.Output.WriteLineAsync($"  Payload URL:  {ingressUrl}").ConfigureAwait(false);
        await api.Output.WriteLineAsync("  Content type: application/json").ConfigureAwait(false);
        await api.Output.WriteLineAsync($"  Secret:       {secret}").ConfigureAwait(false);
        await api.Output.WriteLineAsync("  Events:       issues, pull_request_review, check_suite").ConfigureAwait(false);
        await api.Output.WriteLineAsync().ConfigureAwait(false);
        await api.Output.WriteLineAsync("GitHub identity (GitHub App or fine-grained PAT) is not configured yet;").ConfigureAwait(false);
        await api.Output.WriteLineAsync("write-backs and delivery tokens will need it in a later release.").ConfigureAwait(false);
    }

    private static JsonSelection ResolveSelection(
        ParseResult context,
        Option<string?> output,
        ResourceDescriptor descriptor)
    {
        var explicitOutput = MohistCliCommands.OutputOptionState.Explicit;
        var value = context.GetValue(output);
        return JsonSelection.Parse(
            descriptor,
            explicitOutput,
            explicitOutput && string.Equals(value, "table", StringComparison.Ordinal) ? null : value);
    }

    private static bool TryParseOwnerRepo(string coordinates, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(coordinates) || coordinates.Contains(' '))
            return false;
        var parts = coordinates.Split('/');
        if (parts.Length != 2)
            return false;
        owner = parts[0].Trim();
        repo = parts[1].Trim();
        return owner.Length > 0 && repo.Length > 0;
    }
}
