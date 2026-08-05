using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class SlackCommands
{
    private static readonly ResourceDescriptor SetupDescriptor = new(
        ResourceCardinality.Single,
        ["enrollment", "claimCode", "claimExpiresAt", "nextAction"]);

    private static readonly ResourceDescriptor StatusDescriptor = new(
        ResourceCardinality.Single,
        ["enrollment", "connections", "managedApps", "nextAction"]);

    private static readonly ResourceDescriptor InstallAgentDescriptor = new(
        ResourceCardinality.Single,
        ["connection", "managedApp", "nextAction"]);

    private const int WizardStepBudget = 8;

    public static Command Build(MohistCliApi api, OperatorCredentialProvider credentials)
    {
        var group = new Command("slack", "Manage Slack integrations");
        group.Subcommands.Add(BuildSetup(api, credentials));
        group.Subcommands.Add(BuildStatus(api, credentials));
        group.Subcommands.Add(BuildInstallAgent(api));
        group.Subcommands.Add(BuildList(api));
        group.Subcommands.Add(BuildView(api));
        group.Subcommands.Add(BuildClaimOwner(api));
        group.Subcommands.Add(BuildEdit(api));
        group.Subcommands.Add(BuildTransferOwner(api));
        group.Subcommands.Add(BuildEnable(api));
        group.Subcommands.Add(BuildDisable(api));
        group.Subcommands.Add(BuildRemoveBinding(api));
        group.Subcommands.Add(BuildPermanentDelete(api));
        group.Subcommands.Add(BuildListDeliveries(api));
        group.Subcommands.Add(BuildResendDelivery(api));
        group.Subcommands.Add(BuildClearGap(api));
        group.Subcommands.Add(BuildReconcileCreate(api));
        group.Subcommands.Add(BuildReconcileDelete(api));
        return group;
    }

    private static Command BuildSetup(MohistCliApi api, OperatorCredentialProvider credentials)
    {
        var command = new Command("setup", "Install or resume the workspace-level Mohist Slack App (idempotent wizard)");
        var workspaceTeam = new Option<string?>("--workspace-team")
        {
            Description = "Slack workspace team id; required when it cannot be determined interactively.",
        };
        var configurationTokenFile = new Option<string?>("--configuration-token-file")
        {
            Description = "Protected JSON file containing exactly configurationToken and configurationRefreshToken strings, accepted for workspace App provisioning.",
        };
        var credentialsFile = new Option<string?>("--credentials-file")
        {
            Description = "Protected JSON file containing exactly one non-empty botToken string. Re-supplying it on a ready workspace rotates the stored credential.",
        };
        var managerAppId = new Option<string?>("--manager-app-id")
        {
            Description = "Mohist App id in Slack, required for the enrollment step.",
        };
        var managerBotUserId = new Option<string?>("--manager-bot-user-id")
        {
            Description = "Mohist App bot user id in Slack, required for the enrollment step.",
        };
        var managerCredentialRef = new Option<string?>("--manager-credential-ref")
        {
            Description = "Reference to the stored Mohist App credential, required for the enrollment step.",
        };
        var output = MohistCliCommands.OutputOption(SetupDescriptor);
        command.Options.Add(workspaceTeam);
        command.Options.Add(configurationTokenFile);
        command.Options.Add(credentialsFile);
        command.Options.Add(managerAppId);
        command.Options.Add(managerBotUserId);
        command.Options.Add(managerCredentialRef);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, SetupDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(SetupDescriptor, selection);

            var configurationTokenPath = ctx.GetValue(configurationTokenFile);
            if (!string.IsNullOrWhiteSpace(configurationTokenPath)
                && !await ReadConfigurationTokenAsync(api, configurationTokenPath).ConfigureAwait(false))
                return CliExitCode.For(CliExitOutcome.UsageFailure);

            var (state, exit) = await RunSetupWizardAsync(
                api, credentials, ctx, workspaceTeam, credentialsFile,
                managerAppId, managerBotUserId, managerCredentialRef,
                jsonMode: selection.Kind == JsonSelectionKind.Selected).ConfigureAwait(false);
            if (state is null || exit != 0) return exit;
            if (selection.Kind != JsonSelectionKind.Selected) return 0;
            return await new CliResultWriter(api.Invocation).WriteSuccessAsync(
                selection.Project(state, SetupDescriptor.Cardinality)).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task<(JsonObject? State, int Exit)> RunSetupWizardAsync(
        MohistCliApi api,
        OperatorCredentialProvider credentials,
        ParseResult ctx,
        Option<string?> workspaceTeam,
        Option<string?> credentialsFile,
        Option<string?> managerAppId,
        Option<string?> managerBotUserId,
        Option<string?> managerCredentialRef,
        bool jsonMode)
    {
        var team = await ResolveWorkspaceTeamAsync(api, ctx, workspaceTeam, "slack setup").ConfigureAwait(false);
        if (team is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));

        var headers = await GetOperatorHeadersAsync(api, credentials).ConfigureAwait(false);
        if (headers is null) return (null, 1);

        JsonObject? enrollment = null;
        string? nextAction = null;
        string? claimCode = null;
        string? claimExpiresAt = null;
        var rotated = false;
        var credentialsFileProvided = !string.IsNullOrWhiteSpace(ctx.GetValue(credentialsFile));

        for (var step = 0; step < WizardStepBudget; step++)
        {
            var status = await ReadEnrollmentStatusAsync(api, team, headers).ConfigureAwait(false);
            if (status is null) return (null, 1);
            if (status.Failure is not null)
            {
                if (status.StatusCode != HttpStatusCode.NotFound)
                    return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(status.Failure).ConfigureAwait(false));
                nextAction = "setup";
            }
            else
            {
                enrollment = (status.Data as JsonObject)?["enrollment"]?.DeepClone() as JsonObject ?? enrollment;
                nextAction = ValueOf(status.Data as JsonObject, "nextAction");
            }

            switch (nextAction)
            {
                case "setup":
                case "configure_manager_app":
                {
                    var facts = await ResolveManagerFactsAsync(api, ctx, managerAppId, managerBotUserId, managerCredentialRef).ConfigureAwait(false);
                    if (facts is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));
                    var result = await PostSetupAsync(api, team, facts, headers).ConfigureAwait(false);
                    if (result.Failure is not null)
                        return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(result.Failure).ConfigureAwait(false));
                    await api.Error.WriteLineAsync($"Workspace {team} is enrolled.").ConfigureAwait(false);
                    if (result.Data is JsonObject setupData)
                    {
                        enrollment = setupData["enrollment"]?.DeepClone() as JsonObject ?? enrollment;
                        var code = ValueOf(setupData, "claimCode");
                        if (!string.IsNullOrEmpty(code))
                        {
                            claimCode = code;
                            claimExpiresAt = ValueOf(setupData, "claimExpiresAt");
                        }

                        var responseNextAction = ValueOf(setupData, "nextAction");
                        if (!string.IsNullOrEmpty(responseNextAction)) nextAction = responseNextAction;
                    }

                    break;
                }

                case "configure_manager_credentials":
                {
                    var token = await ReadManagerCredentialAsync(api, ctx.GetValue(credentialsFile)).ConfigureAwait(false);
                    if (token is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));
                    var provision = await api.ResponseReader.ReadAsync(
                        HttpMethod.Post,
                        "/api/slack-manager/credentials",
                        new { workspaceTeamId = team, managerBotToken = token },
                        mutating: true,
                        headers: headers,
                        cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
                    if (provision.Failure is not null)
                        return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(provision.Failure).ConfigureAwait(false));
                    await api.Error.WriteLineAsync($"Manager credential provisioned for {team}.").ConfigureAwait(false);
                    break;
                }

                case "claim_manager":
                {
                    if (claimCode is null)
                    {
                        var facts = await ResolveManagerFactsAsync(api, ctx, managerAppId, managerBotUserId, managerCredentialRef).ConfigureAwait(false);
                        if (facts is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));
                        var result = await PostSetupAsync(api, team, facts, headers).ConfigureAwait(false);
                        if (result.Failure is not null)
                            return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(result.Failure).ConfigureAwait(false));
                        if (result.Data is JsonObject setupData)
                        {
                            claimCode = ValueOf(setupData, "claimCode");
                            claimExpiresAt = ValueOf(setupData, "claimExpiresAt");
                            var responseNextAction = ValueOf(setupData, "nextAction");
                            if (!string.IsNullOrEmpty(responseNextAction)) nextAction = responseNextAction;
                        }

                        if (string.IsNullOrEmpty(claimCode)) break;
                    }

                    if (!jsonMode) RenderSetupClaim(api, team, claimCode, claimExpiresAt);
                    return (BuildSetupState(enrollment, claimCode, claimExpiresAt, "claim_manager"), 0);
                }

                case "ready":
                {
                    if (credentialsFileProvided && !rotated)
                    {
                        rotated = true;
                        var token = await ReadManagerCredentialAsync(api, ctx.GetValue(credentialsFile)).ConfigureAwait(false);
                        if (token is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));
                        var provision = await api.ResponseReader.ReadAsync(
                            HttpMethod.Post,
                            "/api/slack-manager/credentials",
                            new { workspaceTeamId = team, managerBotToken = token },
                            mutating: true,
                            headers: headers,
                            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
                        if (provision.Failure is not null)
                            return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(provision.Failure).ConfigureAwait(false));
                        await api.Error.WriteLineAsync($"Manager credential rotated for {team}.").ConfigureAwait(false);
                        break;
                    }

                    if (!jsonMode) RenderSetupReady(api, team, enrollment);
                    return (BuildSetupState(enrollment, claimCode, claimExpiresAt, "ready"), 0);
                }

                default:
                {
                    if (!jsonMode) RenderSetupNextAction(api, team, nextAction);
                    return (BuildSetupState(enrollment, claimCode, claimExpiresAt, nextAction), 0);
                }
            }
        }

        api.Error.WriteLine("Slack setup did not converge; re-run `mo slack setup` to continue.");
        return (BuildSetupState(enrollment, claimCode, claimExpiresAt, nextAction), 1);
    }

    private static JsonObject BuildSetupState(
        JsonObject? enrollment,
        string? claimCode,
        string? claimExpiresAt,
        string? nextAction) => new()
    {
        ["enrollment"] = enrollment?.DeepClone(),
        ["claimCode"] = claimCode,
        ["claimExpiresAt"] = claimExpiresAt,
        ["nextAction"] = nextAction,
    };

    private static void RenderSetupClaim(MohistCliApi api, string team, string? claimCode, string? claimExpiresAt)
    {
        api.Output.WriteLine($"Workspace {team} is enrolled and waiting for its Slack Owner claim.");
        WriteValue(api.Output, "  code", claimCode ?? "");
        WriteValue(api.Output, "  valid until", claimExpiresAt ?? "");
        api.Output.WriteLine("  1. Open Slack and DM the Mohist App bot.");
        api.Output.WriteLine("  2. Send the code above as the only message.");
        api.Output.WriteLine("  The code is single-use. Re-running `mo slack setup` issues a fresh code that invalidates this one.");
    }

    private static void RenderSetupReady(MohistCliApi api, string team, JsonObject? enrollment)
    {
        api.Output.WriteLine($"Slack workspace {team} is ready.");
        WriteValue(api.Output, "  enrollment", ValueOf(enrollment, "id"));
        api.Output.WriteLine("  The Mohist App is installed, its credential is provisioned and the workspace is claimed.");
    }

    private static void RenderSetupNextAction(MohistCliApi api, string team, string? nextAction)
    {
        api.Output.WriteLine($"Slack workspace {team}: next action is '{nextAction}'.");
        api.Output.WriteLine("Re-run `mo slack setup` to continue from the current step.");
    }

    private static async Task<CliResponseResult?> ReadEnrollmentStatusAsync(
        MohistCliApi api,
        string team,
        IReadOnlyDictionary<string, string> headers)
    {
        var status = await api.ResponseReader.ReadAsync(
            HttpMethod.Get,
            $"/api/slack-manager/status?workspaceTeamId={Uri.EscapeDataString(team)}",
            headers: headers,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
        if (status.Failure is not null
            && status.StatusCode != HttpStatusCode.NotFound
            && status.StatusCode != (HttpStatusCode)0)
            return status;
        return status;
    }

    private static async Task<CliResponseResult> PostSetupAsync(
        MohistCliApi api,
        string team,
        ManagerEnrollmentFacts facts,
        IReadOnlyDictionary<string, string> headers)
    {
        var result = await api.ResponseReader.ReadAsync(
            HttpMethod.Post,
            "/api/slack-manager/setup",
            new
            {
                workspaceTeamId = team,
                managerAppId = facts.AppId,
                managerBotUserId = facts.BotUserId,
                managerCredentialRef = facts.CredentialRef,
                transportKind = "socket",
                readiness = "ready",
            },
            mutating: true,
            headers: headers,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
        if (result.Failure is null)
            return result;
        if (result.StatusCode == HttpStatusCode.Conflict && result.Failure.Code == "enrollment_removed")
        {
            await api.Error.WriteLineAsync(
                "The workspace enrollment was removed and cannot be reused; choose another workspace or re-enroll from scratch.").ConfigureAwait(false);
        }

        return result;
    }

    private static Command BuildInstallAgent(MohistCliApi api)
    {
        var command = new Command("install-agent", "Install or resume an existing Agent's Slack installation (idempotent wizard)");
        var agent = new Argument<string>("agent") { Description = "Agent name or id" };
        var workspaceTeam = new Option<string?>("--workspace-team")
        {
            Description = "Slack workspace team id; required when it cannot be determined interactively.",
        };
        var credentialsFile = new Option<string?>("--credentials-file")
        {
            Description = "Protected JSON file containing exactly appToken and botToken strings, validated when the credential step is reached.",
        };
        var project = MohistCliCommands.ProjectRefOption();
        var output = MohistCliCommands.OutputOption(InstallAgentDescriptor);
        command.Arguments.Add(agent);
        command.Options.Add(workspaceTeam);
        command.Options.Add(credentialsFile);
        command.Options.Add(project);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, InstallAgentDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(InstallAgentDescriptor, selection);

            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project)).ConfigureAwait(false);
            if (exit != 0 || projectId is null) return exit;

            var (state, wizardExit) = await RunInstallAgentWizardAsync(
                api, ctx, workspaceTeam, credentialsFile, ctx.GetValue(agent)!, projectId,
                jsonMode: selection.Kind == JsonSelectionKind.Selected).ConfigureAwait(false);
            if (state is null || wizardExit != 0) return wizardExit;
            if (selection.Kind != JsonSelectionKind.Selected) return 0;
            return await new CliResultWriter(api.Invocation).WriteSuccessAsync(
                selection.Project(state, InstallAgentDescriptor.Cardinality)).ConfigureAwait(false);
        });
        return command;
    }

    private static async Task<(JsonObject? State, int Exit)> RunInstallAgentWizardAsync(
        MohistCliApi api,
        ParseResult ctx,
        Option<string?> workspaceTeam,
        Option<string?> credentialsFile,
        string agentReference,
        string projectId,
        bool jsonMode)
    {
        var agentId = await ResolveAgentIdAsync(api, projectId, agentReference).ConfigureAwait(false);
        if (agentId is null) return (null, 1);

        var team = await ResolveWorkspaceTeamAsync(api, ctx, workspaceTeam, "slack install-agent").ConfigureAwait(false);
        if (team is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));

        var optionsPath =
            $"/api/projects/{Uri.EscapeDataString(projectId)}/slack-manager/agents?workspaceTeamId={Uri.EscapeDataString(team)}";

        JsonObject? connection = null;
        JsonObject? managedApp = null;
        var nextAction = string.Empty;

        for (var step = 0; step < WizardStepBudget; step++)
        {
            var options = await api.GetDataOrPrintErrorAsync(optionsPath).ConfigureAwait(false);
            if (options.ExitCode != 0) return (null, options.ExitCode);
            var option = FindAgentOption(options.Data, agentId);
            if (option is null)
            {
                api.Error.WriteLine($"Agent '{agentReference}' is not available for Slack installation in workspace '{team}'.");
                return (null, 1);
            }

            connection = option["connection"] as JsonObject;
            managedApp = option["managedApp"] as JsonObject;
            nextAction = ValueOf(managedApp, "nextAction");

            if (connection is null)
            {
                var created = await api.PostAndReadAsync(
                    $"/api/projects/{Uri.EscapeDataString(projectId)}/slack-manager/apps",
                    new { agentId, workspaceTeamId = team, accessPolicy = "owner_only" }).ConfigureAwait(false);
                if (created.ExitCode != 0) return (null, created.ExitCode);
                connection = created.Data?["connection"] as JsonObject;
                managedApp = created.Data?["managedApp"] as JsonObject;
                nextAction = ValueOf(managedApp, "nextAction");
                await api.Error.WriteLineAsync($"Created the Slack Connection and Agent App for '{agentReference}'.").ConfigureAwait(false);
                if (managedApp is null)
                {
                    api.Error.WriteLine("The Agent App record was not returned; re-run `mo slack install-agent` to continue.");
                    return (null, 1);
                }
            }

            switch (nextAction)
            {
                case "create_child_app":
                {
                    var result = await api.PrintPostAsync(
                        ManagerPath(projectId, $"/connections/{Uri.EscapeDataString(ValueOf(connection, "id"))}/create"),
                        new { }).ConfigureAwait(false);
                    if (result != 0) return (null, result);
                    await api.Error.WriteLineAsync($"Agent App creation started for '{agentReference}'.").ConfigureAwait(false);
                    break;
                }

                case "reconcile_create":
                {
                    var result = await api.PrintPostAsync(
                        ManagerPath(projectId, $"/connections/{Uri.EscapeDataString(ValueOf(connection, "id"))}/reconcile-create"),
                        new { }).ConfigureAwait(false);
                    if (result != 0) return (null, result);
                    await api.Error.WriteLineAsync($"Agent App create outcome reconciled for '{agentReference}'.").ConfigureAwait(false);
                    break;
                }

                case "reconcile_delete":
                {
                    var result = await api.PrintPostAsync(
                        ManagerPath(projectId, $"/connections/{Uri.EscapeDataString(ValueOf(connection, "id"))}/reconcile-delete"),
                        new { }).ConfigureAwait(false);
                    if (result != 0) return (null, result);
                    await api.Error.WriteLineAsync($"Agent App delete outcome reconciled for '{agentReference}'.").ConfigureAwait(false);
                    break;
                }

                case "authorize_child_app":
                {
                    var connectionId = ValueOf(connection, "id");
                    var issued = await api.PostAndReadAsync(
                        ManagerPath(projectId, $"/connections/{Uri.EscapeDataString(connectionId)}/begin-authorization"),
                        new { }).ConfigureAwait(false);
                    if (issued.ExitCode != 0) return (null, issued.ExitCode);
                    var oauthState = ValueOf(issued.Data as JsonObject, "state");
                    var expiresAt = ValueOf(issued.Data as JsonObject, "expiresAt");
                    var progress = await api.PostAndReadAsync(
                        ManagerPath(projectId, $"/connections/{Uri.EscapeDataString(connectionId)}/authorization-progress"),
                        new { authorization = "awaiting_user" }).ConfigureAwait(false);
                    if (progress.ExitCode != 0) return (null, progress.ExitCode);

                    if (!string.IsNullOrWhiteSpace(ctx.GetValue(credentialsFile)))
                    {
                        var credentials = await ReadCredentialsAsync(api, ctx.GetValue(credentialsFile)).ConfigureAwait(false);
                        if (credentials is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));
                        // TODO(slack install-agent): submitting the run credentials to
                        // /connections/{id}/authorize also requires the Slack bot user id,
                        // which has no spec-confirmed CLI input yet. The credential file is
                        // validated here and reserved for that step.
                    }

                    if (!jsonMode) RenderInstallAuthorization(api, agentReference, team, oauthState, expiresAt, connectionId);
                    return (BuildInstallState(connection, managedApp, "authorize_child_app"), 0);
                }

                case "apply_manifest":
                case "bind_connection":
                case "configure_socket_credentials":
                case "wait_for_operation":
                {
                    if (!jsonMode) RenderInstallResume(api, agentReference, team, nextAction);
                    return (BuildInstallState(connection, managedApp, nextAction), 0);
                }

                case "deleted":
                {
                    if (!jsonMode)
                    {
                        api.Output.WriteLine($"The Agent App for '{agentReference}' was permanently deleted.");
                        api.Output.WriteLine("The Connection binding was removed; nothing else to install.");
                    }

                    return (BuildInstallState(connection, managedApp, nextAction), 0);
                }

                case "ready":
                {
                    if (!jsonMode) RenderInstallReady(api, agentReference, team, connection);
                    return (BuildInstallState(connection, managedApp, nextAction), 0);
                }

                default:
                {
                    if (!jsonMode) RenderInstallResume(api, agentReference, team, nextAction);
                    return (BuildInstallState(connection, managedApp, nextAction), 0);
                }
            }
        }

        api.Error.WriteLine("Slack install-agent did not converge; re-run `mo slack install-agent` to continue.");
        return (BuildInstallState(connection, managedApp, nextAction), 1);
    }

    private static JsonObject BuildInstallState(
        JsonObject? connection,
        JsonObject? managedApp,
        string? nextAction) => new()
    {
        ["connection"] = connection?.DeepClone(),
        ["managedApp"] = managedApp?.DeepClone(),
        ["nextAction"] = nextAction,
    };

    private static void RenderInstallAuthorization(
        MohistCliApi api,
        string agentReference,
        string team,
        string oauthState,
        string expiresAt,
        string connectionId)
    {
        api.Output.WriteLine($"Installation authorization is required for '{agentReference}' in workspace {team}.");
        WriteValue(api.Output, "  OAuth state", oauthState);
        WriteValue(api.Output, "  valid until", expiresAt);
        api.Output.WriteLine("  1. Open the Agent App in Slack App management and complete Install to Workspace (Allow).");
        api.Output.WriteLine("  2. Workspace admin approval may be required before the install is accepted.");
        api.Output.WriteLine("  3. Re-run `mo slack install-agent <agent>` to verify and finish the installation.");
        api.Error.WriteLine($"Connection {connectionId} is waiting for the Slack install confirmation.");
    }

    private static void RenderInstallResume(MohistCliApi api, string agentReference, string team, string nextAction)
    {
        api.Output.WriteLine($"Installation of '{agentReference}' in workspace {team} is in progress.");
        WriteValue(api.Output, "  next action", nextAction);
        api.Output.WriteLine("Re-run `mo slack install-agent <agent>` to continue from the current step.");
    }

    private static void RenderInstallReady(MohistCliApi api, string agentReference, string team, JsonObject? connection)
    {
        api.Output.WriteLine($"Agent '{agentReference}' is installed and ready in workspace {team}.");
        WriteValue(api.Output, "  connection", ValueOf(connection, "id"));
        WriteValue(api.Output, "  bot", ValueOf(connection, "botName"));
        api.Output.WriteLine("Invite the bot to a channel, or DM it to start a task.");
    }

    private static JsonObject? FindAgentOption(JsonNode? data, string agentId)
    {
        if (data is not JsonArray options) return null;
        foreach (var candidate in options.OfType<JsonObject>())
        {
            if (string.Equals(ValueOf(candidate, "agentId"), agentId, StringComparison.Ordinal)
                || string.Equals(ValueOf(candidate, "id"), agentId, StringComparison.Ordinal))
                return candidate;
        }

        return null;
    }

    private static Command BuildStatus(MohistCliApi api, OperatorCredentialProvider credentials)
    {
        var command = new Command("status", "Show the workspace Slack integration status");
        var workspaceTeam = new Option<string?>("--workspace-team", "--workspace-team-id")
        {
            Description = "Slack workspace team id",
        };
        var output = MohistCliCommands.OutputOption(StatusDescriptor);
        command.Options.Add(workspaceTeam);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, StatusDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(StatusDescriptor, selection);

            var team = ctx.GetValue(workspaceTeam);
            if (string.IsNullOrWhiteSpace(team))
                return CommandHelpHook.RenderUsageFailure(
                    ctx,
                    api.Error,
                    "slack status requires --workspace-team in non-interactive mode.");

            var headers = await GetOperatorHeadersAsync(api, credentials).ConfigureAwait(false);
            if (headers is null) return 1;
            return await api.PrintResourceAsync(
                $"/api/slack-manager/status?workspaceTeamId={Uri.EscapeDataString(team)}",
                StatusDescriptor,
                selection,
                api.WriteJsonDataAsync,
                headers).ConfigureAwait(false);
        });
        return command;
    }

    private static JsonSelection ResolveSelection(
        ParseResult context,
        Option<string?> output,
        ResourceDescriptor descriptor)
    {
        var explicitOutput = MohistCliCommands.OutputOptionState.Explicit;
        var value = context.GetValue(output);
        return JsonSelection.Parse(
            descriptor,
            explicitOutput,
            explicitOutput && string.Equals(value, "table", StringComparison.Ordinal) ? null : value);
    }

    private static string Path(string projectId, string suffix = "") =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/slack-connections{suffix}";

    private static string ManagerPath(string projectId, string suffix = "") =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/slack-manager{suffix}";

    private static async Task<(string? ProjectId, int Exit)> ProjectAsync(MohistCliApi api, string? project)
    {
        var resolved = await api.ResolveProject(project).ConfigureAwait(false);
        return (resolved.ProjectId, resolved.Exit);
    }

    private static async Task<string?> ResolveWorkspaceTeamAsync(
        MohistCliApi api,
        ParseResult ctx,
        Option<string?> workspaceTeam,
        string commandName)
    {
        var team = ctx.GetValue(workspaceTeam);
        if (!string.IsNullOrWhiteSpace(team)) return team.Trim();

        var accepted = await api.Invocation.RequirePromptAsync(
            "Slack workspace team id",
            "--workspace-team <team-id>",
            () => Task.FromResult(true)).ConfigureAwait(false);
        if (!accepted)
        {
            await api.Error.WriteLineAsync(
                $"slack {commandName} requires --workspace-team <team-id> when the workspace cannot be determined interactively.").ConfigureAwait(false);
            return null;
        }

        await api.Error.WriteAsync("Slack workspace team id: ").ConfigureAwait(false);
        var value = await api.Invocation.Input
            .ReadLineAsync(api.Invocation.CancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static async Task<ManagerEnrollmentFacts?> ResolveManagerFactsAsync(
        MohistCliApi api,
        ParseResult ctx,
        Option<string?> managerAppId,
        Option<string?> managerBotUserId,
        Option<string?> managerCredentialRef)
    {
        var appId = ctx.GetValue(managerAppId);
        var botUserId = ctx.GetValue(managerBotUserId);
        var credentialRef = ctx.GetValue(managerCredentialRef);

        if (string.IsNullOrWhiteSpace(appId))
            appId = await PromptLineAsync(api, "Mohist App id", "--manager-app-id <id>").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(botUserId))
            botUserId = await PromptLineAsync(api, "Mohist App bot user id", "--manager-bot-user-id <id>").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credentialRef))
            credentialRef = await PromptLineAsync(api, "Mohist App credential reference", "--manager-credential-ref <ref>").ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(botUserId) || string.IsNullOrWhiteSpace(credentialRef))
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(appId)) missing.Add("--manager-app-id");
            if (string.IsNullOrWhiteSpace(botUserId)) missing.Add("--manager-bot-user-id");
            if (string.IsNullOrWhiteSpace(credentialRef)) missing.Add("--manager-credential-ref");
            api.Error.WriteLine($"slack setup requires {string.Join(", ", missing)} for the enrollment step in non-interactive mode.");
            return null;
        }

        return new ManagerEnrollmentFacts(appId.Trim(), botUserId.Trim(), credentialRef.Trim());
    }

    private static async Task<string?> PromptLineAsync(MohistCliApi api, string requirement, string explicitInput)
    {
        var accepted = await api.Invocation.RequirePromptAsync(
            requirement,
            explicitInput,
            () => Task.FromResult(true)).ConfigureAwait(false);
        if (!accepted) return null;
        await api.Error.WriteAsync($"{requirement}: ").ConfigureAwait(false);
        var line = await api.Invocation.Input
            .ReadLineAsync(api.Invocation.CancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }

    private sealed record ManagerEnrollmentFacts(string AppId, string BotUserId, string CredentialRef);

    private static async Task<bool> ReadConfigurationTokenAsync(MohistCliApi api, string path)
    {
        try
        {
            if (!IsProtectedFile(api.FileSystem, path))
                throw new InvalidOperationException("Configuration token file must be a regular, non-symlink file readable and writable only by the current user.");
            var json = await api.FileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("configurationToken", out var token)
                || !root.TryGetProperty("configurationRefreshToken", out var refresh)
                || token.ValueKind != JsonValueKind.String
                || refresh.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString())
                || string.IsNullOrWhiteSpace(refresh.GetString()))
                throw new InvalidOperationException("Configuration token file must contain exactly non-empty configurationToken and configurationRefreshToken strings.");
            await api.Error.WriteLineAsync("Configuration token pair accepted for workspace App provisioning.").ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            api.Error.WriteLine(ex.Message);
            return false;
        }
    }

    private static Command BuildClaimOwner(MohistCliApi api)
    {
        var command = new Command("claim-owner", "Generate a one-time Slack owner claim code");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var result = await api.PostAndReadAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/claim-owner"), new { });
            return result.ExitCode;
        });
        return command;
    }

    private static Command BuildTransferOwner(MohistCliApi api)
    {
        var command = new Command("transfer-owner", "Generate a one-time Slack owner transfer code");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var result = await api.PostAndReadAsync(
                Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/transfer-owner"),
                new { });
            return result.ExitCode;
        });
        return command;
    }

    private static Command BuildDisable(MohistCliApi api) => BuildDesiredStateCommand(api, "disable", "Disable");

    private static Command BuildEnable(MohistCliApi api) => BuildDesiredStateCommand(api, "enable", "Enable");

    private static Command BuildDesiredStateCommand(MohistCliApi api, string operation, string desiredState)
    {
        var command = new Command(operation, $"{desiredState} a Slack Connection");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var result = await api.PostAndReadAsync(
                Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/{operation}"),
                new { });
            return result.ExitCode;
        });
        return command;
    }

    private static Command BuildView(MohistCliApi api)
    {
        var command = new Command("view", "View a Slack Connection");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var result = await api.GetDataOrPrintErrorAsync(
                Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/diagnostic"));
            if (result.ExitCode != 0) return result.ExitCode;
            return RenderDiagnostic(api.Output, result.Data);
        });
        return command;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var command = new Command("list", "List Slack Connections");
        var agent = new Argument<string?>("agent")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Limit status to an Agent name or id.",
        };
        var workspaceTeam = new Option<string?>("--workspace-team");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(agent);
        command.Options.Add(workspaceTeam);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var team = ctx.GetValue(workspaceTeam);
            var agentReference = ctx.GetValue(agent);
            var agentId = string.IsNullOrWhiteSpace(agentReference)
                ? null
                : await ResolveAgentIdAsync(api, projectId, agentReference);
            if (!string.IsNullOrWhiteSpace(agentReference) && agentId is null) return 1;
            var result = await api.GetDataOrPrintErrorAsync(
                string.IsNullOrWhiteSpace(team)
                    ? Path(projectId, string.IsNullOrWhiteSpace(agentId)
                        ? ""
                        : $"?agentId={Uri.EscapeDataString(agentId)}")
                    : $"{ManagerPath(projectId, "/agents")}?workspaceTeamId={Uri.EscapeDataString(team)}");
            if (result.ExitCode != 0) return result.ExitCode;
            if (string.IsNullOrWhiteSpace(team))
                RenderConnectionList(api.Output, result.Data);
            else
                RenderManagerAgentList(api.Output, result.Data, agentReference);
            return 0;
        });
        return command;
    }

    private static Command BuildReconcileCreate(MohistCliApi api) => BuildManagerOperation(
        api, "reconcile-create", "Reconcile an unknown managed Agent App create", "/reconcile-create");

    private static Command BuildReconcileDelete(MohistCliApi api) => BuildManagerOperation(
        api, "reconcile-delete", "Reconcile an unknown managed Agent App delete", "/reconcile-delete");

    private static Command BuildRemoveBinding(MohistCliApi api) => BuildManagerOperation(
        api, "remove-binding", "Remove the Mohist Connection binding while retaining Agent App facts", "/remove-binding");

    private static Command BuildManagerOperation(
        MohistCliApi api,
        string name,
        string description,
        string suffix)
    {
        var command = new Command(name, description);
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            return await api.PrintPostAsync(
                ManagerPath(projectId, $"/connections/{Uri.EscapeDataString(ctx.GetValue(id)!)}{suffix}"),
                new { });
        });
        return command;
    }

    private static Command BuildPermanentDelete(MohistCliApi api)
    {
        var command = new Command("permanent-delete", "Permanently delete the managed Agent App after its Connection binding was removed");
        var id = new Argument<string>("connection-id");
        var confirmation = new Option<string?>("--confirm")
        {
            Description = "Must be exactly DELETE. This operation cannot be inferred from a normal remove-binding.",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(confirmation);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            if (!string.Equals(ctx.GetValue(confirmation), "DELETE", StringComparison.Ordinal))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--confirm DELETE is required for permanent-delete.");
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            return await api.PrintPostAsync(
                ManagerPath(projectId, $"/connections/{Uri.EscapeDataString(ctx.GetValue(id)!)}/permanent-delete"),
                new { confirmation = "DELETE" });
        });
        return command;
    }

    private static Command BuildListDeliveries(MohistCliApi api)
    {
        var command = new Command("deliveries", "List outbound Slack deliveries for a Connection (delivery_uncertain rows are surfaced with their LastError)");
        var id = new Argument<string>("connection-id");
        var onlyUncertain = new Option<bool>("--only-uncertain")
        {
            Description = "Restrict output to rows in the Delivery uncertain state.",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(onlyUncertain);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var result = await api.GetDataOrPrintErrorAsync(
                Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/deliveries"));
            if (result.ExitCode != 0) return result.ExitCode;
            RenderDeliveries(api.Output, result.Data, ctx.GetValue(onlyUncertain));
            return 0;
        });
        return command;
    }

    private static Command BuildResendDelivery(MohistCliApi api)
    {
        var command = new Command("resend-delivery", "Re-queue a Delivery uncertain row for another send attempt (warns about possible duplication; inspect the authoritative execution result before committing)");
        var id = new Argument<string>("connection-id");
        var deliveryId = new Argument<string>("delivery-id")
        {
            Description = "Outbox row id returned by `mo slack deliveries` (state: delivery_uncertain).",
        };
        var yes = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the duplicate-warning prompt in non-interactive mode.",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Arguments.Add(deliveryId);
        command.Options.Add(yes);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;

            var connectionPath = Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}");
            var lookup = await api.GetDataOrPrintErrorAsync(connectionPath + "/deliveries");
            if (lookup.ExitCode != 0) return lookup.ExitCode;
            var row = FindDelivery(lookup.Data, ctx.GetValue(deliveryId)!);
            if (row is null)
            {
                api.Error.WriteLine($"Delivery '{ctx.GetValue(deliveryId)}' not found on Connection '{ctx.GetValue(id)}'.");
                return CliExitCode.For(CliExitOutcome.OperationFailure);
            }
            var state = ValueOf(row, "state");
            if (!string.Equals(state, "delivery_uncertain", StringComparison.Ordinal))
            {
                api.Error.WriteLine(
                    $"Delivery '{ctx.GetValue(deliveryId)}' is in state '{state}'; only Delivery uncertain rows can be resent.");
                return CliExitCode.For(CliExitOutcome.OperationFailure);
            }

            var lastError = ValueOf(row, "lastError");
            var dispatchRef = ValueOf(row, "dispatchRef");
            var kind = ValueOf(row, "kind");

            api.Output.WriteLine("Manual resend of Delivery uncertain row");
            WriteValue(api.Output, "  delivery id", ctx.GetValue(deliveryId)!);
            WriteValue(api.Output, "  kind", kind);
            WriteValue(api.Output, "  dispatch ref", dispatchRef);
            WriteValue(api.Output, "  reason held", lastError);

            api.Error.WriteLine("Slack may have already delivered this reply silently. Resending can produce a duplicate Slack message even though the underlying AgentJob/AgentTurn result is unchanged.");
            api.Error.WriteLine("Inspect the authoritative execution result for the dispatch ref before committing.");

            if (!ctx.GetValue(yes))
            {
                if (!api.Invocation.PromptsEnabled)
                {
                    api.Error.WriteLine("--yes is required to confirm this resend in non-interactive mode.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                api.Error.Write("Resend anyway? [y/N] ");
                var line = await api.Invocation.Input
                    .ReadLineAsync(api.Invocation.CancellationToken).ConfigureAwait(false);
                var confirmed = !string.IsNullOrEmpty(line)
                    && line.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase);
                if (!confirmed)
                {
                    api.Error.WriteLine("Aborted.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }
            }

            return await api.PrintPostAsync(
                $"{connectionPath}/deliveries/{Uri.EscapeDataString(ctx.GetValue(deliveryId)!)}/resend",
                new { });
        });
        return command;
    }

    private static JsonObject? FindDelivery(JsonNode? data, string deliveryId)
    {
        if (data is not JsonObject obj) return null;
        var entries = obj["entries"] as JsonArray;
        if (entries is null) return null;
        foreach (var candidate in entries.OfType<JsonObject>())
        {
            if (string.Equals(ValueOf(candidate, "id"), deliveryId, StringComparison.Ordinal))
                return candidate;
        }
        return null;
    }

    private static Command BuildClearGap(MohistCliApi api)
    {
        var command = new Command("clear-gap", "Dismiss a possible-messages-missed notice after a long adapter outage");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;

            return await api.PrintPostAsync(
                Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/clear-gap"),
                new { });
        });
        return command;
    }

    private static int RenderDiagnostic(TextWriter output, JsonNode? data)
    {
        if (data is not JsonObject diagnostic)
        {
            output.WriteLine(data?.ToJsonString(MohistCliApi.JsonOutputOptions) ?? "(no diagnostic data)");
            return 0;
        }

        output.WriteLine("Connection diagnostic");
        WriteValue(output, "  primary state", ValueOf(diagnostic, "primaryState"));
        WriteValue(output, "  reason", ValueOf(diagnostic, "reason"));
        WriteValue(output, "  next action", ValueOf(diagnostic, "nextAction"));

        if (diagnostic["facts"] is JsonObject factsForGap
            && factsForGap["offlineGapAt"] is JsonValue gapNode
            && gapNode.TryGetValue<string>(out var gapValue)
            && !string.IsNullOrEmpty(gapValue))
        {
            output.WriteLine();
            output.WriteLine("Possible messages missed");
            output.WriteLine("  The Slack adapter was offline long enough that Slack may have discarded");
            output.WriteLine("  events from the outage window. Resend any critical delegations.");
            WriteValue(output, "  offline gap at", gapValue);
        }

        output.WriteLine();
        output.WriteLine("Supporting facts");
        if (diagnostic["facts"] is not JsonObject facts)
        {
            output.WriteLine("  (none)");
            return 0;
        }

        foreach (var key in new[]
        {
            "setupProgress", "desiredState", "connectionHealth", "healthReason",
            "credentialStatus", "adapterOnline", "ownerAvailability", "agentReadiness",
            "offlineGapAt",
        })
            WriteValue(output, $"  {key}", ValueOf(facts, key));

        if (facts["identity"] is JsonObject identity)
        {
            output.WriteLine("  identity:");
            foreach (var key in new[]
            {
                "verificationStatus", "verifiedBotName", "botName", "agentName",
                "verifiedBotIconUrl", "avatarHash", "driftKinds",
            })
                WriteValue(output, $"    {key}", ValueOf(identity, key));
        }

        return 0;
    }

    private static void RenderDeliveries(TextWriter output, JsonNode? data, bool onlyUncertain)
    {
        if (data is not JsonObject root || root["entries"] is not JsonArray entries || entries.Count == 0)
        {
            output.WriteLine(onlyUncertain ? "No deliveries are in Delivery uncertain." : "No deliveries.");
            return;
        }

        var rows = entries.OfType<JsonObject>()
            .Where(row => !onlyUncertain || string.Equals(ValueOf(row, "state"), "delivery_uncertain", StringComparison.Ordinal))
            .ToList();
        if (rows.Count == 0)
        {
            output.WriteLine("No deliveries are in Delivery uncertain.");
            return;
        }

        var headers = new[] { "id", "state", "kind", "attempts", "reason" };
        var widths = new[] { 32, 18, 22, 9, 60 };
        output.WriteLine(string.Join("  ", headers.Select((header, index) => header.PadRight(widths[index]))).TrimEnd());
        foreach (var row in rows)
        {
            var cells = new[]
            {
                Truncate(ValueOf(row, "id"), widths[0]),
                Truncate(ValueOf(row, "state"), widths[1]),
                Truncate(ValueOf(row, "kind"), widths[2]),
                Truncate(ValueOf(row, "attemptCount"), widths[3]),
                Truncate(ValueOf(row, "lastError"), widths[4]),
            };
            output.WriteLine(string.Join("  ", cells.Select((cell, index) => cell.PadRight(widths[index]))).TrimEnd());
        }
    }

    private static void RenderConnectionList(TextWriter output, JsonNode? data)
    {
        if (data is not JsonArray rows || rows.Count == 0)
        {
            output.WriteLine("No Slack Connections");
            return;
        }

        var headers = new[] { "id", "bot", "primary state", "next action" };
        var widths = new[] { 24, 24, 20, 42 };
        output.WriteLine(string.Join("  ", headers.Select((header, index) => header.PadRight(widths[index]))).TrimEnd());
        foreach (var row in rows.OfType<JsonObject>())
        {
            var state = StoredPrimaryState(row);
            var cells = new[]
            {
                Truncate(ValueOf(row, "id"), widths[0]),
                Truncate(ValueOf(row, "botName"), widths[1]),
                Truncate(state.State, widths[2]),
                Truncate(state.NextAction, widths[3]),
            };
            output.WriteLine(string.Join("  ", cells.Select((cell, index) => cell.PadRight(widths[index]))).TrimEnd());
        }
    }

    private static void RenderManagerAgentList(TextWriter output, JsonNode? data, string? agentReference)
    {
        if (data is not JsonArray rows)
        {
            output.WriteLine(data?.ToJsonString(MohistCliApi.JsonOutputOptions) ?? "(no Agent App data)");
            return;
        }

        var selected = rows.OfType<JsonObject>()
            .Where(row => string.IsNullOrWhiteSpace(agentReference)
                || string.Equals(ValueOf(row, "agentId"), agentReference, StringComparison.Ordinal)
                || string.Equals(ValueOf(row, "agentName"), agentReference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (selected.Count == 0)
        {
            output.WriteLine("No active Agent App candidates");
            return;
        }

        var headers = new[] { "agent", "preview bot", "app lifecycle", "authorization", "next action" };
        var widths = new[] { 28, 28, 18, 18, 28 };
        output.WriteLine(string.Join("  ", headers.Select((header, index) => header.PadRight(widths[index]))).TrimEnd());
        foreach (var row in selected)
        {
            var managedApp = row["managedApp"] as JsonObject;
            var preview = row["preview"] as JsonObject;
            var cells = new[]
            {
                Truncate(ValueOf(row, "agentName"), widths[0]),
                Truncate(ValueOf(preview, "botName"), widths[1]),
                Truncate(ValueOf(managedApp, "appLifecycle"), widths[2]),
                Truncate(ValueOf(managedApp, "authorization"), widths[3]),
                Truncate(ValueOf(managedApp, "nextAction"), widths[4]),
            };
            output.WriteLine(string.Join("  ", cells.Select((cell, index) => cell.PadRight(widths[index]))).TrimEnd());
        }
    }

    private static (string State, string NextAction) StoredPrimaryState(JsonObject connection)
    {
        var setup = ValueOf(connection, "setupProgress");
        if (!string.Equals(setup, "complete", StringComparison.Ordinal))
            return ("setup_incomplete", "Advance the current setup step.");

        var health = ValueOf(connection, "connectionHealth");
        var reason = ValueOf(connection, "healthReason");
        if (string.Equals(health, "unhealthy", StringComparison.Ordinal)
            && ContainsAny(reason, "token", "scope", "credential", "invalid_auth", "app and bot", "missing required"))
            return ("credentials_invalid", "Rotate credentials.");

        if (string.Equals(health, "unhealthy", StringComparison.Ordinal))
            return ("service_offline", "Start mohist-slack / check Slack connectivity.");

        if (string.Equals(health, "degraded", StringComparison.Ordinal)
            && (ContainsAny(reason, "slack_outbox_backpressured", "slack_inbox_backpressured", "backpressured")))
            return ("backpressured", "Wait for the backlog to drain / retry input shortly.");

        if (string.Equals(ValueOf(connection, "agentReadiness"), "needs_setup", StringComparison.Ordinal))
            return ("agent_needs_setup", "Configure Agent runtime/model.");

        if (string.Equals(ValueOf(connection, "desiredState"), "disabled", StringComparison.Ordinal))
            return ("disabled", "Enable the Connection.");

        if (HasStoredIdentityDrift(connection))
            return ("identity_drift", "Review the name/avatar difference.");

        return ("healthy", "No action needed.");
    }

    private static bool HasStoredIdentityDrift(JsonObject connection)
    {
        var verifiedName = ValueOf(connection, "verifiedBotName");
        var botName = ValueOf(connection, "botName");
        var verifiedIcon = ValueOf(connection, "verifiedBotIconUrl");
        var avatarHash = ValueOf(connection, "avatarHash");
        return (!string.IsNullOrEmpty(verifiedName) && !string.Equals(verifiedName, botName, StringComparison.Ordinal))
            || (!string.IsNullOrEmpty(verifiedIcon) && !string.Equals(verifiedIcon, avatarHash, StringComparison.Ordinal));
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string ValueOf(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null) return "";
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return text ?? "";
        return value.ToJsonString(MohistCliApi.JsonCompactOutputOptions);
    }

    private static void WriteValue(TextWriter output, string key, string value) =>
        output.WriteLine(string.IsNullOrEmpty(value) ? $"{key}:" : $"{key}: {value}");

    private static string Truncate(string value, int softCap)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var firstLine = value.AsSpan();
        var newline = firstLine.IndexOf('\n');
        if (newline >= 0) firstLine = firstLine[..newline];
        if (firstLine.Length <= softCap) return firstLine.ToString();
        if (softCap <= 1) return firstLine[..softCap].ToString();
        return string.Concat(firstLine[..(softCap - 1)], "...");
    }

    private const string OwnerOnlyPolicy = "owner_only";
    private const string AllowlistPolicy = "allowlist";
    private const string AnyonePolicy = "anyone";

    private const string AnyoneDisclosure =
        "Invoking this Bot grants channel members the Agent's configured repository-write, tool, and credential authority.";

    private static Command BuildEdit(MohistCliApi api)
    {
        var command = new Command("edit", "Edit Slack Connection presentation fields and channel access policy");
        var id = new Argument<string>("connection-id");
        var botName = new Option<string?>("--bot-name");
        var avatar = new Option<string?>("--avatar-hash");
        var accessPolicy = new Option<string?>("--access-policy")
        {
            Description = "Channel access policy: owner_only, allowlist, or anyone. Routes to Manage access (separate from --bot-name/--avatar-hash).",
        };
        var allowMember = new Option<string[]?>("--allow-member")
        {
            Description = "Slack member id allowed under allowlist. Repeatable; replaces the full list excluding the Owner. Only valid with --access-policy allowlist.",
            AllowMultipleArgumentsPerToken = true,
        };
        var yes = new Option<bool>("--yes", "-y")
        {
            Description = "Bypass the Anyone execution-authority disclosure (required in non-interactive mode for --access-policy anyone).",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(botName);
        command.Options.Add(avatar);
        command.Options.Add(accessPolicy);
        command.Options.Add(allowMember);
        command.Options.Add(yes);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var policy = ctx.GetValue(accessPolicy);
            var members = ctx.GetValue(allowMember);
            var hasAccess = policy is not null || members is { Length: > 0 };
            var hasPresentation = ctx.GetValue(botName) is not null || ctx.GetValue(avatar) is not null;
            if (!hasAccess && !hasPresentation)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "Specify --access-policy/--allow-member or --bot-name/--avatar-hash.");

            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;

            var connectionPath = Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}");

            if (hasAccess)
            {
                var accessExit = await ManageAccessAsync(api, ctx, policy, members, ctx.GetValue(yes), connectionPath);
                if (accessExit != 0) return accessExit;
            }

            if (hasPresentation)
            {
                return await api.PrintPatchAsync(connectionPath, new
                {
                    botName = ctx.GetValue(botName),
                    avatarHash = ctx.GetValue(avatar),
                });
            }
            return 0;
        });
        return command;
    }

    private static async Task<int> ManageAccessAsync(
        MohistCliApi api,
        ParseResult ctx,
        string? accessPolicy,
        string[]? allowMember,
        bool yes,
        string connectionPath)
    {
        var policy = accessPolicy?.Trim().ToLowerInvariant();
        var members = allowMember ?? Array.Empty<string>();
        var membersPresent = members.Length > 0;

        if (policy is not null && policy is not (OwnerOnlyPolicy or AllowlistPolicy or AnyonePolicy))
            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--access-policy must be owner_only, allowlist, or anyone.");

        if (membersPresent && policy is null)
            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--allow-member requires --access-policy allowlist.");

        if (membersPresent && policy is not AllowlistPolicy)
            return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--allow-member may be supplied only with --access-policy allowlist.");

        if (policy is AnyonePolicy)
        {
            if (!yes && !api.Invocation.PromptsEnabled)
            {
                await api.Error.WriteLineAsync(AnyoneDisclosure).ConfigureAwait(false);
                await api.Error.WriteLineAsync("--yes is required to confirm this change in non-interactive mode.").ConfigureAwait(false);
                return CliExitCode.For(CliExitOutcome.OperationFailure);
            }

            if (!yes)
            {
                await api.Error.WriteLineAsync(AnyoneDisclosure).ConfigureAwait(false);
                await api.Error.WriteAsync("Proceed with anyone? [y/N] ").ConfigureAwait(false);
                var line = await api.Invocation.Input
                    .ReadLineAsync(api.Invocation.CancellationToken).ConfigureAwait(false);
                var confirmed = !string.IsNullOrEmpty(line)
                    && line.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase);
                if (!confirmed)
                {
                    await api.Error.WriteLineAsync("Aborted.").ConfigureAwait(false);
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }
            }
        }

        return await api.PrintPostAsync($"{connectionPath}/manage-access", new
        {
            accessPolicy = policy ?? OwnerOnlyPolicy,
            allowMembers = members,
        });
    }

    private static async Task<string?> ResolveAgentIdAsync(MohistCliApi api, string projectId, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            api.Error.WriteLine("agent is required");
            return null;
        }
        var agents = await api.GetDataAsync($"/api/projects/{Uri.EscapeDataString(projectId)}/agents?all=true");
        if (agents is JsonArray list)
        {
            foreach (var item in list.OfType<JsonObject>())
            {
                if (string.Equals(item["id"]?.GetValue<string>(), reference, StringComparison.Ordinal)
                    || string.Equals(item["name"]?.GetValue<string>(), reference, StringComparison.Ordinal))
                    return item["id"]?.GetValue<string>();
            }
        }
        api.Error.WriteLine($"Agent '{reference}' not found.");
        return null;
    }

    private static async Task<CredentialPair?> ReadCredentialsAsync(MohistCliApi api, string? path)
    {
        try
        {
            string json;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!IsProtectedFile(api.FileSystem, path))
                    throw new InvalidOperationException("Credential file must be a regular, non-symlink file readable and writable only by the current user.");
                json = await api.FileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            else
            {
                if (!await api.Invocation.RequirePromptAsync("Slack credentials", "--credentials-file <path>", () => Task.FromResult(true)).ConfigureAwait(false))
                    return null;
                await api.Error.WriteLineAsync("App token:").ConfigureAwait(false);
                var appToken = await api.Invocation.Terminal.ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken).ConfigureAwait(false);
                await api.Error.WriteLineAsync("Bot token:").ConfigureAwait(false);
                var botToken = await api.Invocation.Terminal.ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(appToken) || string.IsNullOrWhiteSpace(botToken))
                    throw new InvalidOperationException("Both Slack credentials are required.");
                return new CredentialPair(appToken, botToken);
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("appToken", out var app) || !root.TryGetProperty("botToken", out var bot)
                || app.ValueKind != JsonValueKind.String || bot.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(app.GetString()) || string.IsNullOrWhiteSpace(bot.GetString()))
                throw new InvalidOperationException("Credential file must contain exactly non-empty appToken and botToken strings.");
            return new CredentialPair(app.GetString()!, bot.GetString()!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            api.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private static async Task<string?> ReadManagerCredentialAsync(MohistCliApi api, string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (!await api.Invocation.RequirePromptAsync(
                        "Manager Bot credential",
                        "--credentials-file <path>",
                        () => Task.FromResult(true)).ConfigureAwait(false))
                    return null;
                await api.Error.WriteLineAsync("Manager Bot credential:").ConfigureAwait(false);
                var hidden = await api.Invocation.Terminal
                    .ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(hidden))
                    throw new InvalidOperationException("A non-empty Manager Bot credential is required.");
                return hidden.Trim();
            }

            if (!IsProtectedFile(api.FileSystem, path))
                throw new InvalidOperationException("Credential file must be a regular, non-symlink file readable and writable only by the current user.");
            var json = await api.FileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("botToken", out var bot)
                || bot.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(bot.GetString()))
                throw new InvalidOperationException("Credential file must contain exactly one non-empty botToken string.");
            return bot.GetString()!.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            api.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private static bool IsProtectedFile(IFileSystem fileSystem, string path)
    {
        if (!fileSystem.Exists(path) || fileSystem.DirectoryExists(path)) return false;
        return !fileSystem.IsSymbolicLink(path) && fileSystem.IsUserOnlyFile(path);
    }

    private static async Task<IReadOnlyDictionary<string, string>?> GetOperatorHeadersAsync(
        MohistCliApi api,
        OperatorCredentialProvider credentials)
    {
        var baseAddress = api.Http.BaseAddress;
        if (baseAddress is null || !baseAddress.IsLoopback)
        {
            api.Error.WriteLine(
                $"Slack setup and status require a loopback Mohist server URL; refusing to send the operator credential to '{baseAddress}'.");
            return null;
        }

        try
        {
            var token = await credentials.GetAsync().ConfigureAwait(false);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OperatorCredentialProvider.HeaderName] = token,
            };
        }
        catch (InvalidOperationException ex)
        {
            api.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private sealed record CredentialPair(string AppToken, string BotToken);
}
