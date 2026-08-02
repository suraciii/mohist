using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class AgentConnectionCommands
{
    public static Command Build(MohistCliApi api)
    {
        var group = new Command("connection", "Manage Slack Agent Connections");
        group.Subcommands.Add(BuildCreate(api));
        group.Subcommands.Add(BuildConfigure(api));
        group.Subcommands.Add(BuildRotateCredentials(api));
        group.Subcommands.Add(BuildClaimOwner(api));
        group.Subcommands.Add(BuildTransferOwner(api));
        group.Subcommands.Add(BuildDisable(api));
        group.Subcommands.Add(BuildEnable(api));
        group.Subcommands.Add(BuildView(api));
        group.Subcommands.Add(BuildList(api));
        group.Subcommands.Add(BuildListDeliveries(api));
        group.Subcommands.Add(BuildResendDelivery(api));
        group.Subcommands.Add(BuildClearGap(api));
        group.Subcommands.Add(BuildEdit(api));
        group.Subcommands.Add(BuildDelete(api));
        return group;
    }

    private static string Path(string projectId, string suffix = "") =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/slack-connections{suffix}";

    private static async Task<(string? ProjectId, int Exit)> ProjectAsync(MohistCliApi api, string? project)
    {
        var resolved = await api.ResolveProject(project).ConfigureAwait(false);
        return (resolved.ProjectId, resolved.Exit);
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var command = new Command("create", "Create a Slack Connection");
        var agent = new Argument<string>("agent") { Description = "Agent name or id" };
        var provider = new Option<string>("--provider") { DefaultValueFactory = _ => "slack" };
        var botName = new Option<string?>("--bot-name");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(agent);
        command.Options.Add(provider);
        command.Options.Add(botName);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            if (!string.Equals(ctx.GetValue(provider), "slack", StringComparison.OrdinalIgnoreCase))
                return CommandHelpHook.RenderUsageFailure(ctx, api.Error, "Only --provider slack is supported.");
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var agentId = await ResolveAgentIdAsync(api, projectId, ctx.GetValue(agent));
            if (agentId is null) return 1;
            var result = await api.PostAndReadAsync(Path(projectId), new
            {
                agentId,
                botName = ctx.GetValue(botName),
            });
            return result.ExitCode;
        });
        return command;
    }

    private static Command BuildConfigure(MohistCliApi api)
    {
        var command = new Command("configure", "Store Slack App and Bot credentials");
        var id = new Argument<string>("connection-id");
        var file = new Option<string?>("--credentials-file")
        {
            Description = "UTF-8 JSON file containing exactly appToken and botToken",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(file);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var credentials = await ReadCredentialsAsync(api, ctx.GetValue(file));
            if (credentials is null) return 2;
            var result = await api.PostAndReadAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/configure"), credentials);
            return result.ExitCode;
        });
        return command;
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

    private static Command BuildRotateCredentials(MohistCliApi api)
    {
        var command = new Command("rotate-credentials", "Verify and rotate Slack App and Bot credentials");
        var id = new Argument<string>("connection-id");
        var file = new Option<string?>("--credentials-file")
        {
            Description = "UTF-8 JSON file containing exactly appToken and botToken",
        };
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(file);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var credentials = await ReadCredentialsAsync(api, ctx.GetValue(file));
            if (credentials is null) return 2;
            var result = await api.PostAndReadAsync(
                Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!)}/rotate-credentials"),
                credentials);
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

    private static Command BuildDisable(MohistCliApi api) => BuildDesiredStateCommand(api, "disable", "Disabled");

    private static Command BuildEnable(MohistCliApi api) => BuildDesiredStateCommand(api, "enable", "Enabled");

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
        var project = MohistCliCommands.ProjectRefOption();
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            if (exit != 0 || projectId is null) return exit;
            var result = await api.GetDataOrPrintErrorAsync(Path(projectId));
            if (result.ExitCode != 0) return result.ExitCode;
            RenderConnectionList(api.Output, result.Data);
            return 0;
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
            Description = "Outbox row id returned by `mo connection deliveries` (state: delivery_uncertain).",
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


    private static Command BuildDelete(MohistCliApi api)
    {
        var command = new Command("delete", "Delete a Slack Connection");
        var id = new Argument<string>("connection-id");
        var project = MohistCliCommands.ProjectRefOption();
        command.Arguments.Add(id);
        command.Options.Add(project);
        command.SetAction(async ctx =>
        {
            var (projectId, exit) = await ProjectAsync(api, ctx.GetValue(project));
            return exit != 0 || projectId is null ? exit : await api.PrintDeleteAsync(Path(projectId, $"/{Uri.EscapeDataString(ctx.GetValue(id)!) }"));
        });
        return command;
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

    private static bool IsProtectedFile(IFileSystem fileSystem, string path)
    {
        if (!fileSystem.Exists(path) || fileSystem.DirectoryExists(path)) return false;
        return !fileSystem.IsSymbolicLink(path) && fileSystem.IsUserOnlyFile(path);
    }

    private sealed record CredentialPair(string AppToken, string BotToken);
}
