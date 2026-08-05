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
/// <c>session cancel</c> deterministically cancels a queued Turn. Runtime
/// interruption is exposed separately as <c>session stop</c>.
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
             "compact <session-id>, reset <session-id>, cancel <session-id>, stop <session-id>.");

        session.Subcommands.Add(BuildList(api));
        session.Subcommands.Add(BuildTree(api));
        session.Subcommands.Add(BuildView(api));
        session.Subcommands.Add(BuildTranscript(api));
        session.Subcommands.Add(BuildFollowup(api));
        session.Subcommands.Add(BuildCompact(api));
        session.Subcommands.Add(BuildReset(api));
        session.Subcommands.Add(BuildCancel(api));
        session.Subcommands.Add(BuildStop(api));
        session.Subcommands.Add(BuildDetach(api));

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
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionList)));

        cmd.Options.Add(agentOpt);
        cmd.Options.Add(issueOpt);
        cmd.Options.Add(runOpt);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.Validators.Add(result =>
        {
            var provided = (result.GetResult(agentOpt) is { Implicit: false } ? 1 : 0)
                + (result.GetResult(issueOpt) is { Implicit: false } ? 1 : 0)
                + (result.GetResult(runOpt) is { Implicit: false } ? 1 : 0);
            if (provided > 1)
                result.AddError("Only one of --agent, --issue, or --run may be set.");
        });
        cmd.SetAction(ctx =>
        {
            var agent = ctx.GetValue(agentOpt);
            var issue = ctx.GetValue(issueOpt);
            var run = ctx.GetValue(runOpt);
            var limit = ctx.GetValue(limitOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ListAsync();

            async Task<int> ListAsync()
            {
                var (mode, outputExit) = api.ResolveOutputMode(output);
                if (outputExit != 0) return outputExit;
                var localJsonExit = api.HandleLocalJsonSelection(mode, nameof(MohistCliApi.TableShape.SessionList));
                if (localJsonExit is not null) return localJsonExit.Value;

                var agentProvided = ctx.GetResult(agentOpt) is { Implicit: false };
                var issueProvided = ctx.GetResult(issueOpt) is { Implicit: false };
                var runProvided = ctx.GetResult(runOpt) is { Implicit: false };
                var providedCount = (agentProvided ? 1 : 0) + (issueProvided ? 1 : 0) + (runProvided ? 1 : 0);
                if (providedCount == 0)
                {
                    return CommandHelpHook.RenderUsageFailure(
                        ctx,
                        api.Error,
                        "One of --agent, --issue, or --run is required.");
                }
                if (providedCount > 1)
                {
                    return CommandHelpHook.RenderUsageFailure(
                        ctx,
                        api.Error,
                        "Only one of --agent, --issue, or --run may be set.");
                }

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
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

    private static Command BuildTree(MohistCliApi api)
    {
        var cmd = new Command("tree", "Show the Server-authoritative AgentSession tree rooted at a session.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Root AgentSession id" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var limitOpt = new Option<int?>("--limit") { Description = "Maximum number of tree nodes/edges" };
        var continuationOpt = new Option<string?>("--continuation") { Description = "Continuation token returned by the Server" };
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionTree)));

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(continuationOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx => TreeAsync(ctx));

        async Task<int> TreeAsync(ParseResult ctx)
        {
            var project = ctx.GetValue(projectOpt);
            var sessionId = ctx.GetValue(sessionIdArg);
            var limit = ctx.GetValue(limitOpt);
            var continuation = ctx.GetValue(continuationOpt);
            var output = ctx.GetValue(outputOpt);

            var (mode, exit) = api.ResolveOutputMode(output);
            if (exit != 0)
                return exit;

            var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
            if (resolveExit != 0)
                return resolveExit;

            var query = new List<string>();
            if (limit is not null)
                query.Add($"limit={limit.Value}");
            if (continuation is not null)
                query.Add($"continuation={Uri.EscapeDataString(continuation)}");
            var suffix = query.Count == 0 ? "" : "?" + string.Join("&", query);
            return await api.PrintWithOutputAsync(
                $"/api/projects/{MohistCliCommands.Escape(resolvedProjectId)}/agent-sessions/{MohistCliCommands.Escape(sessionId!)}/tree{suffix}",
                mode,
                nameof(MohistCliApi.TableShape.SessionTree));
        }

        return cmd;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var cmd = new Command(
            "view",
            "Show the unified summary of an AgentSession by its stable Session ID. GETs the project-scoped .../sessions/{sessionId} route that resolves agent-launch and workflow sessions by the same id.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id returned by launch or the workflow session list" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionShow)));

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return ShowAsync();

            async Task<int> ShowAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
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
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionTranscript)));

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return TranscriptAsync();

            async Task<int> TranscriptAsync()
            {
                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
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
        var textOpt = new Option<string?>("--text") { Description = "Followup text (mutually exclusive with --text-file)" };
        var textFileOpt = new Option<string?>("--text-file") { Description = "Read followup text from a UTF-8 file path, or - for stdin (mutually exclusive with --text)" };
        var attachOpt = new Option<string[]?>("--attach")
        {
            Description = "Attach a local file to the follow-up. Repeat for multiple files.",
            AllowMultipleArgumentsPerToken = true,
        };
        var idempotencyKeyOpt = new Option<string?>("--idempotency-key") { Description = "Reuse this key to safely retry a follow-up after response loss" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionFollowup)));

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(textOpt);
        cmd.Options.Add(textFileOpt);
        cmd.Options.Add(attachOpt);
        cmd.Options.Add(idempotencyKeyOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var text = ctx.GetValue(textOpt);
            var textFile = ctx.GetValue(textFileOpt);
            var attachPaths = ctx.GetValue(attachOpt) ?? [];
            var suppliedIdempotencyKey = ctx.GetValue(idempotencyKeyOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return FollowupAsync();

            async Task<int> FollowupAsync()
            {
                var resolvedText = attachPaths.Length > 0
                    && text is null
                    && string.IsNullOrWhiteSpace(textFile)
                    ? new BodyInputResolver.Result.Success("")
                    : await BodyInputResolver.ResolveAsync(
                        text, textFile,
                        new BodyInputResolver.SourceFlags("--text", "--text-file", "text"),
                        api.FileSystem, api.StandardInput, api.Error,
                        allowEmptyBody: attachPaths.Length > 0);
                if (resolvedText is BodyInputResolver.Result.Failure)
                    return CommandHelpHook.RenderNearestUsage(ctx, api.Error);

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;

                var uploads = await AgentAttachmentInput.UploadAsync(api, resolvedProjectId, attachPaths, mode);
                if (uploads is null)
                    return 1;

                var textValue = ((BodyInputResolver.Result.Success)resolvedText).Body;
                var idempotencyKey = string.IsNullOrWhiteSpace(suppliedIdempotencyKey)
                    ? Guid.NewGuid().ToString("N")
                    : suppliedIdempotencyKey;
                if (string.IsNullOrWhiteSpace(suppliedIdempotencyKey))
                {
                    if (mode == "table")
                        api.Output.WriteLine($"Idempotency-Key: {idempotencyKey}");
                    else
                    {
                        api.Error.WriteLine($"Idempotency-Key: {idempotencyKey}");
                        api.Error.WriteLine($"If the outcome is unknown, retry with --idempotency-key {idempotencyKey}.");
                    }
                }
                var attachmentIds = uploads.Select(attachment => attachment.Id).ToArray();
                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/followup"),
                    attachmentIds.Length == 0
                        ? new { text = textValue }
                        : new { text = textValue, attachments = attachmentIds },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionFollowup),
                    rawJson: true,
                    headers: new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey! },
                    retries: 1);
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
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionRecovery)));

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return RecoverAsync();

            async Task<int> RecoverAsync()
            {
                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
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

    private static Command BuildCancel(MohistCliApi api) => BuildTurnControl(
        api,
        "cancel",
        "Deterministically cancel a queued Turn. Sends a Server-only cancel request; use stop for an executing Turn.");

    private static Command BuildStop(MohistCliApi api)
    {
        var cmd = new Command("stop", "Cascade stop a session tree with a durable idempotency key.");
        var sessionIdArg = new Argument<string>("session-id") { Description = "Root AgentSession id" };
        var idempotencyKeyOpt = new Option<string?>("--idempotency-key")
        {
            Description = "Stable key used to retry the same cascade stop operation",
        };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionStop)));

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(idempotencyKeyOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx => StopAsync(ctx));

        async Task<int> StopAsync(ParseResult ctx)
        {
            var key = ctx.GetValue(idempotencyKeyOpt);
            if (string.IsNullOrWhiteSpace(key))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--idempotency-key is required.");

            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(outputOpt));
            if (exit != 0) return exit;
            var (project, projectExit) = await api.ResolveProject(ctx.GetValue(projectOpt));
            if (projectExit != 0) return projectExit;
            return await api.PrintPostWithOutputAsync(
                ProjectAgentSessionsPath(project, $"/{MohistCliCommands.Escape(ctx.GetValue(sessionIdArg)!)}/stop"),
                null,
                mode,
                nameof(MohistCliApi.TableShape.SessionStop),
                rawJson: true,
                headers: new Dictionary<string, string> { ["Idempotency-Key"] = key });
        }

        return cmd;
    }

    private static Command BuildDetach(MohistCliApi api)
    {
        var cmd = new Command("detach", "Detach a child session using its durable parent-link tuple.");
        var childSessionIdArg = new Argument<string>("child-session-id") { Description = "Attached child AgentSession id" };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionDetach)));

        cmd.Arguments.Add(childSessionIdArg);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx => DetachAsync(ctx));

        async Task<int> DetachAsync(ParseResult ctx)
        {
            var (mode, exit) = api.ResolveOutputMode(ctx.GetValue(outputOpt));
            if (exit != 0) return exit;
            var (project, projectExit) = await api.ResolveProject(ctx.GetValue(projectOpt));
            if (projectExit != 0) return projectExit;
            return await api.PrintPostWithOutputAsync(
                ProjectAgentSessionsPath(project, $"/{MohistCliCommands.Escape(ctx.GetValue(childSessionIdArg)!)}/detach"),
                null,
                mode,
                nameof(MohistCliApi.TableShape.SessionDetach),
                rawJson: true);
        }

        return cmd;
    }

    private static Command BuildTurnControl(MohistCliApi api, string operation, string description)
    {
        var cmd = new Command(operation, description);
        var sessionIdArg = new Argument<string>("session-id") { Description = "Stable AgentSession id" };
        var turnIdOpt = new Option<string>("--turn-id")
        {
            Arity = ArgumentArity.ExactlyOne,
            Description = "Stable AgentTurn id to target"
        };
        var projectOpt = MohistCliCommands.ProjectRefOption();
        var outputOpt = MohistCliCommands.OutputOption(ResourceOutputCatalog.For(nameof(MohistCliApi.TableShape.SessionCancel)));

        cmd.Arguments.Add(sessionIdArg);
        cmd.Options.Add(turnIdOpt);
        cmd.Options.Add(projectOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var sessionId = ctx.GetValue(sessionIdArg);
            var turnId = ctx.GetValue(turnIdOpt);
            var project = ctx.GetValue(projectOpt);
            var output = ctx.GetValue(outputOpt);
            return TurnControlAsync();

            async Task<int> TurnControlAsync()
            {
                if (string.IsNullOrWhiteSpace(turnId))
                    return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--turn-id is required.");

                var (mode, exit) = api.ResolveOutputMode(output);
                if (exit != 0) return exit;

                var (resolvedProjectId, resolveExit) = await api.ResolveProject(project);
                if (resolveExit != 0) return resolveExit;
                return await api.PrintPostWithOutputAsync(
                    ProjectAgentSessionsPath(resolvedProjectId, $"/{MohistCliCommands.Escape(sessionId!)}/{operation}"),
                    new { turnId },
                    mode,
                    nameof(MohistCliApi.TableShape.SessionCancel),
                    rawJson: true);
            }
        });
        return cmd;
    }
}
