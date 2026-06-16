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
        issue.Subcommands.Add(BuildFeedback(api));

        return issue;
    }

    private static Argument<string> NumberArg() => new("number") { Description = "Issue number" };

    private static string ProjectIssuesPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List issues");
        cmd.Aliases.Add("ls");
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var stageOpt = MohistCliCommands.StageOption();
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var allOpt = new Option<bool>("--all") { Description = "Show all issues" };
        var archivedOpt = new Option<bool>("--archived") { Description = "Show archived issues" };
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(allOpt);
        cmd.Options.Add(archivedOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var stage = ctx.GetValue(stageOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var all = ctx.GetValue(allOpt);
            var archived = ctx.GetValue(archivedOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var query = MohistCliCommands.Query(
                    Stage: stage,
                    Label: labels is { Length: > 0 } ? string.Join(",", labels) : null,
                    Priority: priority,
                    Archived: archived ? true : null,
                    All: all ? true : null);
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
                return await api.PrintWithOutputAsync(
                    ProjectIssuesPath(resolvedProjectId, "/issues") + query,
                    mode,
                    nameof(MohistCliApi.TableShape.IssueList));
            }
        });
        return cmd;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create a new issue");
        var titleArg = new Argument<string>("title") { Description = "Issue title" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Issue body (mutually exclusive with --body-file and --body-stdin)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read issue body from a UTF-8 file path (recommended for long Markdown; mutually exclusive with --body and --body-stdin)" };
        var bodyStdinOpt = new Option<bool>("--body-stdin") { Description = "Read issue body from stdin (mutually exclusive with --body and --body-file)" };
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var modelOpt = new Option<string?>("--model") { Description = "Model to use" };
        var workflowProfileOpt = new Option<string?>("--workflow-profile") { Description = "Workflow profile ID" };
        cmd.Arguments.Add(titleArg);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(bodyStdinOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(workflowProfileOpt);
        cmd.SetAction(ctx =>
        {
            var title = ctx.GetValue(titleArg);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var bodyStdin = ctx.GetValue(bodyStdinOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var model = ctx.GetValue(modelOpt);
            var workflowProfile = ctx.GetValue(workflowProfileOpt);
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var resolvedBody = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, bodyStdin, api.FileSystem, api.StandardInput, api.Error);
                if (resolvedBody is BodyInputResolver.Result.Failure)
                    return 1;
                var bodyText = ((BodyInputResolver.Result.Success)resolvedBody).Body;
                return await api.PrintPostAsync(ProjectIssuesPath(resolvedProjectId, "/issues"), new
                {
                    title,
                    body = bodyText,
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
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return GetAsync();

            async Task<int> GetAsync()
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
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildUpdate(MohistCliApi api)
    {
        var cmd = new Command("update", "Update an issue");
        var numberArg = NumberArg();
        var titleOpt = new Option<string?>("--title") { Description = "New title" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "New body (mutually exclusive with --body-file and --body-stdin)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read new body from a UTF-8 file path (recommended for long Markdown; mutually exclusive with --body and --body-stdin)" };
        var bodyStdinOpt = new Option<bool>("--body-stdin") { Description = "Read new body from stdin (mutually exclusive with --body and --body-file)" };
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var modelOpt = new Option<string?>("--model") { Description = "Model to use" };
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(titleOpt);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(bodyStdinOpt);
        cmd.Options.Add(labelOpt);
        cmd.Options.Add(priorityOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(modelOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var title = ctx.GetValue(titleOpt);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var bodyStdin = ctx.GetValue(bodyStdinOpt);
            var labels = ctx.GetValue(labelOpt);
            var priority = ctx.GetValue(priorityOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var model = ctx.GetValue(modelOpt);
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var hasAnyBodySource =
                    !string.IsNullOrWhiteSpace(body) ||
                    !string.IsNullOrWhiteSpace(bodyFile) ||
                    bodyStdin;
                if (hasAnyBodySource)
                {
                    var resolvedBody = await BodyInputResolver.ResolveAsync(
                        body, bodyFile, bodyStdin, api.FileSystem, api.StandardInput, api.Error);
                    if (resolvedBody is BodyInputResolver.Result.Failure)
                        return 1;
                    body = ((BodyInputResolver.Result.Success)resolvedBody).Body;
                }
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
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return ActAsync();

            async Task<int> ActAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                return await api.PrintPostAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/{name}"),
                    new { });
            }
        });
         return cmd;
    }

    private static Command BuildRebase(MohistCliApi api)
    {
        var cmd = new Command("rebase", "Rebase issue branch");
        var numberArg = NumberArg();
        var baseBranchOpt = new Option<string?>("--base-branch", "-b") { Description = "Base branch to rebase onto" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(baseBranchOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var baseBranch = ctx.GetValue(baseBranchOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return RebaseAsync();

            async Task<int> RebaseAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
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
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(allCompletedOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var allCompleted = ctx.GetValue(allCompletedOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var number = ctx.GetValue(numberArg);
            return ArchiveAsync();

            async Task<int> ArchiveAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
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
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            return GetAsync();

            async Task<int> GetAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
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
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return SessionsAsync();

            async Task<int> SessionsAsync()
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
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/coder-sessions"),
                    mode,
                    nameof(MohistCliApi.TableShape.Sessions));
            }
        });
        return cmd;
    }

    private static Command BuildWorkflow(MohistCliApi api)
    {
        var workflow = new Command("workflow", "Issue workflow actions");
        var numberArg = new Argument<string>("number") { Description = "Issue number" };

        var statusCmd = new Command("status", "Show workflow status");
        var (statusProjectOpt, statusProjectIdOpt) = MohistCliCommands.ProjectRefOption();
        var statusOutputOpt = MohistCliCommands.OutputOption();
        statusCmd.Arguments.Add(numberArg);
        statusCmd.Options.Add(statusProjectOpt);
        statusCmd.Options.Add(statusProjectIdOpt);
        statusCmd.Options.Add(statusOutputOpt);
        statusCmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(statusProjectOpt);
            var projectId = ctx.GetValue(statusProjectIdOpt);
            var output = ctx.GetValue(statusOutputOpt);
            return StatusAsync();

            async Task<int> StatusAsync()
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
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow/status"),
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowStatus));
            }
        });

        var timelineCmd = new Command("timeline", "Show workflow timeline");
        var (timelineProjectOpt, timelineProjectIdOpt) = MohistCliCommands.ProjectRefOption();
        timelineCmd.Arguments.Add(numberArg);
        timelineCmd.Options.Add(timelineProjectOpt);
        timelineCmd.Options.Add(timelineProjectIdOpt);
        timelineCmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var project = ctx.GetValue(timelineProjectOpt);
            var projectId = ctx.GetValue(timelineProjectIdOpt);
            return TimelineAsync();

            async Task<int> TimelineAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow/timeline"));
            }
        });

        workflow.Subcommands.Add(statusCmd);
        workflow.Subcommands.Add(timelineCmd);
        return workflow;
    }

    private static Command BuildFeedback(MohistCliApi api)
    {
        var feedback = new Command("feedback", "Issue approval feedback");
        feedback.Subcommands.Add(BuildFeedbackList(api));
        feedback.Subcommands.Add(BuildFeedbackShow(api));
        return feedback;
    }

    private static Command BuildFeedbackList(MohistCliApi api)
    {
        var cmd = new Command("list", "List approval feedback records for an issue");
        var numberArg = NumberArg();
        var stageOpt = MohistCliCommands.StageOption();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var stage = ctx.GetValue(stageOpt);
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
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/feedback");
                if (!string.IsNullOrWhiteSpace(stage))
                    path += $"?stage={Uri.EscapeDataString(stage!)}";
                return await api.PrintWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.FeedbackList));
            }
        });
        return cmd;
    }

    private static Command BuildFeedbackShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show approval feedback record");
        var numberArg = NumberArg();
        var feedbackOpt = new Option<string?>("--feedback") { Description = "Feedback id" };
        var latestOpt = new Option<bool>("--latest") { Description = "Show the most recent feedback record" };
        var stageOpt = MohistCliCommands.StageOption();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(feedbackOpt);
        cmd.Options.Add(latestOpt);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var feedbackId = ctx.GetValue(feedbackOpt);
            var latest = ctx.GetValue(latestOpt);
            var stage = ctx.GetValue(stageOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                if (string.IsNullOrWhiteSpace(feedbackId) && !latest)
                {
                    api.Error.WriteLine("Either --feedback <id> or --latest is required");
                    return 1;
                }
                var validation = MohistCliApi.ValidateOutputMode(output);
                if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
                {
                    api.Error.WriteLine(invalid.Message);
                    return 1;
                }
                var mode = ((MohistCliApi.OutputModeResult.Valid)validation).Mode;
                var basePath = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/feedback");

                if (!string.IsNullOrWhiteSpace(feedbackId))
                {
                    var path = $"{basePath}/{Uri.EscapeDataString(feedbackId!)}";
                    return await api.PrintWithOutputAsync(
                        path,
                        mode,
                        nameof(MohistCliApi.TableShape.FeedbackShow));
                }

                var listPath = basePath;
                if (!string.IsNullOrWhiteSpace(stage))
                    listPath += $"?stage={Uri.EscapeDataString(stage!)}";

                var latestData = await api.GetDataSafeAsync(listPath);
                if (latestData is null)
                    return 1;
                var latestId = ExtractLatestId(latestData, stage);
                if (latestId is null)
                {
                    api.Error.WriteLine("No feedback records found");
                    return 1;
                }
                var detailPath = $"{basePath}/{Uri.EscapeDataString(latestId)}";
                return await api.PrintWithOutputAsync(
                    detailPath,
                    mode,
                    nameof(MohistCliApi.TableShape.FeedbackShow));
            }
        });
        return cmd;
    }

    private static string? ExtractLatestId(System.Text.Json.Nodes.JsonNode? data, string? stage)
    {
        if (data is not System.Text.Json.Nodes.JsonArray arr || arr.Count == 0)
            return null;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not System.Text.Json.Nodes.JsonObject obj) continue;
            if (!string.IsNullOrWhiteSpace(stage))
            {
                var recordStage = obj["stage"]?.GetValue<string>();
                if (!string.Equals(recordStage, stage, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var id = obj["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }
        return null;
    }
}
