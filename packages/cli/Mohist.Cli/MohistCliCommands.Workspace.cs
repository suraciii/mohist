using System.CommandLine;

namespace Mohist.Cli;

internal static class WorkspaceCommands
{
    public static Command Build(MohistCliApi api)
    {
        var workspace = new Command("workspace", "Workspace management");

        workspace.Subcommands.Add(BuildList(api));
        workspace.Subcommands.Add(BuildView(api));
        workspace.Subcommands.Add(BuildCreate(api));
        workspace.Subcommands.Add(BuildClose(api));
        workspace.Subcommands.Add(BuildRepo(api));

        return workspace;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List workspaces");
        var statusOpt = new Option<string?>("--status") { Description = "Filter by status (active|archived)" };
        var originOpt = new Option<string?>("--origin") { Description = "Filter by origin (issue|slack|web|manual)" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WorkspaceList)));

        cmd.Options.Add(statusOpt);
        cmd.Options.Add(originOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var status = ctx.GetValue(statusOpt);
            var origin = ctx.GetValue(originOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var queryParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(status))
                    queryParts.Add($"status={Uri.EscapeDataString(status)}");
                if (!string.IsNullOrWhiteSpace(origin))
                    queryParts.Add($"origin={Uri.EscapeDataString(origin)}");
                var query = queryParts.Count == 0 ? "" : "?" + string.Join("&", queryParts);

                return await api.PrintWithOutputAsync(
                    $"{WorkspacesPath(resolvedProjectId)}{query}",
                    mode,
                    nameof(MohistCliApi.TableShape.WorkspaceList));
            }
        });
        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command("view", "View a workspace");
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WorkspaceShow)));

        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
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
                    $"{WorkspacesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}",
                    mode,
                    nameof(MohistCliApi.TableShape.WorkspaceShow));
            }
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a workspace");
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        var repoOpt = new Option<string[]?>("--repo")
        {
            Description = "Repository name. Repeat for multiple repos.",
            AllowMultipleArgumentsPerToken = true,
        };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WorkspaceShow)));

        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(repoOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var repos = ctx.GetValue(repoOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var payload = new Dictionary<string, object?>
                {
                    ["name"] = name,
                };
                if (repos is { Length: > 0 })
                    payload["repos"] = repos;

                return await api.PrintPostWithOutputAsync(
                    WorkspacesPath(resolvedProjectId),
                    payload,
                    mode,
                    nameof(MohistCliApi.TableShape.WorkspaceShow));
            }
        });
        return cmd;
    }

    private static Command BuildClose(MohistCliApi api)
    {
        var cmd = new Command("close", "Close (archive) a workspace");
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WorkspaceShow)));

        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return CloseAsync();

            async Task<int> CloseAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                return await api.PrintPostWithOutputAsync(
                    $"{WorkspacesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}/close",
                    body: null,
                    mode,
                    nameof(MohistCliApi.TableShape.WorkspaceShow));
            }
        });
        return cmd;
    }

    private static Command BuildRepo(MohistCliApi api)
    {
        var repo = new Command("repo", "Manage workspace repository membership");
        repo.Subcommands.Add(BuildRepoAdd(api));
        repo.Subcommands.Add(BuildRepoRemove(api));
        return repo;
    }

    private static Command BuildRepoAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a repository to a workspace");
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        var repoArg = new Argument<string>("repo") { Description = "Repository name" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WorkspaceShow)));

        cmd.Arguments.Add(nameArg);
        cmd.Arguments.Add(repoArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var repo = ctx.GetValue(repoArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return AddAsync();

            async Task<int> AddAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                return await api.PrintPostWithOutputAsync(
                    $"{WorkspacesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}/repo",
                    new { repo = repo },
                    mode,
                    nameof(MohistCliApi.TableShape.WorkspaceShow));
            }
        });
        return cmd;
    }

    private static Command BuildRepoRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a repository from a workspace");
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        var repoArg = new Argument<string>("repo") { Description = "Repository name" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.WorkspaceShow)));

        cmd.Arguments.Add(nameArg);
        cmd.Arguments.Add(repoArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var repo = ctx.GetValue(repoArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var path = $"{WorkspacesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}/repo?repo={Uri.EscapeDataString(repo!)}";
                return await api.PrintDeleteWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.WorkspaceShow));
            }
        });
        return cmd;
    }

    private static string WorkspacesPath(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/workspaces";
    }
}
