using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

/// <summary>
/// Top-level <c>mo session</c> command group (issue-479 T-005 / design D5+D6).
/// Source-agnostic: every verb is addressed by the stable AgentSession id
/// regardless of whether the session originated from an Agent launch or a
/// Workflow run. Source is a discovery filter (<c>list --agent|--issue|--run</c>),
/// not a separate capability set.
/// </summary>
/// <remarks>
/// <para>
/// <c>show</c> / <c>transcript</c> hit the project-scoped unified
/// <c>/api/projects/{projectRef}/sessions/{sessionId}</c> routes from T-004
/// (no <c>source-kind == agent-launch</c> gate). <c>followup</c>,
/// <c>compact</c>, <c>reset</c>, <c>cancel</c> hit the existing id-keyed
/// action routes under <c>/api/projects/{projectRef}/agent-sessions/{sessionId}/…</c>
/// which already resolve canonically by id for both sources. <c>list</c>
/// hits the unified <c>GET /api/projects/{projectRef}/sessions?agent=|?issue=|?run=</c>
/// route, delegating to the existing per-source queriers.
/// </para>
/// <para>
/// <c>session cancel</c> requests runtime interruption only; it does not
/// transition or rewrite the owning AgentJob's lifecycle (the job remains
/// the sole terminal authority).
/// </para>
/// </remarks>
internal static class SessionCommands
{
    public static Command Build(MohistCliApi api)
    {
        var session = new Command(
            "session",
            "Manage one AgentSession by its stable Session ID (issue-479). " +
            "Subcommands: list (--agent|--issue|--run), view <session-id>, " +
            "transcript <session-id>, followup <session-id>, " +
            "compact <session-id>, reset <session-id>, cancel <session-id>.");

        session.Subcommands.Add(BuildList(api));
        session.Subcommands.Add(BuildView(api));
        session.Subcommands.Add(BuildTranscript(api));
        session.Subcommands.Add(BuildFollowup(api));
        session.Subcommands.Add(BuildCompact(api));
        session.Subcommands.Add(BuildReset(api));
        session.Subcommands.Add(BuildCancel(api));

        return session;
    }

    private static string ProjectSessionsPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/sessions{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static string ProjectAgentSessionsPath(string? projectId, string path = "")
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(MohistCliCommands.NoActiveProjectMessage);
        return $"/api/projects/{MohistCliCommands.Escape(projectId)}/agent-sessions{(path.StartsWith('/') ? path : "/" + path)}";
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command(
            "list",
            "List AgentSessions filtered by source. Exactly one of --agent, --issue, or --run is required.");
        var agentOpt = new Option<string?>("--agent") { Description = "Filter by Agent name or id (agent-launch source)" };
        var issueOpt = new Option<int?>("--issue") { Description = "Filter by Issue number (workflow source)" };
        var runOpt = new Option<string?>("--run") { Description = "Filter by Workflow run id (workflow source)" };
        var limitOpt = new Option<int?>("--limit") { Description = "Maximum number of sessions to return" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Options.Add(agentOpt);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(runOpt);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var agent = ctx.GetValue(agentOpt);
            var issue = ctx.GetValue(issueOpt);
            var run = ctx.GetValue(runOpt);
            var limit = ctx.GetValue(limitOpt);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var agentProvided = ctx.GetResult(agentOpt) is { Implicit: false };
                var issueProvided = ctx.GetResult(issueOpt) is { Implicit: false };
                var runProvided = ctx.GetResult(runOpt) is { Implicit: false };
                var providedCount = (agentProvided ? 1 : 0) + (issueProvided ? 1 : 0) + (runProvided ? 1 : 0);
                if (providedCount == 0)
                {
                    api.Error.WriteLine("One of --agent, --issue, or --run is required.");
                    return 1;
                }
                if (providedCount > 1)
                {
                    api.Error.WriteLine("Only one of --agent, --issue, or --run may be set.");
                    return 1;
                }

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                var queryParts = new List<string>();
                if (agentProvided && !string.IsNullOrWhiteSpace(agent))
                    queryParts.Add($"agent={Uri.EscapeDataString(agent)}");
                if (issueProvided && issue is > 0)
                    queryParts.Add($"issue={issue.Value}");
                if (runProvided && !string.IsNullOrWhiteSpace(run))
                    queryParts.Add($"run={Uri.EscapeDataString(run)}");
                if (limit is > 0)
                    queryParts.Add($"limit={limit.Value}");
                var query = queryParts.Count == 0 ? "" : "?" + string.Join("&", queryParts);

                return await api.PrintWithOutputAsync(
                    $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/sessions{query}",
                    mode,
                    nameof(MohistCliApi.TableShape.SessionList));
            }
        });
        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command(
            "view",
            "Show the unified summary of an AgentSession by its stable Session ID. GETs the project-scoped .../sessions/{sessionId} route that resolves agent-launch and workflow sessions by the same id.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id returned by launch or the workflow session list" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                return await api.PrintWithOutputAsync(
                    ProjectSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}"),
                    mode,
                    nameof(MohistCliApi.TableShape.SessionShow));
            }
        });
        return cmd;
    }

    private static Command BuildTranscript(MohistCliApi api)
    {
        var cmd = new Command(
            "transcript",
            "Show the transcript of an AgentSession by its stable Session ID. GETs the project-scoped .../sessions/{sessionId}/transcript route that resolves agent-launch and workflow sessions by the same id.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id returned by launch or the workflow session list" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return TranscriptAsync();

            async Task<int> TranscriptAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;

                return await api.PrintWithOutputAsync(
                    ProjectSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/transcript"),
                    mode,
                    nameof(MohistCliApi.TableShape.SessionTranscript));
            }
        });
        return cmd;
    }

    private static Command BuildFollowup(MohistCliApi api)
    {
        var cmd = new Command(
            "followup",
            "Send follow-up text to an AgentSession. It joins an active turn or starts a user-initiated turn when idle without creating a TaskRun or AgentJob. Sends POST /api/projects/:projectId/agent-sessions/:sessionId/followup.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id" };
        var textOpt = new Option<string?>("--text") { Description = "Followup text (mutually exclusive with --text-file and --text-stdin)" };
        var textFileOpt = new Option<string?>("--text-file") { Description = "Read followup text from a UTF-8 file path (recommended for long messages; mutually exclusive with --text and --text-stdin)" };
        var textStdinOpt = new Option<bool>("--text-stdin") { Description = "Read followup text from stdin (mutually exclusive with --text and --text-file)" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(textOpt);
        cmd.Options.Add(textFileOpt);
        cmd.Options.Add(textStdinOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
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
                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/followup"),
                    new { text = textValue },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionFollowup),
                    rawJson: true);
            }
        });
        return cmd;
    }

    private static Command BuildCompact(MohistCliApi api) =>
        BuildRecovery(api, "compact", "Compact the session in place");

    private static Command BuildReset(MohistCliApi api) =>
        BuildRecovery(api, "reset", "Reset the session in place");

    private static Command BuildRecovery(MohistCliApi api, string operation, string description)
    {
        var cmd = new Command(operation, description);
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var projectId = ctx.GetValue(projectIdOpt);
            var output = ctx.GetValue(outputOpt);
            return RecoverAsync();

            async Task<int> RecoverAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project, projectId);
                if (resolveExit != 0) return resolveExit;
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/{operation}"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionRecovery),
                    headers: new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString("N") });
            }
        });
        return cmd;
    }

    private static Command BuildCancel(MohistCliApi api)
    {
        var cmd = new Command(
            "cancel",
            "Request interruption of the current Runtime execution only. Sends POST /api/projects/:projectId/agent-sessions/:sessionId/cancel and prints the resulting session state honestly. Does not cancel or rewrite the owning AgentJob lifecycle; the job remains the sole terminal authority.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id" };
        var (projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption("table");

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(projectIdOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
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
                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/cancel"),
                    new { },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionCancel),
                    rawJson: true);
            }
        });
        return cmd;
    }
}
