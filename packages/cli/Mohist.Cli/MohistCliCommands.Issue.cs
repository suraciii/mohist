using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class IssueCommands
{
    public static Command Build(MohistCliApi api)
    {
        var issue = new Command("issue", "Issue management");

        issue.Subcommands.Add(BuildList(api));
        issue.Subcommands.Add(BuildCreate(api));
        issue.Subcommands.Add(BuildShow(api));
        issue.Subcommands.Add(BuildUpdate(api));
        issue.Subcommands.Add(BuildAction("start", "Start workflow", api));
        issue.Subcommands.Add(BuildAction("approve", "Approve workflow", api));
        issue.Subcommands.Add(BuildReject(api));
        issue.Subcommands.Add(BuildAction("close", "Close issue", api));
        issue.Subcommands.Add(BuildAction("reopen", "Reopen issue", api));
        issue.Subcommands.Add(BuildAction("retry", "Retry issue", api));
        issue.Subcommands.Add(BuildAction("rerun", "Rerun issue", api));
        issue.Subcommands.Add(BuildAction("force-stop", "Force stop workflow", api));
        issue.Subcommands.Add(BuildAction("resume", "Resume workflow", api));
        issue.Subcommands.Add(BuildRebase(api));
        issue.Subcommands.Add(BuildArchive(api));
        issue.Subcommands.Add(BuildAction("unarchive", "Unarchive issue", api));
        issue.Subcommands.Add(BuildGetSub("logs", api));
        issue.Subcommands.Add(BuildGetSub("events", api));
        issue.Subcommands.Add(BuildGetSub("diff", api));
        issue.Subcommands.Add(BuildGetSub("commits", api));
        issue.Subcommands.Add(BuildSessions(api));
        issue.Subcommands.Add(BuildWorkflow(api));

        return issue;
    }

    private static Argument<string> NumberArg() => new("number") { Description = "Issue number" };

    private static string ProjectIssuesPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("No active project. Run 'mo project use <id-or-name>' or pass --project-id.");
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List issues");
        cmd.Aliases.Add("ls");
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        var stageOpt = MohistCliCommands.StageOption();
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var allOpt = new Option<bool>("--all") { Description = "Show all issues" };
        var archivedOpt = new Option<bool>("--archived") { Description = "Show archived issues" };
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(allOpt);
        cmd.Options.Add(archivedOpt);
        cmd.SetAction(ctx =>
        {
            var projectId = ctx.GetValue(projectIdOpt);
            var stage = ctx.GetValue(stageOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var all = ctx.GetValue(allOpt);
            var archived = ctx.GetValue(archivedOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                var query = MohistCliCommands.Query(
                    Stage: stage,
                    Label: labels is { Length: > 0 } ? string.Join(",", labels) : null,
                    Priority: priority,
                    Archived: archived ? true : null,
                    All: all ? true : null);
                return await api.PrintGetAsync(ProjectIssuesPath(resolvedProjectId, "/issues") + query);
            }
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new issue");
        var titleArg = new Argument<string>("title") { Description = "Issue title" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Issue body" };
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        var modelOpt = new Option<string?>("--model") { Description = "Model to use" };
        var workflowProfileOpt = new Option<string?>("--workflow-profile") { Description = "Workflow profile ID" };
        cmd.Arguments.Add(titleArg);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(workflowProfileOpt);
        cmd.SetAction(ctx =>
        {
            var title = ctx.GetValue(titleArg);
            var body = ctx.GetValue(bodyOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var model = ctx.GetValue(modelOpt);
            var workflowProfile = ctx.GetValue(workflowProfileOpt);
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintPostAsync(ProjectIssuesPath(resolvedProjectId, "/issues"), new
                {
                    title,
                    body = body ?? "",
                    labels = labels ?? [],
                    priority = priority ?? "p2",
                    model,
                    workflowProfileId = workflowProfile,
                });
            }
        });
        return cmd;
    }

    private static Command BuildShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show issue details");
        var numberArg = NumberArg();
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var projectId = ctx.GetValue(projectIdOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintGetAsync(ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}"));
            }
        });
        return cmd;
    }

    private static Command BuildUpdate(MohistCliApi api)
    {
        var cmd = new Command("update", "Update an issue");
        var numberArg = NumberArg();
        var titleOpt = new Option<string?>("--title") { Description = "New title" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "New body" };
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        var modelOpt = new Option<string?>("--model") { Description = "Model to use" };
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(titleOpt);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(modelOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var title = ctx.GetValue(titleOpt);
            var body = ctx.GetValue(bodyOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var model = ctx.GetValue(modelOpt);
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintPatchAsync(ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}"), new
                {
                    title,
                    body,
                    labels,
                    priority,
                    model,
                });
            }
        });
        return cmd;
    }

    private static Command BuildAction(string name, string description, MohistCliApi api)
    {
        var cmd = new Command(name, $"{description} an issue");
        var numberArg = NumberArg();
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var projectId = ctx.GetValue(projectIdOpt);
            return ActAsync();

            async Task<int> ActAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintPostAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/{name}"),
                    new { });
            }
        });
        return cmd;
    }

    private static Command BuildReject(MohistCliApi api)
    {
        var cmd = new Command("reject", "Reject workflow approval");
        var numberArg = NumberArg();
        var reasonOpt = new Option<string?>("--reason", "-m") { Description = "Rejection reason" };
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(reasonOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var reason = ctx.GetValue(reasonOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RejectAsync();

            async Task<int> RejectAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintPostAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/reject"),
                    new { reason });
            }
        });
        return cmd;
    }

    private static Command BuildRebase(MohistCliApi api)
    {
        var cmd = new Command("rebase", "Rebase issue branch");
        var numberArg = NumberArg();
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch to rebase onto" };
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RebaseAsync();

            async Task<int> RebaseAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintPostAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/rebase"),
                    new { baseBranch });
            }
        });
        return cmd;
    }

    private static Command BuildArchive(MohistCliApi api)
    {
        var cmd = new Command("archive", "Archive issues");
        var numberArg = new Argument<string?>("number")
        {
            Description = "Issue number (omit with --all-completed)",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => null,
        };
        var allCompletedOpt = new Option<bool>("--all-completed") { Description = "Archive all completed issues" };
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(allCompletedOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var allCompleted = ctx.GetValue(allCompletedOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var number = ctx.GetValue(numberArg);
            return ArchiveAsync();

            async Task<int> ArchiveAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                if (allCompleted)
                    return await api.PrintPostAsync(ProjectIssuesPath(resolvedProjectId, "/issues/archive-completed"), new { });
                return await api.PrintPostAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{Uri.EscapeDataString(number!)}/archive"),
                    new { });
            }
        });
        return cmd;
    }

    private static Command BuildGetSub(string name, MohistCliApi api)
    {
        var cmd = new Command(name, $"Show issue {name}");
        var numberArg = NumberArg();
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var projectId = ctx.GetValue(projectIdOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/{name}"));
            }
        });
        return cmd;
    }

    private static Command BuildSessions(MohistCliApi api)
    {
        var cmd = new Command("sessions", "Show coder sessions for issue");
        cmd.Aliases.Add("coder-sessions");
        var numberArg = NumberArg();
        var projectIdOpt = MohistCliCommands.ProjectIdOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var projectId = ctx.GetValue(projectIdOpt);
            return SessionsAsync();

            async Task<int> SessionsAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/coder-sessions"));
            }
        });
        return cmd;
    }

    private static Command BuildWorkflow(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Issue workflow actions");
        var numberArg = new Argument<string>("number") { Description = "Issue number" };
        var projectIdOpt = MohistCliCommands.ProjectIdOption();

        var statusCmd = new Command("status", "Show workflow status");
        statusCmd.Arguments.Add(numberArg);
        statusCmd.Options.Add(projectIdOpt);
        statusCmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var projectId = ctx.GetValue(projectIdOpt);
            return StatusAsync();

            async Task<int> StatusAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow/status"));
            }
        });

        var timelineCmd = new Command("timeline", "Show workflow timeline");
        timelineCmd.Arguments.Add(numberArg);
        timelineCmd.Options.Add(projectIdOpt);
        timelineCmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var projectId = ctx.GetValue(projectIdOpt);
            return TimelineAsync();

            async Task<int> TimelineAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(projectId);
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow/timeline"));
            }
        });

        workflow.Subcommands.Add(statusCmd);
        workflow.Subcommands.Add(timelineCmd);
        return workflow;
    }
}
