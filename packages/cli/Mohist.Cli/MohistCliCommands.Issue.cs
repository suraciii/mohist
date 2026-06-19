using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;

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
        issue.Subcommands.Add(BuildTemplate(api));

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
        var labelOpt = MohistCliCommands.LabelFilterOption();
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
                if (labels is { Length: > 0 })
                {
                    var labelError = LabelDelta.ValidateFilterTokens(labels);
                    if (labelError is not null)
                    {
                        api.Error.WriteLine(labelError);
                        return 1;
                    }
                }
                var query = MohistCliCommands.Query(
                    Stage: stage,
                    Labels: labels,
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
        var riskOpt = new Option<string?>("--risk") { Description = "Risk level (low, medium, high); overrides frontmatter risk" };
        var (readyOpt, draftOpt) = MohistCliCommands.IsDraftFlags("creating");
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
        cmd.Options.Add(riskOpt);
        cmd.Options.Add(readyOpt);
        cmd.Options.Add(draftOpt);
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
            var risk = ctx.GetValue(riskOpt);
            var ready = ctx.GetValue(readyOpt);
            var draft = ctx.GetValue(draftOpt);
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
var draftState = MohistCliCommands.ResolveDraftFlagState(ready, draft);
                if (draftState == MohistCliCommands.DraftFlagState.Conflicting)
                {
                    api.Error.WriteLine("--ready and --draft are mutually exclusive");
                    return 1;
                }
                var isDraft = draftState switch
                {
                    MohistCliCommands.DraftFlagState.Draft => true,
                    MohistCliCommands.DraftFlagState.Ready => false,
                    _ => true,
                };
                var labelParse = LabelDelta.Parse(labels);
                if (!labelParse.IsValid)
                {
                    api.Error.WriteLine(labelParse.Error);
                    return 1;
                }
                var resolvedBody = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, bodyStdin, api.FileSystem, api.StandardInput, api.Error);
                if (resolvedBody is BodyInputResolver.Result.Failure)
                    return 1;
                var bodyText = ((BodyInputResolver.Result.Success)resolvedBody).Body;

                var (effectiveBody, effectiveWorkflow, effectiveRisk) =
                    ApplyFrontmatter(api.Error, bodyText, bodyFile, workflowProfile, risk);

                Dictionary<string, string>? labelMap = null;
                foreach (var entry in labelParse.Entries)
                {
                    if (!entry.IsSet) continue;
                    labelMap ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    labelMap[entry.Key] = entry.Value!;
                }

                var result = await api.PostAndReadAsync(ProjectIssuesPath(resolvedProjectId, "/issues"), new
                {
                    title,
                    body = effectiveBody,
                    labels = labelMap,
                    priority = priority ?? "p2",
                    model,
                    workflowProfileId = effectiveWorkflow,
                    risk = effectiveRisk,
                    isDraft,
                });
                if (result.ExitCode == 0)
                    PrintCreateGuidance(result.Data, api.Output);
                return result.ExitCode;
            }
        });
        return cmd;
    }

    private static (string Body, string? Workflow, string? Risk) ApplyFrontmatter(
        TextWriter error,
        string bodyText,
        string? bodyFile,
        string? workflowFlag,
        string? riskFlag)
    {
        var parsed = FrontmatterParser.Parse(bodyText);

        switch (parsed)
        {
            case FrontmatterParser.Result.Parsed ok:
                var workflow = workflowFlag ?? ok.RecommendedWorkflow;
                var risk = riskFlag ?? ok.Risk;
                if (workflowFlag is not null
                    && ok.RecommendedWorkflow is not null
                    && !string.Equals(workflowFlag, ok.RecommendedWorkflow, StringComparison.Ordinal))
                {
                    error.WriteLine(
                        $"note: --workflow-profile '{workflowFlag}' overrides frontmatter recommended_workflow '{ok.RecommendedWorkflow}'");
                }

                if (riskFlag is not null
                    && ok.Risk is not null
                    && !string.Equals(riskFlag, ok.Risk, StringComparison.Ordinal))
                {
                    error.WriteLine(
                        $"note: --risk '{riskFlag}' overrides frontmatter risk '{ok.Risk}'");
                }

                return (ok.Body, workflow, risk);
            case FrontmatterParser.Result.Malformed:
                if (!string.IsNullOrWhiteSpace(bodyFile))
                    error.WriteLine(
                        $"warning: malformed YAML frontmatter in '{bodyFile}'; sending full body text without parsing metadata");
                else
                    error.WriteLine(
                        "warning: malformed YAML frontmatter; sending full body text without parsing metadata");
                return (bodyText, workflowFlag, riskFlag);
            default:
                if (!string.IsNullOrWhiteSpace(bodyFile))
                    error.WriteLine(
                        "warning: no frontmatter found in body file. Consider including recommended_workflow and risk.");
                return (bodyText, workflowFlag, riskFlag);
        }
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
        var bodyStdinOpt = new Option<bool>("--body-stdin") { Description = "Read new body from stdin (mutually exclusive with --body and --body-stdin)" };
        var labelOpt = MohistCliCommands.LabelOption();
        var priorityOpt = MohistCliCommands.PriorityOption();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var modelOpt = new Option<string?>("--model") { Description = "Model to use" };
        var (readyOpt, draftOpt) = MohistCliCommands.IsDraftFlags("updating");
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
        cmd.Options.Add(readyOpt);
        cmd.Options.Add(draftOpt);
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
            var ready = ctx.GetValue(readyOpt);
            var draft = ctx.GetValue(draftOpt);
            return UpdateAsync();

            async Task<int> UpdateAsync()
            {
                var resolvedProjectId = await api.ResolveProjectIdAsync(project, projectId);
                if (resolvedProjectId is null)
                    return 1;
                var draftState = MohistCliCommands.ResolveDraftFlagState(ready, draft);
                if (draftState == MohistCliCommands.DraftFlagState.Conflicting)
                {
                    api.Error.WriteLine("--ready and --draft are mutually exclusive");
                    return 1;
                }
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

                object payload;
                if (labels is { Length: > 0 })
                {
var labelParse = LabelDelta.Parse(labels);
                    if (!labelParse.IsValid)
                    {
                        api.Error.WriteLine(labelParse.Error);
                        return 1;
                    }
                    var issuePath = $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/issues/{MohistCliCommands.Escape(number!)}";
                    var (loadExit, current) = await LoadCurrentLabelsAsync(api, issuePath);
                    if (loadExit != 0)
                        return loadExit;
                    var merged = LabelDelta.Apply(labelParse.Entries, current);
                    payload = new
                    {
                        title,
                        body,
                        labels = merged,
                        priority,
                        model,
                        isDraft = draftState switch
                        {
                            MohistCliCommands.DraftFlagState.Draft => (bool?)true,
                            MohistCliCommands.DraftFlagState.Ready => (bool?)false,
                            _ => null,
                        },
                    };
                }
                else
                {
                    payload = new
                    {
                        title,
                        body,
                        labels = (Dictionary<string, string>?)null,
                        priority,
                        model,
                        isDraft = draftState switch
                        {
                            MohistCliCommands.DraftFlagState.Draft => (bool?)true,
                            MohistCliCommands.DraftFlagState.Ready => (bool?)false,
                            _ => null,
                        },
                    };
                }

                return await api.PrintPatchAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}"),
                    payload);
            }
        });
        return cmd;
    }

    private static async Task<(int ExitCode, IReadOnlyDictionary<string, string> Labels)> LoadCurrentLabelsAsync(
        MohistCliApi api, string issuePath)
    {
        try
        {
            var data = await api.GetDataAsync(issuePath);
            if (data is null) return (0, new Dictionary<string, string>(StringComparer.Ordinal));
            return (0, ParseLabelsFromIssue(data));
        }
        catch (HttpRequestException)
        {
            api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
            return (1, new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseLabelsFromIssue(System.Text.Json.Nodes.JsonNode? data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data is null) return result;
        var labels = data["labels"];
        if (labels is null) return result;
        if (labels is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var kvp in obj)
            {
                if (kvp.Value is null) continue;
                var str = kvp.Value.GetValue<string>();
                if (str is not null)
                    result[kvp.Key] = str;
            }
        }
        return result;
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

    internal static void PrintCreateGuidance(System.Text.Json.Nodes.JsonNode? data, TextWriter output)
    {
        if (data is null) return;
        var blocker = data["blocker"];
        var isDraft = data["isDraft"]?.GetValue<bool>() ?? false;
        var number = data["number"]?.GetValue<int?>();
        if (isDraft && number is int draftNumber)
        {
            output.WriteLine($"Mark the issue ready with 'mo issue update {draftNumber} --ready' before starting.");
            return;
        }
        if (isDraft)
        {
            output.WriteLine("Mark the issue ready with 'mo issue update <number> --ready' before starting.");
            return;
        }
        if (blocker is System.Text.Json.Nodes.JsonObject blockerObj)
        {
            var kind = blockerObj["kind"]?.GetValue<string>();
            if (kind == "waiting-for")
            {
                var issue = blockerObj["issue"] as System.Text.Json.Nodes.JsonObject;
                var blockedNumber = issue?["number"]?.GetValue<int?>();
                if (blockedNumber is int n)
                {
                    output.WriteLine($"Waiting for #{n} to be delivered before this issue can start.");
                    return;
                }
                output.WriteLine("Waiting for a prerequisite issue to be delivered before this issue can start.");
                return;
            }
        }
        if (number is int n2)
            output.WriteLine($"Tip: Run 'mo issue start {n2}' to begin processing");
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

    private static Command BuildTemplate(MohistCliApi api)
    {
        var template = new Command("template", "Issue template management");
        template.Subcommands.Add(BuildTemplateList(api));
        template.Subcommands.Add(BuildTemplateGet(api));
        return template;
    }

    private static Command BuildTemplateList(MohistCliApi api)
    {
        var cmd = new Command("list", "List available issue templates for the active project");
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
                    IssueTemplatesPath(resolvedProjectId, "/"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueTemplateList));
            }
        });
        return cmd;
    }

    private static Command BuildTemplateGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Show a single issue template by name");
        var nameArg = new Argument<string>("name") { Description = "Template name or id (e.g. mohist/default)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
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
            return GetAsync();

            async Task<int> GetAsync()
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    api.Error.WriteLine("Template name is required");
                    return 1;
                }
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
                    IssueTemplatesPath(resolvedProjectId, $"/{name}"),
                    mode,
                    nameof(MohistCliApi.TableShape.IssueTemplateShow));
            }
        });
        return cmd;
    }

    private static string IssueTemplatesPath(string? projectId, string path)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        var suffix = path == "/" ? string.Empty : (path.StartsWith('/') ? path : "/" + path);
        return $"/api/issue-templates{suffix}?projectId={MohistCliCommands.Escape(projectId)}";
    }
}
