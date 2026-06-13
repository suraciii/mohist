using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class ProjectRepoCommands
{
    public static Command Build(MohistCliApi api)
    {
        var repo = new Command("repo", "Project repository management");
        repo.Aliases.Add("repository");
        repo.Aliases.Add("repositories");

        repo.Subcommands.Add(BuildList(api));
        repo.Subcommands.Add(BuildAdd(api));
        repo.Subcommands.Add(BuildSetDefault(api));
        repo.Subcommands.Add(BuildRemove(api));

        return repo;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List project repositories");
        cmd.Aliases.Add("ls");
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
            var validation = MohistCliApi.ValidateOutputMode(output);
            if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
            {
                api.Error.WriteLine(invalid.Message);
                return Task.FromResult(1);
            }
            var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/repositories",
                    mode,
                    nameof(MohistCliApi.TableShape.RepoList));
            }
        });
        return cmd;
    }

    private static Command BuildAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a repository to a project");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var nameOpt = new Option<string>("--name", "-n") { Description = "Repository name" };
        var pathOpt = new Option<string?>("--path", "-p") { Description = "Repository path or git URL" };
        var gitUrlOpt = new Option<string?>("--git-url", "-u") { Description = "Git URL" };
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch" };
        var setDefaultOpt = new Option<bool>("--set-default", "-d") { Description = "Set as default repository" };
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(pathOpt);
        cmd.Options.Add(gitUrlOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(setDefaultOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var name = ctx.GetValue(nameOpt);
            var path = ctx.GetValue(pathOpt);
            var gitUrl = ctx.GetValue(gitUrlOpt);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var setDefault = ctx.GetValue(setDefaultOpt);

            if (string.IsNullOrWhiteSpace(name))
            {
                api.Error.WriteLine("--name is required to add a repository");
                return Task.FromResult(1);
            }

            return AddAsync();

            async Task<int> AddAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                var resolvedGitUrl = !string.IsNullOrWhiteSpace(gitUrl) ? gitUrl : path;
                return await api.PrintPostAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/repositories",
                    new
                    {
                        name,
                        path,
                        gitUrl = resolvedGitUrl,
                        baseBranch,
                        setDefault,
                    });
            }
        });
        return cmd;
    }

    private static Command BuildSetDefault(MohistCliApi api)
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
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintPatchAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/repositories/{MohistCliCommands.Escape(name!)}",
                    new { setDefault = true });
            }
        });
        return cmd;
    }

    private static Command BuildRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a project repository");
        cmd.Aliases.Add("delete");
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
                if (string.IsNullOrWhiteSpace(resolvedProjectId))
                    return 1;
                return await api.PrintDeleteAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/repositories/{MohistCliCommands.Escape(name!)}");
            }
        });
        return cmd;
    }
}
