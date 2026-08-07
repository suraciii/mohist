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
        ["enrollment", "phase", "managerAppId", "installUrl", "nextAction", "errorClass"]);

    private static readonly ResourceDescriptor StatusDescriptor = new(
        ResourceCardinality.Single,
        ["enrollment", "connections", "managedApps", "nextAction"]);

    private static readonly ResourceDescriptor InstallAgentDescriptor = new(
        ResourceCardinality.Single,
        ["enrollment", "connection", "managedApp", "installUrl", "nextAction", "errorClass"]);

    private const int WizardStepBudget = 8;

    public static Command Build(MohistCliApi api, CliCredentialProvider credentials)
    {
        var group = new Command("slack", "Manage Slack integrations");
        group.Subcommands.Add(BuildSetup(api, credentials));
        group.Subcommands.Add(BuildStatus(api, credentials));
        group.Subcommands.Add(BuildInstallAgent(api, credentials));
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
        group.Subcommands.Add(BuildMessage(api));
        return group;
    }

    private static Command BuildSetup(MohistCliApi api, CliCredentialProvider credentials)
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
            Description = "Protected JSON file containing exactly non-empty botToken and appToken strings. Re-supplying it on a ready workspace rotates the stored runtime credentials.",
        };
        var output = MohistCliCommands.OutputOption(SetupDescriptor);
        command.Options.Add(workspaceTeam);
        command.Options.Add(configurationTokenFile);
        command.Options.Add(credentialsFile);
        command.Options.Add(output);
        command.SetAction(async ctx =>
        {
            var selection = ResolveSelection(ctx, output, SetupDescriptor);
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(SetupDescriptor, selection);

            var (state, exit) = await RunSetupWizardAsync(
                api, credentials, ctx, workspaceTeam, configurationTokenFile, credentialsFile,
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
        CliCredentialProvider credentials,
        ParseResult ctx,
        Option<string?> workspaceTeam,
        Option<string?> configurationTokenFile,
        Option<string?> credentialsFile,
        bool jsonMode)
    {
        var team = await ResolveWorkspaceTeamAsync(api, ctx, workspaceTeam, "slack setup").ConfigureAwait(false);
        if (team is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));

        var headers = await GetOperatorHeadersAsync(api, credentials).ConfigureAwait(false);
        if (headers is null) return (null, 1);

        JsonObject? progress = null;
        var configurationSupplied = false;
        var runtimeSupplied = false;

        for (var step = 0; step < WizardStepBudget; step++)
        {
            var (current, exit) = await ReadSetupProgressAsync(api, team, headers).ConfigureAwait(false);
            if (exit != 0) return (null, exit);
            if (current is null) return (null, 1);
            progress = current;

            var nextAction = ValueOf(progress, "nextAction");
            if (string.IsNullOrEmpty(nextAction)) nextAction = SlackSetupActionSupplyConfiguration;

            if (nextAction == SlackSetupActionSupplyConfiguration && !configurationSupplied)
            {
                var pair = await ReadConfigurationTokenPairAsync(api, ctx.GetValue(configurationTokenFile)).ConfigureAwait(false);
                if (pair is null) return (null, await FailSetupInputAsync(api, team, progress, nextAction).ConfigureAwait(false));
                var posted = await PostSetupConfigurationAsync(api, team, pair, headers).ConfigureAwait(false);
                if (posted.Failure is not null)
                    return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(posted.Failure).ConfigureAwait(false));
                configurationSupplied = true;
                await api.Error.WriteLineAsync($"Configuration credentials accepted for {team}.").ConfigureAwait(false);
                continue;
            }

            if (nextAction == SlackSetupActionSupplyRuntimeCredentials && !runtimeSupplied)
            {
                var pair = await ReadRuntimeCredentialsAsync(api, ctx.GetValue(credentialsFile)).ConfigureAwait(false);
                if (pair is null) return (null, await FailSetupInputAsync(api, team, progress, nextAction).ConfigureAwait(false));
                var posted = await PostSetupRuntimeCredentialsAsync(api, team, pair, headers).ConfigureAwait(false);
                if (posted.Failure is not null)
                    return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(posted.Failure).ConfigureAwait(false));
                runtimeSupplied = true;
                await api.Error.WriteLineAsync($"Runtime credentials accepted for {team}.").ConfigureAwait(false);
                continue;
            }

            if (nextAction == SlackSetupActionReady && !runtimeSupplied && !string.IsNullOrWhiteSpace(ctx.GetValue(credentialsFile)))
            {
                var pair = await ReadRuntimeCredentialsAsync(api, ctx.GetValue(credentialsFile)).ConfigureAwait(false);
                if (pair is null) return (null, await FailSetupInputAsync(api, team, progress, nextAction).ConfigureAwait(false));
                var posted = await PostSetupRuntimeCredentialsAsync(api, team, pair, headers).ConfigureAwait(false);
                if (posted.Failure is not null)
                    return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(posted.Failure).ConfigureAwait(false));
                runtimeSupplied = true;
                await api.Error.WriteLineAsync($"Runtime credentials rotated for {team}.").ConfigureAwait(false);
                continue;
            }

            if (nextAction == SlackSetupActionReady && !configurationSupplied && !string.IsNullOrWhiteSpace(ctx.GetValue(configurationTokenFile)))
            {
                var pair = await ReadConfigurationTokenPairAsync(api, ctx.GetValue(configurationTokenFile)).ConfigureAwait(false);
                if (pair is null) return (null, await FailSetupInputAsync(api, team, progress, nextAction).ConfigureAwait(false));
                var posted = await PostSetupConfigurationAsync(api, team, pair, headers).ConfigureAwait(false);
                if (posted.Failure is not null)
                    return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(posted.Failure).ConfigureAwait(false));
                configurationSupplied = true;
                await api.Error.WriteLineAsync($"Configuration credentials rotated for {team}.").ConfigureAwait(false);
                continue;
            }

            return (BuildSetupState(progress), RenderSetupStep(api, team, progress, jsonMode));
        }

        api.Error.WriteLine("Slack setup did not converge; re-run `mo slack setup` to continue.");
        return (BuildSetupState(progress ?? new JsonObject()), 1);
    }

    private static async Task<int> FailSetupInputAsync(MohistCliApi api, string team, JsonObject progress, string nextAction)
    {
        await api.Error.WriteLineAsync($"Slack workspace {team}: next action is '{nextAction}'.").ConfigureAwait(false);
        var installUrl = ValueOf(progress, "installUrl");
        if (!string.IsNullOrEmpty(installUrl))
            await api.Error.WriteLineAsync($"Complete the Slack-side App install first: {installUrl}").ConfigureAwait(false);
        await api.Error.WriteLineAsync("Re-run `mo slack setup` to continue from the current step.").ConfigureAwait(false);
        return CliExitCode.For(CliExitOutcome.UsageFailure);
    }

    private static int RenderSetupStep(MohistCliApi api, string team, JsonObject progress, bool jsonMode)
    {
        var nextAction = ValueOf(progress, "nextAction");
        if (string.Equals(nextAction, SlackSetupActionReady, StringComparison.Ordinal))
        {
            if (!jsonMode) RenderSetupReady(api, team, progress);
            return 0;
        }

        var errorClass = ValueOf(progress, "errorClass");
        if (!jsonMode)
        {
            RenderSetupNextAction(api, team, nextAction, progress);
            if (!string.IsNullOrEmpty(errorClass))
                api.Error.WriteLine($"The last setup step failed ({errorClass}); fix the cause and re-run `mo slack setup`.");
        }

        return string.IsNullOrEmpty(errorClass) ? 0 : 1;
    }

    private static JsonObject BuildSetupState(JsonObject progress) => new()
    {
        ["enrollment"] = EnrollmentState(progress),
        ["phase"] = NullIfEmpty(ValueOf(progress, "phase")),
        ["managerAppId"] = NullIfEmpty(ValueOf(progress, "managerAppId")),
        ["installUrl"] = NullIfEmpty(ValueOf(progress, "installUrl")),
        ["nextAction"] = ValueOf(progress, "nextAction"),
        ["errorClass"] = NullIfEmpty(ValueOf(progress, "errorClass")),
    };

    private static JsonObject? EnrollmentState(JsonObject progress)
    {
        var enrollmentId = ValueOf(progress, "enrollmentId");
        return string.IsNullOrEmpty(enrollmentId) ? null : new JsonObject { ["id"] = enrollmentId };
    }

    private static void RenderSetupReady(MohistCliApi api, string team, JsonObject progress)
    {
        api.Output.WriteLine($"Slack workspace {team} is ready.");
        WriteValue(api.Output, "  enrollment", ValueOf(progress, "enrollmentId"));
        api.Output.WriteLine("  The Mohist App is installed, its credentials are provisioned and the workspace is connected.");
    }

    private static void RenderSetupNextAction(MohistCliApi api, string team, string nextAction, JsonObject progress)
    {
        api.Output.WriteLine($"Slack workspace {team}: next action is '{nextAction}'.");
        var installUrl = ValueOf(progress, "installUrl");
        if (!string.IsNullOrEmpty(installUrl))
        {
            api.Output.WriteLine("  Complete the Slack-side App install:");
            api.Output.WriteLine($"  {installUrl}");
        }

        switch (nextAction)
        {
            case SlackSetupActionSupplyConfiguration:
                api.Output.WriteLine("  Provide the workspace Configuration token pair with --configuration-token-file <path>.");
                break;
            case SlackSetupActionSupplyRuntimeCredentials:
                api.Output.WriteLine("  Provide the runtime credentials with --credentials-file <path>.");
                break;
            case SlackSetupActionReportSocketHello:
                api.Output.WriteLine("  The credentials are staged; the mohist-slack service completes the Socket hello automatically, no CLI step is required.");
                break;
            case SlackSetupActionReconcileCreate:
                api.Output.WriteLine("  The App create outcome is unknown; re-running this command reconciles it.");
                break;
        }

        api.Output.WriteLine("Re-run `mo slack setup` to continue from the current step.");
    }

    private static async Task<(JsonObject? Progress, int Exit)> ReadSetupProgressAsync(
        MohistCliApi api,
        string team,
        IReadOnlyDictionary<string, string> headers)
    {
        var response = await api.ResponseReader.ReadAsync(
            HttpMethod.Get,
            $"/api/slack-manager/setup/progress?workspaceTeamId={Uri.EscapeDataString(team)}",
            headers: headers,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (new JsonObject { ["nextAction"] = SlackSetupActionSupplyConfiguration }, 0);
            return (null, await new CliResultWriter(api.Invocation).WriteFailureAsync(response.Failure).ConfigureAwait(false));
        }

        return (response.Data as JsonObject, 0);
    }

    private static async Task<CliResponseResult> PostSetupConfigurationAsync(
        MohistCliApi api,
        string team,
        ConfigurationTokenPair pair,
        IReadOnlyDictionary<string, string> headers) =>
        await api.ResponseReader.ReadAsync(
            HttpMethod.Post,
            "/api/slack-manager/setup/configuration",
            new { workspaceTeamId = team, configurationAccessToken = pair.Token, configurationRefreshToken = pair.Refresh },
            mutating: true,
            headers: headers,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);

    private static async Task<CliResponseResult> PostSetupRuntimeCredentialsAsync(
        MohistCliApi api,
        string team,
        CredentialPair pair,
        IReadOnlyDictionary<string, string> headers) =>
        await api.ResponseReader.ReadAsync(
            HttpMethod.Post,
            "/api/slack-manager/setup/runtime-credentials",
            new { workspaceTeamId = team, botToken = pair.BotToken, appLevelToken = pair.AppToken },
            mutating: true,
            headers: headers,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);

    private static Command BuildInstallAgent(MohistCliApi api, CliCredentialProvider credentials)
    {
        var command = new Command("install-agent", "Install or resume an existing Agent's Slack installation (idempotent wizard)");
        var agent = new Argument<string>("agent") { Description = "Agent name or id" };
        var workspaceTeam = new Option<string?>("--workspace-team")
        {
            Description = "Slack workspace team id; required when it cannot be determined interactively.",
        };
        var credentialsFile = new Option<string?>("--credentials-file")
        {
            Description = "Protected JSON file containing exactly appToken and botToken strings, submitted when the credential step is reached.",
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
                api, credentials, ctx, workspaceTeam, credentialsFile, ctx.GetValue(agent)!, projectId,
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
        CliCredentialProvider credentials,
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

        var headers = await GetOperatorHeadersAsync(api, credentials).ConfigureAwait(false);
        if (headers is null) return (null, 1);

        var enrollmentId = await ResolveEnrollmentIdAsync(api, team, headers).ConfigureAwait(false);
        if (enrollmentId is null) return (null, 1);

        var installPath = ManagerPath(projectId, "/install-agent");
        var credentialsPath = ManagerPath(projectId, "/install-agent/credentials");

        JsonObject? connection = null;
        JsonObject? managedApp = null;
        JsonObject? progress = null;
        var credentialsStaged = false;

        for (var step = 0; step < WizardStepBudget; step++)
        {
            progress = await PostInstallAgentAsync(api, installPath, enrollmentId, agentId, headers).ConfigureAwait(false);
            if (progress is null) return (null, 1);
            connection = progress["connection"] as JsonObject ?? connection;
            managedApp = progress["agentApp"] as JsonObject ?? managedApp;

            var nextAction = ValueOf(progress, "nextAction");
            var credentialState = ValueOf(managedApp, "runtimeCredentialValidationState");
            if (nextAction == SlackInstallActionProvideCredentials
                && credentialState is not (SlackCredentialStateCandidate or SlackCredentialStateAwaitingSocket)
                && !credentialsStaged)
            {
                var pair = await ReadCredentialsAsync(api, ctx.GetValue(credentialsFile)).ConfigureAwait(false);
                if (pair is null) return (null, await FailInstallInputAsync(api, progress, agentReference, team).ConfigureAwait(false));
                var provisioned = await PostInstallCredentialsAsync(api, credentialsPath, managedApp, pair, headers).ConfigureAwait(false);
                if (provisioned is null) return (null, 1);
                credentialsStaged = true;
                continue;
            }

            return (BuildInstallState(progress, connection, managedApp),
                RenderInstallStep(api, agentReference, team, progress, connection, managedApp, nextAction, jsonMode));
        }

        api.Error.WriteLine("Slack install-agent did not converge; re-run `mo slack install-agent` to continue.");
        return (BuildInstallState(progress, connection, managedApp), 1);
    }

    private static async Task<int> FailInstallInputAsync(
        MohistCliApi api,
        JsonObject progress,
        string agentReference,
        string team)
    {
        await api.Error.WriteLineAsync($"Installation of '{agentReference}' in workspace {team} needs the runtime credentials.").ConfigureAwait(false);
        var installUrl = ValueOf(progress, "installUrl");
        if (!string.IsNullOrEmpty(installUrl))
            await api.Error.WriteLineAsync($"Complete the Slack-side App install first: {installUrl}").ConfigureAwait(false);
        await api.Error.WriteLineAsync("Re-run `mo slack install-agent <agent>` to continue from the current step.").ConfigureAwait(false);
        return CliExitCode.For(CliExitOutcome.UsageFailure);
    }

    private static async Task<string?> ResolveEnrollmentIdAsync(
        MohistCliApi api,
        string team,
        IReadOnlyDictionary<string, string> headers)
    {
        var (progress, exit) = await ReadSetupProgressAsync(api, team, headers).ConfigureAwait(false);
        if (exit != 0) return null;
        var enrollmentId = ValueOf(progress, "enrollmentId");
        if (string.IsNullOrEmpty(enrollmentId))
        {
            await api.Error.WriteLineAsync(
                $"Workspace {team} has not started Slack setup; run `mo slack setup` for it first.").ConfigureAwait(false);
            return null;
        }

        return enrollmentId;
    }

    private static async Task<JsonObject?> PostInstallAgentAsync(
        MohistCliApi api,
        string installPath,
        string enrollmentId,
        string agentId,
        IReadOnlyDictionary<string, string> headers)
    {
        var response = await api.ResponseReader.ReadAsync(
            HttpMethod.Post,
            installPath,
            new { enrollmentId, agentId },
            mutating: true,
            headers: headers,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            await new CliResultWriter(api.Invocation).WriteFailureAsync(response.Failure).ConfigureAwait(false);
            return null;
        }

        return response.Data as JsonObject;
    }

    private static async Task<JsonObject?> PostInstallCredentialsAsync(
        MohistCliApi api,
        string credentialsPath,
        JsonObject? managedApp,
        CredentialPair pair,
        IReadOnlyDictionary<string, string> headers)
    {
        var response = await api.ResponseReader.ReadAsync(
            HttpMethod.Post,
            credentialsPath,
            new { agentAppId = ValueOf(managedApp, "id"), botToken = pair.BotToken, appLevelToken = pair.AppToken },
            mutating: true,
            headers: headers,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            await new CliResultWriter(api.Invocation).WriteFailureAsync(response.Failure).ConfigureAwait(false);
            return null;
        }

        if (response.Data is not JsonObject result)
        {
            api.Error.WriteLine("The Server did not return a credential result; re-run `mo slack install-agent` to continue.");
            return null;
        }

        var accepted = result["accepted"] is JsonValue acceptedValue
            && acceptedValue.TryGetValue<bool>(out var value)
            && value;
        if (!accepted)
        {
            var errorClass = ValueOf(result, "errorClass");
            api.Error.WriteLine(string.IsNullOrEmpty(errorClass)
                ? "The runtime credentials were rejected; provide credentials that belong to the Agent App and workspace."
                : $"The runtime credentials were rejected ({errorClass}); provide credentials that belong to the Agent App and workspace.");
            return null;
        }

        return result;
    }

    private static JsonObject BuildInstallState(
        JsonObject? progress,
        JsonObject? connection,
        JsonObject? managedApp) => new()
    {
        ["enrollment"] = EnrollmentState(progress ?? new JsonObject()),
        ["connection"] = connection?.DeepClone(),
        ["managedApp"] = managedApp?.DeepClone(),
        ["installUrl"] = NullIfEmpty(ValueOf(progress, "installUrl")),
        ["nextAction"] = ValueOf(progress, "nextAction"),
        ["errorClass"] = NullIfEmpty(ValueOf(progress, "errorClass")),
    };

    private static int RenderInstallStep(
        MohistCliApi api,
        string agentReference,
        string team,
        JsonObject progress,
        JsonObject? connection,
        JsonObject? managedApp,
        string nextAction,
        bool jsonMode)
    {
        if (jsonMode) return 0;
        var installUrl = ValueOf(progress, "installUrl");
        if (!string.IsNullOrEmpty(installUrl) && nextAction is not SlackInstallActionReady)
        {
            api.Output.WriteLine("  Complete the Slack-side App install:");
            api.Output.WriteLine($"  {installUrl}");
        }

        switch (nextAction)
        {
            case SlackInstallActionProvideCredentials:
            {
                var credentialState = ValueOf(managedApp, "runtimeCredentialValidationState");
                if (credentialState is SlackCredentialStateCandidate or SlackCredentialStateAwaitingSocket)
                {
                    api.Output.WriteLine($"Installation of '{agentReference}' in workspace {team} is waiting for the Slack connection.");
                    api.Output.WriteLine("  The credentials are staged; the mohist-slack service completes the Socket hello automatically, no CLI step is required.");
                    api.Output.WriteLine("  Re-run `mo slack install-agent <agent>` to verify and finish.");
                }
                else
                {
                    api.Output.WriteLine($"Installation of '{agentReference}' in workspace {team} needs the runtime credentials.");
                    api.Output.WriteLine("  Provide them with --credentials-file <path> and re-run `mo slack install-agent <agent>`.");
                }

                break;
            }

            case SlackInstallActionCreateAgentApp:
            case SlackInstallActionWaitForOperation:
            case SlackInstallActionReconcileCreate:
            case SlackInstallActionBindConnection:
                RenderInstallResume(api, agentReference, team, nextAction);
                break;
            case SlackInstallActionDeleted:
                api.Output.WriteLine($"The Agent App for '{agentReference}' was permanently deleted.");
                api.Output.WriteLine("The Connection binding was removed; nothing else to install.");
                break;
            case SlackInstallActionReady:
                RenderInstallReady(api, agentReference, team, connection);
                break;
            default:
                RenderInstallResume(api, agentReference, team, nextAction);
                break;
        }

        return 0;
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

    private static Command BuildStatus(MohistCliApi api, CliCredentialProvider credentials)
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

    private static async Task<ConfigurationTokenPair?> ReadConfigurationTokenPairAsync(MohistCliApi api, string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
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
                return new ConfigurationTokenPair(token.GetString()!.Trim(), refresh.GetString()!.Trim());
            }

            if (!await api.Invocation.RequirePromptAsync(
                    "Workspace Configuration tokens",
                    "--configuration-token-file <path>",
                    () => Task.FromResult(true)).ConfigureAwait(false))
                return null;
            await api.Error.WriteLineAsync("Configuration token:").ConfigureAwait(false);
            var hiddenToken = await api.Invocation.Terminal
                .ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken)
                .ConfigureAwait(false);
            await api.Error.WriteLineAsync("Configuration refresh token:").ConfigureAwait(false);
            var hiddenRefresh = await api.Invocation.Terminal
                .ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(hiddenToken) || string.IsNullOrWhiteSpace(hiddenRefresh))
                throw new InvalidOperationException("Both configuration tokens are required.");
            return new ConfigurationTokenPair(hiddenToken.Trim(), hiddenRefresh.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            api.Error.WriteLine(ex.Message);
            return null;
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
            if (result.ExitCode == 0)
                await PrintClaimCodeHintAsync(api, result);
            return result.ExitCode;
        });
        return command;
    }

    private static async Task PrintClaimCodeHintAsync(MohistCliApi api, MohistCliApi.PostResult result)
    {
        var botName = result.Data?["botName"]?.GetValue<string>();
        var hint = string.IsNullOrWhiteSpace(botName)
            ? "Send the code to the Agent bot DM to claim ownership."
            : $"Send the code to the Agent bot DM ({botName}) to claim ownership.";
        await api.Error.WriteLineAsync(hint).ConfigureAwait(false);
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
            if (result.ExitCode == 0)
                await PrintClaimCodeHintAsync(api, result);
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
        var yes = new Option<bool>("--yes", "-y")
        {
            Description = "Confirm permanent deletion. This operation cannot be inferred from a normal remove-binding.",
        };
        var confirmation = new Option<string?>("--confirm")
        {
            Description = "Legacy alias requiring exactly DELETE; use --yes instead.",
            Hidden = true,
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(yes);
        command.Options.Add(confirmation);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var confirmationValue = ctx.GetValue(confirmation);
            if (confirmationValue is not null
                && !string.Equals(confirmationValue, "DELETE", StringComparison.Ordinal))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--confirm must be exactly DELETE.");
            if (!ctx.GetValue(yes) && confirmationValue is null)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--yes is required for permanent-delete.");
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

    private static Command BuildMessage(MohistCliApi api)
    {
        var message = new Command("message", "Send or read Slack messages on behalf of an Agent");
        message.Subcommands.Add(BuildMessageSend(api));
        return message;
    }

    private static Command BuildMessageSend(MohistCliApi api)
    {
        var command = new Command("send", "Send a reply to a Slack conversation (the Agent-authored reply body); --text - reads the body from stdin");
        var conversation = new Option<string>("--conversation")
        {
            Description = "Slack conversation id (channel or DM), read from the injected Slack reply anchor.",
            Required = true,
        };
        var replyTo = new Option<string?>("--reply-to")
        {
            Description = "Thread root message timestamp to reply in a thread (the reply anchor threadRootMessageId). Omit for a DM.",
        };
        var text = new Option<string?>("--text")
        {
            Description = "Reply body in markdown (bold/code/lists/quotes render in Slack). Pass '-' to read it from standard input (preserves newlines).",
        };
        var file = new Option<string?>("--file")
        {
            Description = "Local image file to upload to Slack (base64-encoded to the Server; at most 10 MB).",
        };
        var image = new Option<string?>("--image")
        {
            Description = "Public image URL to display inline in the reply.",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Options.Add(conversation);
        command.Options.Add(replyTo);
        command.Options.Add(text);
        command.Options.Add(file);
        command.Options.Add(image);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;

            var body = ctx.GetValue(text);
            if (string.Equals(body, "-", StringComparison.Ordinal))
            {
                body = await api.StandardInput
                    .ReadToEndAsync(api.Invocation.CancellationToken)
                    .ConfigureAwait(false);
            }

            var filePath = ctx.GetValue(file);
            var imageUrl = ctx.GetValue(image);
            if (filePath is not null && imageUrl is not null)
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--file and --image are mutually exclusive.");
            if (string.IsNullOrWhiteSpace(body) && filePath is null && imageUrl is null)
            {
                api.Error.WriteLine("--text, --file, or --image is required; nothing to send (silence is achieved by not running this command).");
                return CliExitCode.For(CliExitOutcome.OperationFailure);
            }
            if (imageUrl is not null
                && !imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "--image must be a public http(s) image URL.");

            string? fileName = null;
            string? fileContentBase64 = null;
            if (filePath is not null)
            {
                if (!api.FileSystem.Exists(filePath) || api.FileSystem.DirectoryExists(filePath))
                {
                    api.Error.WriteLine($"--file '{filePath}' does not exist or is not a regular file.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                await using var stream = api.FileSystem.OpenRead(filePath);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, api.Invocation.CancellationToken).ConfigureAwait(false);
                if (buffer.Length == 0)
                {
                    api.Error.WriteLine($"--file '{filePath}' is empty.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }
                if (buffer.Length > 10 * 1024 * 1024)
                {
                    api.Error.WriteLine($"--file '{filePath}' exceeds the 10 MB upload limit.");
                    return CliExitCode.For(CliExitOutcome.OperationFailure);
                }

                fileName = System.IO.Path.GetFileName(filePath);
                fileContentBase64 = Convert.ToBase64String(buffer.ToArray());
            }

            return await api.PrintPostWithOutputAsync(
                Path(projectId, "/reply"),
                new
                {
                    conversationId = ctx.GetValue(conversation),
                    threadTs = ctx.GetValue(replyTo),
                    text = string.IsNullOrWhiteSpace(body) ? null : body,
                    imageUrl,
                    fileName,
                    fileContentBase64,
                },
                "json",
                tableShape: null);
        });
        return command;
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

    private static async Task<CredentialPair?> ReadRuntimeCredentialsAsync(MohistCliApi api, string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!IsProtectedFile(api.FileSystem, path))
                    throw new InvalidOperationException("Credential file must be a regular, non-symlink file readable and writable only by the current user.");
                var json = await api.FileSystem.ReadAllTextAsync(path).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                    || !root.TryGetProperty("botToken", out var bot) || !root.TryGetProperty("appToken", out var app)
                    || bot.ValueKind != JsonValueKind.String || app.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(bot.GetString()) || string.IsNullOrWhiteSpace(app.GetString()))
                    throw new InvalidOperationException("Credential file must contain exactly non-empty botToken and appToken strings.");
                return new CredentialPair(app.GetString()!.Trim(), bot.GetString()!.Trim());
            }

            if (!await api.Invocation.RequirePromptAsync(
                    "Runtime credentials",
                    "--credentials-file <path>",
                    () => Task.FromResult(true)).ConfigureAwait(false))
                return null;
            await api.Error.WriteLineAsync("Bot token:").ConfigureAwait(false);
            var botToken = await api.Invocation.Terminal
                .ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken)
                .ConfigureAwait(false);
            await api.Error.WriteLineAsync("App-level token:").ConfigureAwait(false);
            var appToken = await api.Invocation.Terminal
                .ReadHiddenAsync(api.Invocation.Input, api.Invocation.CancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(appToken))
                throw new InvalidOperationException("Both runtime credentials are required.");
            return new CredentialPair(appToken.Trim(), botToken.Trim());
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

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private const string SlackSetupActionSupplyConfiguration = "supply_configuration";
    private const string SlackSetupActionSupplyRuntimeCredentials = "supply_runtime_credentials";
    private const string SlackSetupActionReportSocketHello = "report_socket_hello";
    private const string SlackSetupActionReconcileCreate = "reconcile_create";
    private const string SlackSetupActionReady = "ready";

    private const string SlackInstallActionProvideCredentials = "provide_credentials";
    private const string SlackInstallActionCreateAgentApp = "create_agent_app";
    private const string SlackInstallActionWaitForOperation = "wait_for_operation";
    private const string SlackInstallActionReconcileCreate = "reconcile_create";
    private const string SlackInstallActionBindConnection = "bind_connection";
    private const string SlackInstallActionDeleted = "deleted";
    private const string SlackInstallActionReady = "ready";

    private const string SlackCredentialStateCandidate = "candidate";
    private const string SlackCredentialStateAwaitingSocket = "awaiting_socket";

    private static async Task<IReadOnlyDictionary<string, string>?> GetOperatorHeadersAsync(
        MohistCliApi api,
        CliCredentialProvider credentials)
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
                ["authorization"] = $"Bearer {token}",
            };
        }
        catch (InvalidOperationException ex)
        {
            api.Error.WriteLine(ex.Message);
            return null;
        }
    }

    private sealed record ConfigurationTokenPair(string Token, string Refresh);

    private sealed record CredentialPair(string AppToken, string BotToken);
}
