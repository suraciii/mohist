using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class RepositoryCommands
{
    public static Command Build(MohistCliApi api)
    {
        var repository = new Command("repository", "Repository management");
        repository.Aliases.Add("repo");

        repository.Subcommands.Add(BuildList(api));
        repository.Subcommands.Add(BuildAdd(api));
        repository.Subcommands.Add(BuildUpdate(api));
        repository.Subcommands.Add(BuildRemove(api));

        return repository;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List repositories");
        cmd.Aliases.Add("ls");
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var projectId = ctx.GetValue(projectIdOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintGetAsync(ProjectRepositoriesPath(resolvedProjectId));
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
        var defaultOpt = new Option<bool>("--default", "-d") { Description = "Set as default repository" };
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(gitUrlOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(defaultOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var gitUrl = ctx.GetValue(gitUrlOpt);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var isDefault = ctx.GetValue(defaultOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            if (string.IsNullOrWhiteSpace(gitUrl))
            {
                api.Error.WriteLine("--git-url is required to add a repository");
                return Task.FromResult(1);
            }
            return AddAsync();

            async Task<int> AddAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintPostAsync(ProjectRepositoriesPath(resolvedProjectId), new
                {
                    name,
                    gitUrl,
                    baseBranch,
                    isDefault,
                });
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
        var newNameOpt = new Option<string?>("--new-name", "-n") { Description = "New repository name" };
        var setDefaultOpt = new Option<bool>("--set-default", "-d") { Description = "Set as default repository" };
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(gitUrlOpt);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(newNameOpt);
        cmd.Options.Add(setDefaultOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var gitUrl = ctx.GetValue(gitUrlOpt);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var newName = ctx.GetValue(newNameOpt);
            var setDefault = ctx.GetValue(setDefaultOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintPatchAsync(
                    $"{ProjectRepositoriesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}",
                    new
                    {
                        newName,
                        gitUrl,
                        baseBranch,
                        setDefault,
                    });
            }
        });
        return cmd;
    }

    private static Command BuildRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a repository");
        cmd.Aliases.Add("delete");
        cmd.Aliases.Add("rm");
        var nameArg = new Argument<string>("name") { Description = "Repository name" };
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameArg);
            var projectId = ctx.GetValue(projectIdOpt);
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintDeleteAsync(
                    $"{ProjectRepositoriesPath(resolvedProjectId)}/{MohistCliCommands.Escape(name!)}");
            }
        });
        return cmd;
    }

    private static string ProjectRepositoriesPath(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("No active project. Run 'mo project use <id-or-name>' or pass --project-id.");
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/repositories";
    }
}
