using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class RepositoryCommands
{
    public static Command Build(MohistCliApi api)
    {
        var repository = new Command("repo", "Repository management");
        repository.Aliases.Add("repository");

        repository.Subcommands.Add(BuildList(api));
        repository.Subcommands.Add(BuildAdd(api));
        repository.Subcommands.Add(BuildUpdate(api));
        repository.Subcommands.Add(BuildSetDefault(api));
        repository.Subcommands.Add(BuildDelete(api));

        return repository;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List repositories");
        cmd.Aliases.Add("ls");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            return ListAsync();

            async Task<int> ListAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintWithOutputAsync(
                    ProjectRepositoriesPath(resolvedProjectId),
                    mode,
                    nameof(MohistCliApi.TableShape.RepoList));
            }
        });
        return cmd;
    }

    private static Command BuildAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a repository");
        var nameArg = new Argument<string>("name") { Description = "Repository name" };
        var gitUrlOpt = new Option<string>("--git-url", "-u") { Description = "Git URL" };
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch" };
        var setDefaultOpt = new Option<bool>("--set-default", "-d") { Description = "Set as default repository" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(gitUrlOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(setDefaultOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var gitUrl = ctx.GetValue(gitUrlOpt);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var setDefault = ctx.GetValue(setDefaultOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            if (string.IsNullOrWhiteSpace(gitUrl))
            {
                api.Error.WriteLine("--git-url is required to add a repository");
                return Task.FromResult(1);
            }

            return AddAsync();

            async Task<int> AddAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var payload = new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["gitUrl"] = gitUrl,
                    ["baseBranch"] = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch,
                };
                if (setDefault)
                    payload["setDefault"] = true;
                return await api.PrintPostWithOutputAsync(
                    ProjectRepositoriesPath(resolvedProjectId),
                    payload,
                    mode,
                    nameof(MohistCliApi.TableShape.RepoList));
            }
        });
        return cmd;
    }

    private static Command BuildUpdate(MohistCliApi api)
    {
        var cmd = new Command("update", "Update a repository");
        var nameArg = new Argument<string>("name") { Description = "Repository name" };
        var gitUrlOpt = new Option<string?>("--git-url", "-u") { Description = "Git URL" };
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(gitUrlOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var gitUrl = ctx.GetValue(gitUrlOpt);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            var payload = new Dictionary<string, object?>();
            if (ctx.GetResult(gitUrlOpt) is not null)
                payload["gitUrl"] = gitUrl;
            if (ctx.GetResult(baseBranchOpt) is not null)
                payload["baseBranch"] = baseBranch;

            if (payload.Count == 0)
            {
                api.Error.WriteLine($"Repository '{name}' requires --git-url and/or --base-branch to update");
                return Task.FromResult(1);
            }

            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintPatchWithOutputAsync(
                    $"{ProjectRepositoriesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}",
                    payload,
                    mode,
                    nameof(MohistCliApi.TableShape.RepoList));
            }
        });
        return cmd;
    }

    private static Command BuildSetDefault(MohistCliApi api)
    {
        var cmd = new Command("set-default", "Set a repository as the project default");
        var nameArg = new Argument<string>("name") { Description = "Repository name" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            return SetDefaultAsync();

            async Task<int> SetDefaultAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintPatchWithOutputAsync(
                    $"{ProjectRepositoriesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}",
                    new { setDefault = true },
                    mode,
                    nameof(MohistCliApi.TableShape.RepoList));
            }
        });
        return cmd;
    }

    private static Command BuildDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a repository");
        cmd.Aliases.Add("remove");
        cmd.Aliases.Add("rm");
        var nameArg = new Argument<string>("name") { Description = "Repository name" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var (mode, exit) = api.ResolveOutputMode(output);

            if (exit != 0) return Task.FromResult(exit);

            return DeleteAsync();

            async Task<int> DeleteAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintDeleteWithOutputAsync(
                    $"{ProjectRepositoriesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}",
                    mode,
                    nameof(MohistCliApi.TableShape.RepoList));
            }
        });
        return cmd;
    }

    private static string ProjectRepositoriesPath(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("No active project. Run 'mo project use <id-or-name>' or pass --project.");
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/repositories";
    }
}
