using System.CommandLine;
using System.CommandLine.Parsing;
using System.Net;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class SlackCommands
{
    private static Command BuildSetup(MohistCliApi api)
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
                api, ctx, workspaceTeam, configurationTokenFile, credentialsFile,
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
        ParseResult ctx,
        Option<string?> workspaceTeam,
        Option<string?> configurationTokenFile,
        Option<string?> credentialsFile,
        bool jsonMode)
    {
        var team = await ResolveWorkspaceTeamAsync(api, ctx, workspaceTeam, "slack setup").ConfigureAwait(false);
        if (team is null) return (null, CliExitCode.For(CliExitOutcome.UsageFailure));

        JsonObject? progress = null;
        var configurationSupplied = false;
        var runtimeSupplied = false;

        for (var step = 0; step < WizardStepBudget; step++)
        {
            var (current, exit) = await ReadSetupProgressAsync(api, team).ConfigureAwait(false);
            if (exit != 0) return (null, exit);
            if (current is null) return (null, 1);
            progress = current;

            var nextAction = ValueOf(progress, "nextAction");
            if (string.IsNullOrEmpty(nextAction)) nextAction = SlackSetupActionSupplyConfiguration;

            if (nextAction == SlackSetupActionSupplyConfiguration && !configurationSupplied)
            {
                var pair = await ReadConfigurationTokenPairAsync(api, ctx.GetValue(configurationTokenFile)).ConfigureAwait(false);
                if (pair is null) return (null, await FailSetupInputAsync(api, team, progress, nextAction).ConfigureAwait(false));
                var posted = await PostSetupConfigurationAsync(api, team, pair).ConfigureAwait(false);
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
                var posted = await PostSetupRuntimeCredentialsAsync(api, team, pair).ConfigureAwait(false);
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
                var posted = await PostSetupRuntimeCredentialsAsync(api, team, pair).ConfigureAwait(false);
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
                var posted = await PostSetupConfigurationAsync(api, team, pair).ConfigureAwait(false);
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
        string team)
    {
        var response = await api.ResponseReader.ReadAsync(
            HttpMethod.Get,
            $"/api/slack-manager/setup/progress?workspaceTeamId={Uri.EscapeDataString(team)}",
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
        ConfigurationTokenPair pair) =>
        await api.ResponseReader.ReadAsync(
            HttpMethod.Post,
            "/api/slack-manager/setup/configuration",
            new { workspaceTeamId = team, configurationAccessToken = pair.Token, configurationRefreshToken = pair.Refresh },
            mutating: true,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);

    private static async Task<CliResponseResult> PostSetupRuntimeCredentialsAsync(
        MohistCliApi api,
        string team,
        CredentialPair pair) =>
        await api.ResponseReader.ReadAsync(
            HttpMethod.Post,
            "/api/slack-manager/setup/runtime-credentials",
            new { workspaceTeamId = team, botToken = pair.BotToken, appLevelToken = pair.AppToken },
            mutating: true,
            cancellationToken: api.Invocation.CancellationToken).ConfigureAwait(false);
}
