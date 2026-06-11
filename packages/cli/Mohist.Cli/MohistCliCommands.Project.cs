using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class ProjectCommands
{
    public static Command Build(MohistCliApi api)
    {
        var project = new Command("project", "Project management");

        project.Subcommands.Add(BuildList(api));
        project.Subcommands.Add(BuildCreate(api));
        project.Subcommands.Add(BuildShow(api));
        project.Subcommands.Add(BuildUse(api));
        project.Subcommands.Add(BuildDelete(api));
        project.Subcommands.Add(BuildRepo(api));

        return project;
    }

    public static Command BuildRepo(MohistCliApi api)
    {
        var repo = new Command("repo", "Manage project repositories");

        repo.Subcommands.Add(BuildRepoList(api));
        repo.Subcommands.Add(BuildRepoAdd(api));
        repo.Subcommands.Add(BuildRepoSetDefault(api));
        repo.Subcommands.Add(BuildRepoRemove(api));

        return repo;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List all projects");
        cmd.Aliases.Add("ls");
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return api.PrintWithOutputAsync(
                "/api/projects",
                mode,
                nameof(MohistCliApi.TableShape.ProjectList));
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new project");
        var nameArg = new Argument<string>("name") { Description = "Project name" };
        var pathOpt = new Option<string?>("--path", "-p") { Description = "Project path" };
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch name" };
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(pathOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var path = ctx.GetValue(pathOpt) ?? Environment.CurrentDirectory;
            var baseBranch = ctx.GetValue(baseBranchOpt);
            return api.PrintPostAsync("/api/projects", new { name, path, baseBranch });
        });
        return cmd;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show project details");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(identifierArg);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            var output = ctx.GetValue(outputOpt);
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return api.PrintWithOutputAsync(
                $"/api/projects/{MohistCliCommands.Escape(identifier!)}",
                mode,
                nameof(MohistCliApi.TableShape.ProjectShow));
        });
        return cmd;
    }

    private static Command BuildUse(MohistCliApi api)
    {
        var cmd = new Command("use", "Set active project");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        cmd.Arguments.Add(identifierArg);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            return api.UseProjectAsync(identifier!);
        });
        return cmd;
    }

    private static Command BuildDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a project");
        cmd.Aliases.Add("remove");
        cmd.Aliases.Add("rm");
        var identifierArg = new Argument<string>("project") { Description = "Project name or ID" };
        cmd.Arguments.Add(identifierArg);
        cmd.SetAction(ctx =>
        {
            var identifier = ctx.GetValue(identifierArg);
            return api.PrintDeleteAsync($"/api/projects/{MohistCliCommands.Escape(identifier!)}");
        });
        return cmd;
    }

    private static string ProjectRepoPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        var suffix = string.IsNullOrEmpty(path) ? "" : (path.StartsWith('/') ? path : "/" + path);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/repositories{suffix}";
    }

    private static Command BuildRepoList(MohistCliApi api)
    {
        var cmd = new Command("list", "List project repositories");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
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
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
                return await api.PrintWithOutputAsync(
                    ProjectRepoPath(resolvedProjectId),
                    mode,
                    nameof(MohistCliApi.TableShape.RepoList));
            }
        });
        return cmd;
    }

    private static Command BuildRepoAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a repository to a project");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var nameOpt = new Option<string?>("--name") { Description = "Repository name" };
        var pathOpt = new Option<string?>("--path") { Description = "Repository path" };
        var remoteOpt = new Option<string?>("--remote") { Description = "Repository remote URL" };
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Repository base branch" };
        var setDefaultOpt = new Option<bool>("--set-default") { Description = "Mark this repository as the project default" };
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(pathOpt);
        cmd.Options.Add(remoteOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(setDefaultOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var name = ctx.GetValue(nameOpt);
            var path = ctx.GetValue(pathOpt);
            var remote = ctx.GetValue(remoteOpt);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var setDefault = ctx.GetValue(setDefaultOpt);
            return AddAsync();

            async Task<int> AddAsync()
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    api.Error.WriteLine("--name is required");
                    return 1;
                }
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                return await api.PrintPostAsync(
                    ProjectRepoPath(resolvedProjectId),
                    new
                    {
                        name,
                        path,
                        remote,
                        baseBranch,
                        setDefault,
                    });
            }
        });
        return cmd;
    }

    private static Command BuildRepoSetDefault(MohistCliApi api)
    {
        var cmd = new Command("set-default", "Set a repository as the project default");
        var nameArg = new Argument<string>("name") { Description = "Repository name" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return SetDefaultAsync();

            async Task<int> SetDefaultAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                return await api.PrintPatchAsync(
                    ProjectRepoPath(resolvedProjectId, $"/{MohistCliCommands.Escape(name!)}"),
                    new { setDefault = true });
            }
        });
        return cmd;
    }

    private static Command BuildRepoRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a repository from a project");
        cmd.Aliases.Add("rm");
        var nameArg = new Argument<string>("name") { Description = "Repository name" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                return await api.PrintDeleteAsync(
                    ProjectRepoPath(resolvedProjectId, $"/{MohistCliCommands.Escape(name!)}"));
            }
        });
        return cmd;
    }
}
