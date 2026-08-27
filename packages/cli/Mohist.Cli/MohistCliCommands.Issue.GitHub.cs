using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
    private static Command BuildGitHub(MohistCliApi api)
    {
        var github = new Command("github", "Manage the GitHub mirror for an Issue.");
        github.Subcommands.Add(BuildGitHubSync(api));
        github.Subcommands.Add(BuildGitHubLink(api));
        github.Subcommands.Add(BuildGitHubUnlink(api));
        return github;
    }

    private static Command BuildGitHubSync(MohistCliApi api) =>
        BuildGitHubMutation("sync", "Reconcile the GitHub mirror", api, body: new { });

    private static Command BuildGitHubLink(MohistCliApi api)
    {
        var command = new Command("link", "Pair an existing GitHub Issue with a Mohist Issue.");
        var number = NumberArg();
        var githubIssue = new Argument<string>("owner/repo#number")
        {
            Description = "Existing GitHub Issue coordinates, for example octocat/hello-world#42.",
        };
        var project = MohistCliCommands.ProjectRefOption();
        var json = MohistCliCommands.JsonSelectionOption(IssueViewDescriptor);
        command.Arguments.Add(number);
        command.Arguments.Add(githubIssue);
        command.Options.Add(project);
        command.Options.Add(json);
        command.SetAction(ctx => ExecuteAsync(ctx));

        async Task<int> ExecuteAsync(ParseResult ctx)
        {
            var selection = JsonSelection.Parse(IssueViewDescriptor, ctx.GetResult(json) is not null, ctx.GetValue(json));
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(IssueViewDescriptor, selection);
            var coordinates = ctx.GetValue(githubIssue) ?? string.Empty;
            if (!TryParseGitHubIssue(coordinates, out var repository, out var githubNumber))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "<owner/repo#number> must use the form owner/repo#number.");
            var (projectId, exit) = await api.ResolveProject(ctx.GetValue(project));
            if (exit != 0) return exit;
            return await api.PrintMutationResourceAsync(
                HttpMethod.Post,
                ProjectIssuesPath(projectId, $"/issues/{MohistCliCommands.Escape(ctx.GetValue(number)!)}/github/link"),
                new { repository, number = githubNumber },
                IssueViewDescriptor,
                selection,
                data => api.RenderTableAsync(data, MohistCliApi.TableShape.Issue));
        }

        return command;
    }

    private static Command BuildGitHubUnlink(MohistCliApi api) =>
        BuildGitHubMutation("unlink", "Stop synchronizing an Issue's GitHub mirror", api, body: new { });

    private static Command BuildGitHubMutation(string name, string description, MohistCliApi api, object body)
    {
        var command = new Command(name, description);
        var number = NumberArg();
        var project = MohistCliCommands.ProjectRefOption();
        var json = MohistCliCommands.JsonSelectionOption(IssueViewDescriptor);
        command.Arguments.Add(number);
        command.Options.Add(project);
        command.Options.Add(json);
        command.SetAction(ctx => ExecuteAsync(ctx));

        async Task<int> ExecuteAsync(ParseResult ctx)
        {
            var selection = JsonSelection.Parse(IssueViewDescriptor, ctx.GetResult(json) is not null, ctx.GetValue(json));
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(IssueViewDescriptor, selection);
            var (projectId, exit) = await api.ResolveProject(ctx.GetValue(project));
            if (exit != 0) return exit;
            return await api.PrintMutationResourceAsync(
                HttpMethod.Post,
                ProjectIssuesPath(projectId, $"/issues/{MohistCliCommands.Escape(ctx.GetValue(number)!)}/github/{name}"),
                body,
                IssueViewDescriptor,
                selection,
                data => api.RenderTableAsync(data, MohistCliApi.TableShape.Issue));
        }

        return command;
    }

    private static bool TryParseGitHubIssue(string value, out string repository, out int number)
    {
        repository = string.Empty;
        number = 0;
        var marker = value.LastIndexOf('#');
        if (marker <= 0 || marker == value.Length - 1)
            return false;
        repository = value[..marker];
        if (!int.TryParse(value[(marker + 1)..], out number) || number <= 0)
            return false;
        var slash = repository.IndexOf('/');
        return slash > 0 && slash < repository.Length - 1 && repository.IndexOf('/', slash + 1) < 0;
    }
}
