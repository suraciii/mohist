using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class GithubCommands
{
    private static readonly ResourceDescriptor ConnectionDescriptor = new(
        ResourceCardinality.Single,
        ["id", "projectId", "owner", "repo", "repositoryName", "approvers", "status", "installationId", "repositoryNodeId", "reconnectRequired", "needsAttention", "needsReprojection", "lastError", "webhookSecret", "ingressUrl", "createdAt", "updatedAt"]);

    private static readonly ResourceDescriptor ConnectionListDescriptor = new(
        ResourceCardinality.Collection,
        ["id", "projectId", "owner", "repo", "repositoryName", "approvers", "status", "installationId", "repositoryNodeId", "reconnectRequired", "needsAttention", "needsReprojection", "lastError", "createdAt", "updatedAt"]);

    public static Command Build(MohistCliApi api)
    {
        var github = new Command("github", "Connect GitHub repositories to projects.");
        github.Subcommands.Add(BuildConnect(api));
        github.Subcommands.Add(BuildList(api));
        github.Subcommands.Add(BuildView(api));
        github.Subcommands.Add(BuildUpdate(api));
        github.Subcommands.Add(BuildStatus(api, "enable"));
        github.Subcommands.Add(BuildStatus(api, "disable"));
        return github;
    }

    private static Command BuildConnect(MohistCliApi api)
    {
        var command = new Command("connect", "Connect a GitHub repository through the Mohist GitHub App.");
        var ownerRepo = new Argument<string>("owner/repo") { Description = "GitHub repository coordinates, e.g. octocat/hello-world." };
        var approver = new Option<string[]>("--approver") { Description = "GitHub login whose PR review counts as approval (repeatable)." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ConnectionDescriptor);
        command.Arguments.Add(ownerRepo);
        command.Options.Add(approver);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, ConnectionDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(ConnectionDescriptor, selection);
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            if (!TryParseOwnerRepo(ctx.GetValue(ownerRepo) ?? string.Empty, out var owner, out var repo))
            {
                await api.Error.WriteLineAsync("Invalid 'owner/repo' value; expected the form 'owner/repo'.").ConfigureAwait(false);
                return CliExitCode.For(CliExitOutcome.UsageFailure);
            }
            var (data, exit) = await api.ConnectGitHubRepositoryAsync(
                resolution.ProjectId, owner, repo, ctx.GetValue(approver)).ConfigureAwait(false);
            if (exit != 0 || data is null) return exit;
            if (selection.Kind == JsonSelectionKind.Selected)
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(selection.Project(data, ConnectionDescriptor.Cardinality)).ConfigureAwait(false);
            await PrintChecklistAsync(api, data).ConfigureAwait(false);
            return 0;
        });
        return command;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var command = new Command("list", "List GitHub repository connections.");
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ConnectionListDescriptor);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, ConnectionListDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(ConnectionListDescriptor, selection);
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            return await api.PrintWithOutputAsync(
                $"/api/projects/{Uri.EscapeDataString(resolution.ProjectId)}/github-connections",
                api.ResolveOutputMode(ctx.GetValue(output)).Mode,
                nameof(MohistCliApi.TableShape.GitHubConnectionList));
        });
        return command;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var command = new Command("view", "View a GitHub repository connection.");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ConnectionDescriptor);
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, ConnectionDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(ConnectionDescriptor, selection);
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            return await api.PrintWithOutputAsync(
                $"/api/projects/{Uri.EscapeDataString(resolution.ProjectId)}/github-connections/{Uri.EscapeDataString(ctx.GetValue(id) ?? string.Empty)}",
                api.ResolveOutputMode(ctx.GetValue(output)).Mode,
                nameof(MohistCliApi.TableShape.GitHubConnection));
        });
        return command;
    }

    private static Command BuildUpdate(MohistCliApi api)
    {
        var command = new Command("update", "Update a GitHub connection's approver list.");
        var id = new Argument<string>("connection-id");
        var approver = new Option<string[]?>("--approver") { Description = "GitHub login whose PR review counts as approval (repeatable; replaces the list)." };
        var noApprovers = new Option<bool>("--no-approvers") { Description = "Clear the approver list." };
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ConnectionDescriptor);
        command.Arguments.Add(id);
        command.Options.Add(approver);
        command.Options.Add(noApprovers);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, ConnectionDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(ConnectionDescriptor, selection);
            var approvers = ctx.GetValue(approver);
            var clear = ctx.GetValue(noApprovers);
            if (clear && approvers is { Length: > 0 })
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--no-approvers cannot be combined with --approver.");
            if (!clear && approvers is not { Length: > 0 })
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "Specify --approver or --no-approvers.");
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            var (data, exit) = await api.UpdateGitHubConnectionApproversAsync(
                resolution.ProjectId, ctx.GetValue(id) ?? string.Empty, approvers, clear).ConfigureAwait(false);
            if (exit != 0 || data is null) return exit;
            if (selection.Kind == JsonSelectionKind.Selected)
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(selection.Project(data, ConnectionDescriptor.Cardinality)).ConfigureAwait(false);
            await api.Output.WriteLineAsync($"GitHub connection {ctx.GetValue(id)} updated.").ConfigureAwait(false);
            return 0;
        });
        return command;
    }

    private static Command BuildStatus(MohistCliApi api, string operation)
    {
        var command = new Command(operation, $"{operation} a GitHub connection.");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(ConnectionDescriptor);
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, ConnectionDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(ConnectionDescriptor, selection);
            var resolution = await api.ResolveProject(ctx.GetValue(project));
            if (resolution.Exit != 0) return resolution.Exit;
            return await api.PrintPostWithOutputAsync(
                $"/api/projects/{Uri.EscapeDataString(resolution.ProjectId)}/github-connections/{Uri.EscapeDataString(ctx.GetValue(id) ?? string.Empty)}/{operation}",
                new { },
                api.ResolveOutputMode(ctx.GetValue(output)).Mode,
                nameof(MohistCliApi.TableShape.GitHubConnection));
        });
        return command;
    }

    private static async Task PrintChecklistAsync(MohistCliApi api, JsonNode data)
    {
        var owner = data["owner"]?.GetValue<string>() ?? string.Empty;
        var repo = data["repo"]?.GetValue<string>() ?? string.Empty;
        var repositoryName = data["repositoryName"]?.GetValue<string>() ?? string.Empty;
        var secret = data["webhookSecret"]?.GetValue<string>() ?? string.Empty;
        var ingressUrl = data["ingressUrl"]?.GetValue<string>() ?? string.Empty;
        var id = data["id"]?.GetValue<string>() ?? string.Empty;
        await api.Output.WriteLineAsync($"GitHub connection {id} created: {owner}/{repo} → repository {repositoryName}").ConfigureAwait(false);
        await api.Output.WriteLineAsync("GitHub App installation verified.").ConfigureAwait(false);
        await api.Output.WriteLineAsync("Add a Repository webhook in GitHub:").ConfigureAwait(false);
        await api.Output.WriteLineAsync($"  Payload URL:  {ingressUrl}").ConfigureAwait(false);
        await api.Output.WriteLineAsync("  Content type: application/json").ConfigureAwait(false);
        await api.Output.WriteLineAsync($"  Secret:       {secret}").ConfigureAwait(false);
        await api.Output.WriteLineAsync("  Events:       issues, issue_comment, pull_request_review, check_suite").ConfigureAwait(false);
    }

    private static JsonSelection ResolveSelection(ParseResult context, Option<string?> output, ResourceDescriptor descriptor)
    {
        var explicitOutput = MohistCliCommands.OutputOptionState.Explicit;
        var value = context.GetValue(output);
        return JsonSelection.Parse(descriptor, explicitOutput, explicitOutput && string.Equals(value, "table", StringComparison.Ordinal) ? null : value);
    }

    private static bool TryParseOwnerRepo(string coordinates, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(coordinates) || coordinates.Contains(' ')) return false;
        var parts = coordinates.Split('/');
        if (parts.Length != 2) return false;
        owner = parts[0].Trim();
        repo = parts[1].Trim();
        return owner.Length > 0 && repo.Length > 0;
    }
}
