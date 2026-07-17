using System.CommandLine;

namespace Mohist.Cli;

internal static partial class IssueCommands
{
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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
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
        session.Subcommands.Add(BuildSessionCancel(api));

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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
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
        var cmd = new Command("compact", "Compact the session in place");
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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}/compact");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionRecovery),
                    headers: new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") });
            }
        });
        return cmd;
    }

    private static Command BuildSessionReset(MohistCliApi api)
    {
        var cmd = new Command("reset", "Reset the session in place");
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
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}/reset");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionRecovery),
                    headers: new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") });
            }
        });
        return cmd;
    }

    private static Command BuildSessionFollowup(MohistCliApi api)
    {
        var cmd = new Command(
            "followup",
            "Send follow-up text to an AgentSession. It joins an active turn or starts a user-initiated turn when idle without creating a TaskRun or AgentJob. Sends POST /api/projects/:projectId/issues/:number/sessions/:name/followup.");
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
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
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

    private static Command BuildSessionCancel(MohistCliApi api)
    {
        var cmd = new Command("cancel", "Request cancellation of a running session and print its resulting state.");
        var numberArg = NumberArg();
        var nameArg = SessionNameArg();
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");
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
            return CancelAsync();

            async Task<int> CancelAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var path = ProjectIssuesPath(
                    resolvedProjectId,
                    $"/issues/{MohistCliCommands.Escape(number!)}/sessions/{MohistCliCommands.Escape(name!)}/cancel");
                return await api.PrintPostWithOutputAsync(
                    path,
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.AgentSessionCancel),
                    rawJson: true);
            }
        });
        return cmd;
    }
}
