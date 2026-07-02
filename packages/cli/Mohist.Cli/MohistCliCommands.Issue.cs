using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class IssueCommands
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
        issue.Subcommands.Add(BuildRerunFromStage(api));
        issue.Subcommands.Add(BuildAction("force-stop", "Force stop workflow", api));
        issue.Subcommands.Add(BuildAction("resume", "Resume workflow", api));
        issue.Subcommands.Add(BuildReject(api));
        issue.Subcommands.Add(BuildStop(api));
        issue.Subcommands.Add(BuildRebase(api));
        issue.Subcommands.Add(BuildArchive(api));
        issue.Subcommands.Add(BuildAction("unarchive", "Unarchive issue", api));
        issue.Subcommands.Add(BuildGetSub("logs", api));
        issue.Subcommands.Add(BuildGetSub("events", api));
        issue.Subcommands.Add(BuildGetSub("diff", api));
        issue.Subcommands.Add(BuildGetSub("commits", api));
        issue.Subcommands.Add(BuildSessions(api));
        issue.Subcommands.Add(BuildSession(api));
        issue.Subcommands.Add(BuildWorkflow(api));
        issue.Subcommands.Add(BuildFeedback(api));
        issue.Subcommands.Add(BuildPrereq(api));
        issue.Subcommands.Add(BuildComment(api));
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

    private static bool IsOptionProvided(ParseResult ctx, Option option)
    {
        var result = ctx.GetResult(option);
        if (result is null) return false;
        return !result.Implicit;
    }

    private static (string Mode, int Exit) ValidateOutput(MohistCliApi api, string? output)
    {
        var validation = MohistCliApi.ValidateOutputMode(output);
        if (validation is MohistCliApi.OutputModeResult.Invalid invalid)
        {
            api.Error.WriteLine(invalid.Message);
            return ("json", 1);
        }
        return (((MohistCliApi.OutputModeResult.Valid)validation).Mode, 0);
    }

    private static async Task<(string ProjectId, int Exit)> ResolveProjectId(
        MohistCliApi api, string? project, string? projectId)
    {
        var resolved = await api.ResolveProjectIdAsync(project, projectId);
        if (resolved is null)
            return ("", 1);
        return (resolved, 0);
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                return await api.PrintWithOutputAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/coder-sessions"),
                    mode,
                    nameof(MohistCliApi.TableShape.Sessions));
            }
        });
        return cmd;
    }

    private static Command BuildSession(MohistCliApi api)
    {
        var session = new Command(
            "session",
            "Manage one issue session. <name> is the session name from 'mo issue sessions <num>' (e.g. plan, build, check, integrate).");

        session.Subcommands.Add(BuildSessionShow(api));
        session.Subcommands.Add(BuildSessionTranscript(api));
        session.Subcommands.Add(BuildSessionCompact(api));
        session.Subcommands.Add(BuildSessionReset(api));
        session.Subcommands.Add(BuildSessionFollowup(api));

        return session;
    }

    private static Argument<string> SessionNameArg() => new("name")
    {
        Description = "Session name from 'mo issue sessions <num>'",
    };

    private static Command BuildSessionShow(MohistCliApi api)
    {
        var cmd = new Command("show", "Show session metadata");
        var numberArg = NumberArg();
        var nameArg = SessionNameArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}");
                return await api.PrintWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.SessionMetadata));
            }
        });
        return cmd;
    }

    private static Command BuildSessionTranscript(MohistCliApi api)
    {
        var cmd = new Command("transcript", "Show session transcript summary");
        var numberArg = NumberArg();
        var nameArg = SessionNameArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return TranscriptAsync();

            async Task<int> TranscriptAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}/transcript");
                return await api.PrintWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.SessionTranscriptSummary));
            }
        });
        return cmd;
    }

    private static Command BuildSessionCompact(MohistCliApi api)
    {
        var cmd = new Command("compact", "Compact the session and return a new session id");
        var numberArg = NumberArg();
        var nameArg = SessionNameArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return CompactAsync();

            async Task<int> CompactAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}/compact");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionRecovery));
            }
        });
        return cmd;
    }

    private static Command BuildSessionReset(MohistCliApi api)
    {
        var cmd = new Command("reset", "Reset the session and return a new session id");
        var numberArg = NumberArg();
        var nameArg = SessionNameArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var name = ctx.GetValue(nameArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ResetAsync();

            async Task<int> ResetAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}/reset");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionRecovery));
            }
        });
        return cmd;
    }

    private static Command BuildSessionFollowup(MohistCliApi api)
    {
        var cmd = new Command(
            "followup",
            "Send followup text to a running issue workflow session. Sends POST /api/projects/:projectId/issues/:number/sessions/:name/followup.");
        var numberArg = NumberArg();
        var nameArg = SessionNameArg();
        var textOpt = new Option<string?>("--text") { Description = "Followup text (mutually exclusive with --text-file and --text-stdin)" };
        var textFileOpt = new Option<string?>("--text-file") { Description = "Read followup text from a UTF-8 file path (recommended for long messages; mutually exclusive with --text and --text-stdin)" };
        var textStdinOpt = new Option<bool>("--text-stdin") { Description = "Read followup text from stdin (mutually exclusive with --text and --text-file)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(nameArg);
        cmd.Options.Add(textOpt);
        cmd.Options.Add(textFileOpt);
        cmd.Options.Add(textStdinOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var name = ctx.GetValue(nameArg);
            var text = ctx.GetValue(textOpt);
            var textFile = ctx.GetValue(textFileOpt);
            var textStdin = ctx.GetValue(textStdinOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return FollowupAsync();

            async Task<int> FollowupAsync()
            {
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;

                var resolvedText = await BodyInputResolver.ResolveAsync(
                    text, textFile, textStdin,
                    new BodyInputResolver.SourceFlags("--text", "--text-file", "--text-stdin", "text"),
                    api.FileSystem, api.StandardInput, api.Error);
                if (resolvedText is BodyInputResolver.Result.Failure)
                    return 1;
                var textValue = ((BodyInputResolver.Result.Success)resolvedText).Body;

                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}/followup");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { text = textValue },
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionFollowup),
                    rawJson: true);
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintGetAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow/timeline"));
            }
        });

        workflow.Subcommands.Add(statusCmd);
        workflow.Subcommands.Add(timelineCmd);
        workflow.Subcommands.Add(BuildWorkflowConfig(api));
        return workflow;
    }

    private static Command BuildWorkflowConfig(MohistCliApi api)
    {
        var config = new Command("config", "Issue workflow configuration overrides (template / variables / prompts)");
        config.Subcommands.Add(BuildWorkflowConfigGet(api));
        config.Subcommands.Add(BuildWorkflowConfigPreview(api));
        config.Subcommands.Add(BuildWorkflowConfigSet(api));
        config.Subcommands.Add(BuildWorkflowConfigClear(api));
        return config;
    }

    private static Command BuildWorkflowConfigGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Show the issue's full workflow profile (template / variables / prompts)");
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;

                var profilePath = ProjectIssuesPath(
                    resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile");
                var promptsPath = ProjectIssuesPath(
                    resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile/prompts");

                return await PrintWorkflowProfileAsync(api, profilePath, promptsPath, mode);
            }
        });
        return cmd;
    }

    private static async Task<int> PrintWorkflowProfileAsync(MohistCliApi api, string profilePath, string promptsPath, string mode)
    {
        var (exitCode, dataNode) = await api.GetDataOrPrintErrorAsync(profilePath);
        if (exitCode != 0)
            return exitCode;
        if (dataNode is null)
            return 1;

        var (promptsExitCode, promptsData) = await api.GetDataOrPrintErrorAsync(promptsPath);
        if (promptsExitCode != 0)
            return promptsExitCode;
        if (promptsData is null)
            return 1;
        dataNode["prompts"] = promptsData.DeepClone();

        if (string.Equals(mode, "json", StringComparison.Ordinal))
        {
            api.Output.WriteLine(dataNode.ToJsonString(MohistCliApi.JsonOutputOptions));
            return 0;
        }

        return await api.RenderTableAsync(dataNode, MohistCliApi.TableShape.WorkflowProfile);
    }

    private static Command BuildWorkflowConfigPreview(MohistCliApi api)
    {
        var cmd = new Command("preview", "Render a prompt under the issue's current variables and template");
        var numberArg = NumberArg();
        var keyArg = new Argument<string>("key") { Description = "Prompt key (e.g. plan_prompt)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(keyArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var key = ctx.GetValue(keyArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return PreviewAsync();

            async Task<int> PreviewAsync()
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    api.Error.WriteLine("prompt key is required");
                    return 1;
                }
                if (key.Contains('/'))
                {
                    api.Error.WriteLine($"prompt key '{key}' must not contain '/'");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                return await api.PrintPostWithOutputAsync(
                    ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile/prompts/{Uri.EscapeDataString(key!)}/preview"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.WorkflowProfilePreview));
            }
        });
        return cmd;
    }

    private static Command BuildWorkflowConfigSet(MohistCliApi api)
    {
        var cmd = new Command("set", "Composite config writes (template / variables / prompts)");
        var numberArg = NumberArg();
        var templateOpt = new Option<string?>("--template")
        {
            Description = "Inline YAML template body, or '@<file>' to read UTF-8 from a file (PUT /workflow-profile/template)",
        };
        var varOpt = new Option<string[]?>("--var")
        {
            Description = "Set a top-level variable as 'k=v'. Repeatable; merged into one PATCH /workflow-profile/variables.",
            AllowMultipleArgumentsPerToken = true,
        };
        var stageVarOpt = new Option<string[]?>("--stage-var")
        {
            Description = "Set a stage-scoped variable as '<stage>.k=v'. Repeatable; merged into the same PATCH.",
            AllowMultipleArgumentsPerToken = true,
        };
        var promptOpt = new Option<string[]?>("--prompt")
        {
            Description = "Set a prompt as 'key=body' or 'key=@<file>'. Repeatable; one PUT /workflow-profile/prompts/<key> per occurrence.",
            AllowMultipleArgumentsPerToken = true,
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(templateOpt);
        cmd.Options.Add(varOpt);
        cmd.Options.Add(stageVarOpt);
        cmd.Options.Add(promptOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var template = ctx.GetValue(templateOpt);
            var vars = ctx.GetValue(varOpt);
            var stageVars = ctx.GetValue(stageVarOpt);
            var prompts = ctx.GetValue(promptOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var templateProvided = ctx.GetResult(templateOpt) is not null;
            var varProvided = ctx.GetResult(varOpt) is not null;
            var stageVarProvided = ctx.GetResult(stageVarOpt) is not null;
            var promptProvided = ctx.GetResult(promptOpt) is not null;
            return SetAsync();

            async Task<int> SetAsync()
            {
                var hasAnyChange = templateProvided || varProvided || stageVarProvided || promptProvided;
                if (!hasAnyChange)
                {
                    api.Error.WriteLine("nothing to change — pass at least one of --template, --var, --stage-var, or --prompt");
                    return 1;
                }

                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;

                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;

                var issuePath = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile");

                var varsPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                var stagesPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? templateText = null;
                var promptPayloads = new List<(string Key, string Body)>();

                if (varProvided)
                {
                    foreach (var entry in vars!)
                    {
                        var eq = entry.IndexOf('=');
                        if (eq <= 0)
                        {
                            api.Error.WriteLine($"--var '{entry}' must be in 'k=v' form");
                            return 1;
                        }
                        var key = entry[..eq];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--var '{entry}' has an empty key");
                            return 1;
                        }
                        varsPayload[key] = entry[(eq + 1)..];
                    }
                }

                if (stageVarProvided)
                {
                    foreach (var entry in stageVars!)
                    {
                        var dot = entry.IndexOf('.');
                        if (dot <= 0)
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' must be in '<stage>.k=v' form");
                            return 1;
                        }
                        var stage = entry[..dot];
                        if (string.IsNullOrWhiteSpace(stage))
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' has an empty stage");
                            return 1;
                        }
                        var remainder = entry[(dot + 1)..];
                        var eq = remainder.IndexOf('=');
                        if (eq <= 0)
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' must be in '<stage>.k=v' form");
                            return 1;
                        }
                        var key = remainder[..eq];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--stage-var '{entry}' has an empty key");
                            return 1;
                        }
                        if (!stagesPayload.TryGetValue(stage, out var existing))
                        {
                            existing = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["vars"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                            };
                            stagesPayload[stage] = existing;
                        }
                        var stageObj = (Dictionary<string, object?>)existing!;
                        var stageVars = (Dictionary<string, object?>)stageObj["vars"]!;
                        stageVars[key] = remainder[(eq + 1)..];
                    }
                }

                if (templateProvided)
                {
                    var expanded = await api.ExpandAtFileAsync(template, "--template");
                    if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                        return 1;
                    templateText = ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value;
                }

                if (promptProvided)
                {
                    foreach (var entry in prompts!)
                    {
                        var eq = entry.IndexOf('=');
                        if (eq <= 0)
                        {
                            api.Error.WriteLine($"--prompt '{entry}' must be in 'key=body' or 'key=@<file>' form");
                            return 1;
                        }
                        var key = entry[..eq];
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--prompt '{entry}' has an empty key");
                            return 1;
                        }
                        if (key.Contains('/'))
                        {
                            api.Error.WriteLine($"--prompt key '{key}' must not contain '/'");
                            return 1;
                        }

                        var rawBody = entry[(eq + 1)..];
                        var expanded = await api.ExpandAtFileAsync(rawBody, "--prompt");
                        if (expanded is MohistCliApi.ExpandAtFileResult.Failure)
                            return 1;
                        promptPayloads.Add((key, ((MohistCliApi.ExpandAtFileResult.Success)expanded).Value));
                    }
                }

                if (varProvided || stageVarProvided)
                {
                    var patchBody = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (varProvided)
                        patchBody["vars"] = varsPayload;
                    if (stageVarProvided)
                        patchBody["stages"] = stagesPayload;

                    var patchExit = await api.PrintPatchWithOutputAsync(
                        issuePath + "/variables",
                        patchBody,
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowVariables));
                    if (patchExit != 0)
                        return patchExit;
                }

                if (templateProvided)
                {
                    var putExit = await api.PrintPutWithOutputAsync(
                        issuePath + "/template",
                        new { yaml = templateText },
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowProfile));
                    if (putExit != 0)
                        return putExit;
                }

                if (promptProvided)
                {
                    foreach (var prompt in promptPayloads)
                    {
                        var promptExit = await api.PrintPutWithOutputAsync(
                            $"{issuePath}/prompts/{Uri.EscapeDataString(prompt.Key)}",
                            new { body = prompt.Body },
                            mode,
                            nameof(MohistCliApi.TableShape.WorkflowProfilePrompt));
                        if (promptExit != 0)
                            return promptExit;
                    }
                }

                return 0;
            }
        });
        return cmd;
    }

    private static Command BuildWorkflowConfigClear(MohistCliApi api)
    {
        var cmd = new Command("clear", "Composite config removals (template / variables / prompts)");
        var numberArg = NumberArg();
        var templateOpt = new Option<bool>("--template")
        {
            Description = "Remove the issue's template override (DELETE /workflow-profile/template)",
        };
        var varOpt = new Option<string[]?>("--var")
        {
            Description = "Remove a variable by key. Use '<stage>.k' for stage-scoped variables. Repeatable; merged into one PATCH /workflow-profile/variables with each key set to null.",
            AllowMultipleArgumentsPerToken = true,
        };
        var promptOpt = new Option<string[]?>("--prompt")
        {
            Description = "Remove a prompt by key. Repeatable; one DELETE /workflow-profile/prompts/<key> per occurrence.",
            AllowMultipleArgumentsPerToken = true,
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(templateOpt);
        cmd.Options.Add(varOpt);
        cmd.Options.Add(promptOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var vars = ctx.GetValue(varOpt);
            var prompts = ctx.GetValue(promptOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var templateProvided = IsOptionProvided(ctx, templateOpt);
            var varProvided = ctx.GetResult(varOpt) is not null;
            var promptProvided = ctx.GetResult(promptOpt) is not null;
            return ClearAsync();

            async Task<int> ClearAsync()
            {
                var hasAnyClear = templateProvided || varProvided || promptProvided;
                if (!hasAnyClear)
                {
                    api.Error.WriteLine("nothing to clear — pass at least one of --template, --var, or --prompt");
                    return 1;
                }

                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;

                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;

                var issuePath = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/workflow-profile");

                var varsPatchBody = new Dictionary<string, object?>(StringComparer.Ordinal);
                var promptKeys = new List<string>();

                if (varProvided)
                {
                    var varsPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var stagesPayload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var entry in vars!)
                    {
                        var key = entry;
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--var '{entry}' has an empty key");
                            return 1;
                        }
                        var dot = key.IndexOf('.');
                        if (dot > 0 && dot < key.Length - 1)
                        {
                            var stage = key[..dot];
                            var stageKey = key[(dot + 1)..];
                            if (!stagesPayload.TryGetValue(stage, out var existing))
                            {
                                existing = new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["vars"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                                };
                                stagesPayload[stage] = existing;
                            }
                            var stageObj = (Dictionary<string, object?>)existing!;
                            var stageVars = (Dictionary<string, object?>)stageObj["vars"]!;
                            stageVars[stageKey] = null;
                        }
                        else
                        {
                            varsPayload[key] = null;
                        }
                    }
                    if (varsPayload.Count > 0)
                        varsPatchBody["vars"] = varsPayload;
                    if (stagesPayload.Count > 0)
                        varsPatchBody["stages"] = stagesPayload;
                }

                if (promptProvided)
                {
                    foreach (var key in prompts!)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            api.Error.WriteLine($"--prompt key '{key}' must not be empty");
                            return 1;
                        }
                        if (key.Contains('/'))
                        {
                            api.Error.WriteLine($"--prompt key '{key}' must not contain '/'");
                            return 1;
                        }

                        promptKeys.Add(key);
                    }
                }

                if (varProvided)
                {
                    var patchExit = await api.PrintPatchWithOutputAsync(
                        issuePath + "/variables",
                        varsPatchBody,
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowVariables));
                    if (patchExit != 0)
                        return patchExit;
                }

                if (templateProvided)
                {
                    var deleteExit = await api.PrintDeleteWithOutputAsync(
                        issuePath + "/template",
                        mode,
                        nameof(MohistCliApi.TableShape.WorkflowProfile));
                    if (deleteExit != 0)
                        return deleteExit;
                }

                if (promptProvided)
                {
                    foreach (var key in promptKeys)
                    {
                        var promptExit = await api.PrintDeleteWithOutputAsync(
                            $"{issuePath}/prompts/{Uri.EscapeDataString(key)}",
                            mode,
                            nameof(MohistCliApi.TableShape.WorkflowProfilePrompt),
                            new JsonObject
                            {
                                ["key"] = key,
                                ["deleted"] = true,
                            });
                        if (promptExit != 0)
                            return promptExit;
                    }
                }

                return 0;
            }
        });
        return cmd;
    }

    private static Command BuildFeedback(MohistCliApi api)
    {
        var feedback = new Command("feedback", "Issue approval feedback");
        feedback.Subcommands.Add(BuildFeedbackList(api));
        feedback.Subcommands.Add(BuildFeedbackShow(api));
        feedback.Subcommands.Add(BuildFeedbackCreate(api));
        return feedback;
    }

    private static Command BuildFeedbackCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Create an approval feedback record");
        var numberArg = NumberArg();
        var stageOpt = new Option<string?>("--stage", "-s") { Description = "Workflow stage (e.g. plan, build, check)" };
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Feedback body text (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read feedback body from a UTF-8 file path (recommended for long Markdown; mutually exclusive with --body)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(stageOpt);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var stage = ctx.GetValue(stageOpt);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var stageProvided = ctx.GetResult(stageOpt) is not null;
            var bodyProvided = ctx.GetResult(bodyOpt) is not null;
            var bodyFileProvided = ctx.GetResult(bodyFileOpt) is not null;
            return CreateAsync();

            async Task<int> CreateAsync()
            {
                if (!stageProvided || string.IsNullOrWhiteSpace(stage))
                {
                    api.Error.WriteLine("--stage is required");
                    return 1;
                }
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var resolved = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, false, api.FileSystem, api.StandardInput, api.Error);
                if (resolved is BodyInputResolver.Result.Failure)
                    return 1;
                var bodyText = ((BodyInputResolver.Result.Success)resolved).Body;
                var payload = new { stage, body = bodyText };
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/feedback");
                return await api.PrintPostWithOutputAsync(
                    path,
                    payload,
                    mode,
                    nameof(MohistCliApi.TableShape.FeedbackShow));
            }
        });
        return cmd;
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                if (string.IsNullOrWhiteSpace(feedbackId) && !latest)
                {
                    api.Error.WriteLine("Either --feedback <id> or --latest is required");
                    return 1;
                }
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
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

    private static Command BuildPrereq(MohistCliApi api)
    {
        var prereq = new Command("prereq", "Manage issue start prerequisites");
        prereq.Subcommands.Add(BuildPrereqAdd(api));
        prereq.Subcommands.Add(BuildPrereqRemove(api));
        return prereq;
    }

    private static Command BuildPrereqAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a start prerequisite to an issue");
        var numberArg = NumberArg();
        var prereqNumberArg = new Argument<int>("prereq-number")
        {
            Description = "Prerequisite issue number",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return AddAsync();

            async Task<int> AddAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { prerequisiteNumber = prereqNumber },
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildPrereqRemove(MohistCliApi api)
    {
        var cmd = new Command("remove", "Remove a start prerequisite from an issue");
        var numberArg = NumberArg();
        var prereqNumberArg = new Argument<int>("prereq-number")
        {
            Description = "Prerequisite issue number",
        };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Arguments.Add(prereqNumberArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var prereqNumber = ctx.GetValue(prereqNumberArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return RemoveAsync();

            async Task<int> RemoveAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/prerequisites/{prereqNumber}");
                return await api.PrintDeleteWithOutputAsync(
                    path,
                    mode,
                    nameof(MohistCliApi.TableShape.IssueShow));
            }
        });
        return cmd;
    }

    private static Command BuildComment(MohistCliApi api)
    {
        var comment = new Command("comment", "Manage issue comments");
        comment.Subcommands.Add(BuildCommentAdd(api));
        return comment;
    }

    private static Command BuildCommentAdd(MohistCliApi api)
    {
        var cmd = new Command("add", "Add a comment to an issue");
        var numberArg = NumberArg();
        var bodyOpt = new Option<string?>("--body", "-b") { Description = "Comment body text (mutually exclusive with --body-file)" };
        var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read comment body from a UTF-8 file path (recommended for long Markdown; mutually exclusive with --body)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption();
        cmd.Arguments.Add(numberArg);
        cmd.Options.Add(bodyOpt);
        cmd.Options.Add(bodyFileOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var number = ctx.GetValue(numberArg);
            var body = ctx.GetValue(bodyOpt);
            var bodyFile = ctx.GetValue(bodyFileOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            var bodyProvided = ctx.GetResult(bodyOpt) is not null;
            var bodyFileProvided = ctx.GetResult(bodyFileOpt) is not null;
            return AddAsync();

            async Task<int> AddAsync()
            {
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
                var resolved = await BodyInputResolver.ResolveAsync(
                    body, bodyFile, false, api.FileSystem, api.StandardInput, api.Error);
                if (resolved is BodyInputResolver.Result.Failure)
                    return 1;
                var bodyText = ((BodyInputResolver.Result.Success)resolved).Body;
                var path = ProjectIssuesPath(resolvedProjectId, $"/issues/{MohistCliCommands.Escape(number!)}/comments");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { body = bodyText },
                    mode,
                    nameof(MohistCliApi.TableShape.FeedbackShow));
            }
        });
        return cmd;
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
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
        var nameArg = new Argument<string>("name") { Description = "Template name or id (e.g. feature)" };
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
                var (resolvedProjectId, resolveExit) = await ResolveProjectId(api, project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = ValidateOutput(api, output);
                if (exit != 0) return exit;
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
