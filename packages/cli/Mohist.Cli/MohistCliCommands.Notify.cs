using System.CommandLine;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

/// <summary>
/// <c>mo notification</c> command group. Provides <c>setup</c>, a guided
/// configuration command that probes the Hermes webhook platform,
/// generates one shared secret, writes Mohist's outbound
/// <c>Mohist:Notifications:Hermes</c> config directly to
/// <c>~/.mohist/config.jsonc</c>, and emits a copy-pasteable
/// <c>hermes webhook subscribe mohist</c> command for the user to run.
/// </summary>
internal static class NotifyCommands
{
    /// <summary>Default base URL of the Hermes webhook platform.</summary>
    public const string DefaultHealthBaseUrl = "http://127.0.0.1:8644";

    /// <summary>Default path appended to the probed base to derive the receiver URL.</summary>
    public const string WebhookReceiverSuffix = "/webhooks/mohist";

    /// <summary>Default enabled notification types — mirrors <c>HermesNotificationOptions</c>.</summary>
    public static readonly string[] DefaultEnabledTypes =
    [
        "approval_requested",
        "workflow_failed",
        "issue_completed",
    ];

    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>
    /// Default config-path resolver. Returns the canonical
    /// <c>~/.mohist/config.jsonc</c> path used by
    /// <c>MohistConfigurationExtensions.AddMohistConfigFile</c>.
    /// </summary>
    public static string DefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".mohist",
        "config.jsonc");

    /// <summary>
    /// Default config-path resolution (override hook lets tests pin to a
    /// directory inside <see cref="FakeFileSystem"/>).
    /// </summary>
    public static Func<string> ConfigPathOverride { get; set; } = DefaultConfigPath;

    public static Command Build(MohistCliApi api)
    {
        var notification = new Command("notification", "Notification platform guides");
        notification.Subcommands.Add(BuildSetup(api));
        return notification;
    }

    private static Command BuildSetup(MohistCliApi api)
    {
        var cmd = new Command(
            "setup",
            "Probe Hermes webhook readiness, generate a shared signing secret, " +
            "write Mohist's outbound Hermes config, and print the matching " +
            "hermes webhook subscribe command for the user to run.");

        var healthBaseOpt = new Option<string>("--health-base")
        {
            Description = "Base URL of the Hermes webhook platform (default http://127.0.0.1:8644).",
            DefaultValueFactory = _ => DefaultHealthBaseUrl,
        };
        var webhookUrlOpt = new Option<string?>("--webhook-url")
        {
            Description = "Override the Mohist→Hermes webhook receiver URL " +
                          "(default: <--health-base>/webhooks/mohist).",
        };
        var platformOpt = new Option<string?>("--platform")
        {
            Description = "Delivery platform that will be passed to Hermes as --deliver " +
                          "(e.g. telegram, weixin). When omitted, the printed subscribe command " +
                          "leaves --deliver out and prints placeholder guidance.",
        };
        var deliverChatIdOpt = new Option<string?>("--deliver-chat-id")
        {
            Description = "Chat id passed to Hermes as --deliver-chat-id. Required for " +
                          "platforms without a default home channel (e.g. weixin); optional " +
                          "for platforms that have one (e.g. telegram).",
        };

        cmd.Options.Add(healthBaseOpt);
        cmd.Options.Add(webhookUrlOpt);
        cmd.Options.Add(platformOpt);
        cmd.Options.Add(deliverChatIdOpt);
        cmd.SetAction(ctx =>
        {
            var healthBase = ctx.GetValue(healthBaseOpt)!;
            var webhookUrlOverride = ctx.GetValue(webhookUrlOpt);
            var platform = ctx.GetValue(platformOpt);
            var deliverChatId = ctx.GetValue(deliverChatIdOpt);
            return RunSetupAsync(api, healthBase, webhookUrlOverride, platform, deliverChatId);
        });
        return cmd;
    }

    /// <summary>
    /// Default health-probe implementation: throws
    /// <see cref="HttpRequestException"/> on connection refused /
    /// DNS, <see cref="TaskCanceledException"/> on timeout, and treats
    /// non-success status as failure. Mirrors the discipline in
    /// <c>OtelCommands.RunStatusAsync</c>: never let a stack trace
    /// bubble out of the command.
    /// </summary>
    public interface IHealthProbe
    {
        Task<HealthProbeResult> ProbeAsync(string healthBase);
    }

    /// <summary>Default HTTP-backed probe. Constructed by the action delegate.</summary>
    public sealed class HttpHealthProbe : IHealthProbe
    {
        public Task<HealthProbeResult> ProbeAsync(string healthBase) =>
            ProbeHermesHealthAsync(healthBase);
    }

    /// <summary>
    /// Optional health-probe seam. Tests inject a stub that returns
    /// the configured <see cref="HealthProbeResult"/> without touching
    /// the network. The CLI default uses <see cref="HttpHealthProbe"/>.
    /// </summary>
    public static IHealthProbe HealthProbeOverride { get; set; } = new HttpHealthProbe();

    /// <summary>
    /// Internal command flow used by <c>mo notification setup</c>. Splits
    /// cleanly for unit-testing: <paramref name="healthProbe"/> can be
    /// swapped to a stub; <paramref name="secret"/> is computed once
    /// and handed to both sides so identity is structural, not
    /// coincidental.
    /// </summary>
    public static async Task<int> RunSetupAsync(
        MohistCliApi api,
        string healthBase,
        string? webhookUrlOverride,
        string? platform,
        string? deliverChatId,
        IHealthProbe? healthProbe = null,
        string? secret = null)
    {
        var probe = await (healthProbe ?? HealthProbeOverride)
            .ProbeAsync(healthBase)
            .ConfigureAwait(false);
        if (!probe.IsHealthy)
        {
            await WriteHermesNotStartedAsync(api.Error).ConfigureAwait(false);
            return 1;
        }

        if (!TryValidatePlatform(platform, out var platformError))
        {
            await api.Error.WriteLineAsync(platformError).ConfigureAwait(false);
            return 1;
        }

        var configPath = ConfigPathOverride();
        var fileSystem = api.FileSystem;
        var config = LoadHermesConfig(fileSystem, configPath);
        if (!config.Success)
        {
            await api.Error.WriteLineAsync(config.ErrorMessage).ConfigureAwait(false);
            return 1;
        }

        if (config.HermesSectionExists)
        {
            var overwrite = await PromptForOverwriteAsync(api, api.StandardInput).ConfigureAwait(false);
            if (!overwrite.ShouldContinue)
            {
                await api.Output.WriteLineAsync(
                    "Aborted: existing Mohist:Notifications:Hermes values left untouched.")
                    .ConfigureAwait(false);
                return overwrite.IsEof ? 1 : 0;
            }
        }

        secret ??= SecretGeneratorOverride();
        var webhookUrl = string.IsNullOrWhiteSpace(webhookUrlOverride)
            ? DeriveWebhookUrl(healthBase)
            : webhookUrlOverride.Trim();

        await WriteHermesConfigAsync(fileSystem, configPath, config.Root!, webhookUrl, secret).ConfigureAwait(false);

        await api.Output.WriteLineAsync(
            $"Wrote Mohist:Notifications:Hermes to {configPath}").ConfigureAwait(false);

        await PrintHermesSubscribeCommandAsync(api.Output, platform, deliverChatId, webhookUrl, secret)
            .ConfigureAwait(false);

        await api.Output.WriteLineAsync(
            "Reload the managed server to pick up the new config: mo update server")
            .ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Strips JSONC <c>//</c> line and <c>/* */</c> block comments while
    /// preserving string literals (including escaped quotes) verbatim.
    /// Pure function over the stable JSONC subset; mirrors the server's
    /// <c>MohistConfigurationExtensions.StripJsoncComments</c>.
    /// </summary>
    public static string StripJsoncComments(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var result = new StringBuilder(json.Length);
        var i = 0;
        while (i < json.Length)
        {
            if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '*')
            {
                i += 2;
                while (i < json.Length - 1 && !(json[i] == '*' && json[i + 1] == '/'))
                    i++;
                i += 2;
                continue;
            }

            if (i + 1 < json.Length && json[i] == '/' && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n')
                    i++;
                continue;
            }

            if (json[i] == '"')
            {
                result.Append(json[i]);
                i++;
                while (i < json.Length)
                {
                    result.Append(json[i]);
                    if (json[i] == '\\' && i + 1 < json.Length)
                    {
                        i++;
                        result.Append(json[i]);
                    }
                    else if (json[i] == '"')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            result.Append(json[i]);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Generates a 32-byte random secret rendered as URL-safe base64.
    /// Pure helper isolated so tests / deterministic callers can exercise
    /// boundary behavior without depending on the RNG.
    /// </summary>
    public static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Secret-generator seam. The CLI command path uses
    /// <see cref="GenerateSecret()"/> by default; tests inject a
    /// deterministic generator via <see cref="SecretGeneratorOverride"/>
    /// so they can pin the printed <c>--secret</c> value to a known
    /// string for identity assertions.
    /// </summary>
    public static Func<string> SecretGeneratorOverride { get; set; } = GenerateSecret;

    /// <summary>Generate a fresh secret through the active generator.</summary>
    public static string GenerateSecretThroughOverride() => SecretGeneratorOverride();

    /// <summary>URL-safe base64 encoder (no padding, <c>-</c>/<c>_</c> alphabet).</summary>
    public static string Base64UrlEncode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Derives the receiver URL by appending <see cref="WebhookReceiverSuffix"/>
    /// to the probed base, normalising the path separator.
    /// </summary>
    public static string DeriveWebhookUrl(string healthBase)
    {
        var trimmed = (healthBase ?? string.Empty).TrimEnd('/');
        return trimmed + WebhookReceiverSuffix;
    }

    /// <summary>
    /// Probes the Hermes health endpoint with a throwaway
    /// <see cref="HttpClient"/>/<see cref="HttpRequestMessage"/> pair and
    /// returns a result describing success or failure reason. Mirrors
    /// <c>OtelCommands.RunStatusAsync</c>'s friendly-no-stack-trace
    /// discipline: swallows <see cref="HttpRequestException"/> and
    /// <see cref="TaskCanceledException"/> into a non-success result.
    /// </summary>
    public static async Task<HealthProbeResult> ProbeHermesHealthAsync(string healthBase)
    {
        var url = BuildHealthUrl(healthBase);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return HealthProbeResult.Unhealthy("invalid url");
        }

        using var client = new HttpClient
        {
            Timeout = HealthProbeTimeout,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HealthProbeResult.Unhealthy($"non-success status {(int)response.StatusCode}");
            }
            return HealthProbeResult.Healthy(url);
        }
        catch (HttpRequestException)
        {
            return HealthProbeResult.Unhealthy("connection refused");
        }
        catch (TaskCanceledException)
        {
            return HealthProbeResult.Unhealthy("timeout");
        }
        catch (UriFormatException)
        {
            return HealthProbeResult.Unhealthy("invalid url");
        }
        catch (InvalidOperationException)
        {
            return HealthProbeResult.Unhealthy("invalid url");
        }
    }

    /// <summary>Builds the absolute health URL from a base, appending <c>/health</c>.</summary>
    public static string BuildHealthUrl(string healthBase)
    {
        var trimmed = (healthBase ?? string.Empty).TrimEnd('/');
        return trimmed + "/health";
    }

    /// <summary>
    /// Returns <c>true</c> when the file at <paramref name="configPath"/>
    /// exists and contains a non-empty <c>Mohist.Notifications.Hermes</c>
    /// subtree. Used to gate the overwrite confirmation.
    /// </summary>
    public static HermesConfigLoadResult LoadHermesConfig(IFileSystem fileSystem, string configPath)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!fileSystem.Exists(configPath))
            return HermesConfigLoadResult.Loaded(new JsonObject(), hermesSectionExists: false);

        try
        {
            var text = fileSystem.ReadAllText(configPath);
            var root = JsonNode.Parse(StripJsoncComments(text)) as JsonObject ?? new JsonObject();
            var mohist = root["Mohist"];
            if (mohist is not null && mohist is not JsonObject)
            {
                return HermesConfigLoadResult.Failed(
                    $"Could not update Mohist config file '{configPath}': Mohist must be a JSON object.");
            }

            var notifications = ((JsonObject?)mohist)?["Notifications"];
            if (notifications is not null && notifications is not JsonObject)
            {
                return HermesConfigLoadResult.Failed(
                    $"Could not update Mohist config file '{configPath}': Mohist:Notifications must be a JSON object.");
            }

            var hermes = ((JsonObject?)notifications)?["Hermes"];
            var exists = hermes switch
            {
                null => false,
                JsonObject hermesObj => hermesObj.Count > 0,
                _ => true,
            };
            return HermesConfigLoadResult.Loaded(root, exists);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return HermesConfigLoadResult.Failed(
                $"Could not parse Mohist config file '{configPath}'. Fix the JSONC syntax and re-run 'mo notify setup'.");
        }
    }

    /// <summary>
    /// Reads existing <c>~/.mohist/config.jsonc</c>, ensures
    /// <c>Mohist.Notifications.Hermes</c> carries exactly the freshly
    /// generated values, and serializes back through
    /// <see cref="IFileSystem"/>. Tolerates a missing parent directory
    /// by creating it (the real filesystem already does this; the test
    /// fake does not, so be defensive on the read path only).
    /// </summary>
    public static async Task WriteHermesConfigAsync(
        IFileSystem fileSystem,
        string configPath,
        JsonObject root,
        string webhookUrl,
        string secret)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(webhookUrl);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        SetSection(root, webhookUrl, secret);

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
            fileSystem.CreateDirectory(directory);

        await fileSystem.WriteAllTextAsync(configPath, root.ToJsonString(IndentedJson))
            .ConfigureAwait(false);
    }

    private static void SetSection(JsonObject root, string webhookUrl, string secret)
    {
        var mohist = root["Mohist"] as JsonObject ?? new JsonObject();
        root["Mohist"] = mohist;

        var notifications = mohist["Notifications"] as JsonObject ?? new JsonObject();
        mohist["Notifications"] = notifications;

        var hermes = new JsonObject
        {
            ["WebhookUrl"] = webhookUrl,
            ["Secret"] = secret,
            ["EnabledTypes"] = new JsonArray(DefaultEnabledTypes.Select(t => JsonValue.Create(t)).ToArray()),
        };
        notifications["Hermes"] = hermes;
    }

    /// <summary>
    /// Reads <c>y/N</c> from standard input. Returns
    /// <see cref="OverwritePromptResult.Continue"/> when the user
    /// confirmed, <see cref="OverwritePromptResult.Abandon"/> when the
    /// user declined, and <see cref="OverwritePromptResult.Eof"/> when
    /// stdin is empty or unavailable (non-interactive) so the caller
    /// can abort without writing.
    /// </summary>
    public static async Task<OverwritePromptResult> PromptForOverwriteAsync(
        MohistCliApi api,
        TextReader standardInput)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(standardInput);

        if (!api.Invocation.PromptsEnabled)
        {
            await api.Error.WriteLineAsync(
                "confirmation is required; pass explicit notification configuration or use an interactive terminal")
                .ConfigureAwait(false);
            return OverwritePromptResult.EofResult;
        }

        await api.Error.WriteLineAsync(
            "Existing Mohist:Notifications:Hermes values were found in the config file. " +
            "Overwrite them? [y/N]: ")
            .ConfigureAwait(false);

        string? line;
        try
        {
            line = await standardInput.ReadLineAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            return OverwritePromptResult.EofResult;
        }

        if (line is null)
            return OverwritePromptResult.EofResult;

        var trimmed = line.Trim();
        if (string.Equals(trimmed, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return OverwritePromptResult.Continue;
        }

        return OverwritePromptResult.Abandon;
    }

    /// <summary>
    /// Renders the complete, copy-pasteable Hermes subscribe command,
    /// carrying the same secret written to the config. When
    /// <paramref name="platform"/> is null/empty the command is emitted
    /// with placeholder guidance and the user is told to supply
    /// <c>--deliver</c>. When a platform is set but
    /// <paramref name="deliverChatId"/> is empty, a hint notes that some
    /// platforms (e.g. weixin) require a chat id and points at how to
    /// find it. The <c>--prompt</c> template uses <c>{body}</c>, which is
    /// the field Mohist actually posts (a bespoke <c>{message}</c> would
    /// render empty because no such field exists in the payload).
    /// </summary>
    public static async Task PrintHermesSubscribeCommandAsync(
        TextWriter output,
        string? platform,
        string? deliverChatId,
        string webhookUrl,
        string secret)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(webhookUrl);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (!TryValidatePlatform(platform, out var platformError))
            throw new ArgumentException(platformError, nameof(platform));

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Run this in Hermes to subscribe to Mohist:").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);

        var deliver = string.IsNullOrWhiteSpace(platform) ? "<platform>" : platform!.Trim();
        var chatIdSegment = !string.IsNullOrWhiteSpace(deliverChatId)
            ? $" --deliver-chat-id {deliverChatId!.Trim()}"
            : string.Empty;
        var command =
            $"hermes webhook subscribe mohist --deliver {deliver}{chatIdSegment} --deliver-only " +
            $"--secret {secret} --prompt '{{body}}'";

        await output.WriteLineAsync(command).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(platform))
        {
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync(
                "Replace <platform> with the target delivery platform (e.g. telegram, weixin).")
                .ConfigureAwait(false);
        }
        else if (string.IsNullOrWhiteSpace(deliverChatId))
        {
            // Platforms without a default home channel (weixin and similar)
            // reject delivery with "No chat_id" unless --deliver-chat-id is
            // supplied. We don't auto-detect which platform needs it; we just
            // point the user at how to find the id when they didn't give one.
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync(
                "Hint: some platforms (e.g. weixin) have no default home channel and need " +
                "--deliver-chat-id. Find yours with: hermes send --list <platform>")
                .ConfigureAwait(false);
        }
    }

    public static bool TryValidatePlatform(string? platform, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(platform))
            return true;

        var trimmed = platform.Trim();
        if (trimmed.Length == 0)
            return true;

        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')
                continue;

            error = "--platform must contain only letters, digits, underscore, hyphen, or dot.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Prints the canonical "Hermes webhook platform is not started"
    /// message. Always points the user at the Hermes notification docs
    /// and includes the probed base URL when available.
    /// </summary>
    public static async Task WriteHermesNotStartedAsync(TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        await error.WriteLineAsync(
            "Hermes webhook platform is not started.").ConfigureAwait(false);
        await error.WriteLineAsync(
            "Start the Hermes webhook platform and re-run 'mo notify setup'.").ConfigureAwait(false);
        await error.WriteLineAsync(
            "See docs/hermes-notifications.md for setup steps.").ConfigureAwait(false);
    }

    public enum OverwritePromptOutcome
    {
        Continue,
        Abandon,
        Eof,
    }

    public sealed class OverwritePromptResult
    {
        public OverwritePromptOutcome Outcome { get; }

        public bool ShouldContinue => Outcome == OverwritePromptOutcome.Continue;

        public bool IsEof => Outcome == OverwritePromptOutcome.Eof;

        private OverwritePromptResult(OverwritePromptOutcome outcome) => Outcome = outcome;

        public static readonly OverwritePromptResult Continue = new(OverwritePromptOutcome.Continue);
        public static readonly OverwritePromptResult Abandon = new(OverwritePromptOutcome.Abandon);
        public static readonly OverwritePromptResult EofResult = new(OverwritePromptOutcome.Eof);
    }

    public sealed class HealthProbeResult
    {
        public bool IsHealthy { get; }
        public string ProbeUrl { get; }
        public string? FailureReason { get; }

        private HealthProbeResult(bool healthy, string probeUrl, string? failureReason)
        {
            IsHealthy = healthy;
            ProbeUrl = probeUrl;
            FailureReason = failureReason;
        }

        public static HealthProbeResult Healthy(string url) => new(true, url, null);

        public static HealthProbeResult Unhealthy(string reason) =>
            new(false, string.Empty, reason);
    }

    public sealed class HermesConfigLoadResult
    {
        public bool Success { get; }
        public JsonObject? Root { get; }
        public bool HermesSectionExists { get; }
        public string? ErrorMessage { get; }

        private HermesConfigLoadResult(bool success, JsonObject? root, bool hermesSectionExists, string? errorMessage)
        {
            Success = success;
            Root = root;
            HermesSectionExists = hermesSectionExists;
            ErrorMessage = errorMessage;
        }

        public static HermesConfigLoadResult Loaded(JsonObject root, bool hermesSectionExists) =>
            new(true, root, hermesSectionExists, null);

        public static HermesConfigLoadResult Failed(string message) =>
            new(false, null, false, message);
    }
}
