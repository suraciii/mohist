using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

/// <summary>
/// <c>mo auth token</c>: personal access tokens for scripts, CI and
/// external agents. The full token value is printed only by
/// <c>create</c>; <c>list</c> shows name + prefix and <c>revoke</c>
/// invalidates a token immediately.
/// </summary>
internal static class AuthCommands
{
    private const int DefaultTtlHours = 24 * 90;
    private const int MaxTtlHours = 24 * 365;

    public static Command Build(MohistCliApi api)
    {
        var auth = new Command(
            "auth",
            "Authentication: sign in on this machine, manage the local session and personal access tokens (PAT) for scripts, CI and external agents.");

        auth.Subcommands.Add(BuildLogin(api));
        auth.Subcommands.Add(BuildStatus(api));
        auth.Subcommands.Add(BuildLogout(api));

        var token = new Command("token", "Manage personal access tokens");
        token.Subcommands.Add(BuildCreate(api));
        token.Subcommands.Add(BuildList(api));
        token.Subcommands.Add(BuildRevoke(api));
        auth.Subcommands.Add(token);

        return auth;
    }

    private static Command BuildCreate(MohistCliApi api)
    {
        var cmd = new Command("create", "Issue a personal access token. The full token is shown only once.");
        var name = new Option<string>("--name") { Description = "Token name; unique among active tokens of this account" };
        var scope = new Option<string>("--scope")
        {
            Description = "Token scope: operator (default) or readonly",
            DefaultValueFactory = _ => "operator",
        };
        var ttl = new Option<int?>("--ttl")
        {
            Description = $"Lifetime in hours (default {DefaultTtlHours}, max {MaxTtlHours}); a token can never be permanent",
        };
        var project = new Option<string[]?>("--project")
        {
            Description = "Grant direct Agent API access to a private Project; repeat for multiple Projects",
            AllowMultipleArgumentsPerToken = true,
        };
        var allProjects = new Option<bool>("--all-projects")
        {
            Description = "Grant direct Agent API access to all current private Projects (operator scope only)",
        };
        cmd.Options.Add(name);
        cmd.Options.Add(scope);
        cmd.Options.Add(ttl);
        cmd.Options.Add(project);
        cmd.Options.Add(allProjects);
        cmd.SetAction(async ctx =>
        {
            var tokenName = ctx.GetValue(name);
            if (string.IsNullOrWhiteSpace(tokenName))
            {
                api.Error.WriteLine("--name is required");
                return 1;
            }

            tokenName = tokenName.Trim();
            var scopeValue = ctx.GetValue(scope)?.Trim();
            if (!string.Equals(scopeValue, "operator", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(scopeValue, "readonly", StringComparison.OrdinalIgnoreCase))
            {
                api.Error.WriteLine("--scope must be 'operator' or 'readonly'");
                return 1;
            }

            var projectIds = ctx.GetValue(project) ?? [];
            var grantAllProjects = ctx.GetValue(allProjects);
            if (projectIds.Length > 0 && grantAllProjects)
            {
                api.Error.WriteLine("--project cannot be combined with --all-projects");
                return 1;
            }

            if (grantAllProjects && !string.Equals(scopeValue, "operator", StringComparison.OrdinalIgnoreCase))
            {
                api.Error.WriteLine("--all-projects requires --scope operator");
                return 1;
            }

            if (projectIds.Any(string.IsNullOrWhiteSpace))
            {
                api.Error.WriteLine("--project requires a project id");
                return 1;
            }

            var ttlHours = ctx.GetValue(ttl);
            if (ttlHours is < 1 or > MaxTtlHours)
            {
                api.Error.WriteLine($"--ttl must be between 1 and {MaxTtlHours} hours");
                return 1;
            }

            var result = await api.PostAndReadAsync("/api/auth/tokens", new
            {
                name = tokenName,
                scope = scopeValue!.ToLowerInvariant(),
                ttlHours,
                projectIds = projectIds.Length == 0 ? null : projectIds,
                allProjects = grantAllProjects,
            });
            return result.ExitCode;
        });
        return cmd;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List personal access tokens (name and prefix only — never full values)");
        cmd.SetAction(async _ =>
        {
            var (exit, data) = await api.GetDataOrPrintErrorAsync("/api/auth/tokens");
            if (exit != 0 || data is null)
                return exit;

            var tokens = data["tokens"] as JsonArray ?? new JsonArray();
            if (tokens.Count == 0)
            {
                api.Output.WriteLine("No tokens");
                return 0;
            }

            var headers = new[] { "NAME", "PREFIX", "SCOPE", "EXPIRES", "STATUS" };
            var widths = new[] { 24, 24, 12, 28, 10 };
            var cells = new List<string[]>();
            foreach (var token in tokens.OfType<JsonObject>())
            {
                var name = token["name"]?.GetValue<string>() ?? "";
                var prefix = token["prefix"]?.GetValue<string>() ?? "";
                var scopes = token["scopes"] is JsonArray scopeArray
                    ? string.Join(",", scopeArray.Select(scope => scope?.GetValue<string>()))
                    : "";
                var expires = token["expiresAt"]?.GetValue<string>() ?? "";
                var status = token["revokedAt"] is null ? "active" : "revoked";
                cells.Add(new[] { name, prefix, scopes, expires, status });
            }

            WriteTable(api.Output, headers, widths, cells);
            return 0;
        });
        return cmd;
    }

    private static Command BuildRevoke(MohistCliApi api)
    {
        var cmd = new Command("revoke", "Revoke a personal access token; it stops working immediately");
        var name = new Argument<string>("name") { Description = "Token name" };
        cmd.Arguments.Add(name);
        cmd.SetAction(async ctx =>
        {
            var tokenName = ctx.GetValue(name);
            if (string.IsNullOrWhiteSpace(tokenName))
            {
                api.Error.WriteLine("name is required");
                return 1;
            }

            var result = await api.PostAndReadAsync(
                $"/api/auth/tokens/{Uri.EscapeDataString(tokenName)}/revoke",
                new { });
            return result.ExitCode;
        });
        return cmd;
    }

    internal static void WriteTable(
        TextWriter output,
        string[] headers,
        int[] widths,
        List<string[]> cells)
    {
        output.WriteLine(string.Join("  ", headers.Select((header, index) => Pad(header, widths[index]))));
        foreach (var cell in cells)
            output.WriteLine(string.Join("  ", cell.Select((value, index) => Pad(value, widths[index]))));
    }

    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value.PadRight(width);

    internal const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private static Command BuildLogin(MohistCliApi api)
    {
        var cmd = new Command(
            "login",
            "Sign in to the Mohist server: confirm the code in the browser, then the session is stored locally and renews itself.");
        cmd.SetAction(async ctx =>
        {
            var ct = api.Invocation.CancellationToken;
            var server = CliCredentialFile.NormalizeServer(api.Http.BaseAddress?.ToString() ?? "");

            var flow = await PostDeviceAsync(api, "/api/auth/device/code", new { name = Environment.MachineName }, ct)
                .ConfigureAwait(false);
            if (flow.Error is not null)
            {
                api.Error.WriteLine(flow.Error);
                return 1;
            }
            if (flow.Data is not JsonObject data)
            {
                api.Error.WriteLine(MohistCliApi.ServerUnavailableMessage);
                return 1;
            }

            var deviceCode = data["deviceCode"]?.GetValue<string>() ?? "";
            var userCode = data["userCode"]?.GetValue<string>() ?? "";
            var verificationUriComplete = data["verificationUriComplete"]?.GetValue<string>() ?? "";
            var interval = data["interval"]?.GetValue<int>() ?? 5;
            if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
            {
                api.Error.WriteLine("The server returned an invalid device authorization response.");
                return 1;
            }

            api.Output.WriteLine("Open the confirmation page in your browser:");
            api.Output.WriteLine($"  {verificationUriComplete}");
            api.Output.WriteLine();
            api.Output.WriteLine("Enter this code on the confirmation page:");
            api.Output.WriteLine();
            api.Output.WriteLine($"  {DisplayUserCode(userCode)}");
            api.Output.WriteLine();

            await TryOpenBrowserAsync(api, verificationUriComplete, ct).ConfigureAwait(false);

            var pollInterval = interval;
            while (true)
            {
                var poll = await PostDeviceAsync(api, "/api/auth/token", new
                {
                    grant_type = DeviceCodeGrantType,
                    device_code = deviceCode,
                }, ct).ConfigureAwait(false);
                if (poll.Data is JsonObject tokens)
                {
                    var accessToken = tokens["accessToken"]?.GetValue<string>() ?? "";
                    var refreshToken = tokens["refreshToken"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
                    {
                        api.Error.WriteLine("The server returned an invalid session response.");
                        return 1;
                    }

                    var stored = new StoredCliCredential(
                        server,
                        accessToken,
                        refreshToken,
                        tokens["accessExpiresAt"]?.GetValue<DateTimeOffset>() ?? default,
                        tokens["refreshExpiresAt"]?.GetValue<DateTimeOffset>() ?? default);
                    try
                    {
                        await new CliCredentialFile(api.FileSystem, CliCredentialFile.PathFor(api.GetUserHome))
                            .SaveAsync(stored).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        api.Error.WriteLine($"The session could not be stored: {ex.Message}");
                        return 1;
                    }

                    api.Output.WriteLine($"Logged in to {server}.");
                    return 0;
                }

                switch (poll.Code)
                {
                    case "authorization_pending":
                        break;
                    case "slow_down":
                        pollInterval += 5;
                        break;
                    case "expired_token":
                        api.Error.WriteLine("The authorization code expired. Run 'mo auth login' again.");
                        return 1;
                    case "access_denied":
                        api.Error.WriteLine("The authorization was denied.");
                        return 1;
                    case "invalid_grant":
                        api.Error.WriteLine("The device authorization is no longer valid. Run 'mo auth login' again.");
                        return 1;
                    default:
                        api.Error.WriteLine(poll.Error ?? "Sign-in failed.");
                        return 1;
                }

                await api.PollWait(TimeSpan.FromSeconds(pollInterval), ct).ConfigureAwait(false);
            }
        });
        return cmd;
    }

    private static Command BuildStatus(MohistCliApi api)
    {
        var cmd = new Command("status", "Show the credential source and session state for the current server");
        cmd.SetAction(async ctx =>
        {
            var server = CliCredentialFile.NormalizeServer(api.Http.BaseAddress?.ToString() ?? "");
            var credential = await ResolveCredentialAsync(api, server).ConfigureAwait(false);
            if (credential is null)
            {
                api.Output.WriteLine($"Not signed in to {server}.");
                api.Output.WriteLine("Run 'mo auth login' to sign in.");
                return 1;
            }

            api.Output.WriteLine($"Server: {credential.Server ?? server}");
            api.Output.WriteLine($"Identity: {credential.Identity}");
            api.Output.WriteLine($"Credential: {credential.Source}");
            api.Output.WriteLine($"Access expires: {credential.AccessExpiresAt:O}");
            if (credential.RefreshExpiresAt is not null)
                api.Output.WriteLine($"Session renews until: {credential.RefreshExpiresAt:O}");

            var live = await ProbeSessionAsync(api).ConfigureAwait(false);
            api.Output.WriteLine(live switch
            {
                true => "Session: active",
                false => "Session: expired — run 'mo auth login' to sign in again",
                null => "Session: server unreachable",
            });
            return 0;
        });
        return cmd;
    }

    private static Command BuildLogout(MohistCliApi api)
    {
        var cmd = new Command("logout", "Revoke the local session on the server and clear it from this machine");
        cmd.SetAction(async _ =>
        {
            var server = CliCredentialFile.NormalizeServer(api.Http.BaseAddress?.ToString() ?? "");
            var file = new CliCredentialFile(api.FileSystem, CliCredentialFile.PathFor(api.GetUserHome));
            var stored = await file.FindAsync(server).ConfigureAwait(false);
            if (stored is null)
            {
                api.Output.WriteLine($"No local session for {server}.");
                return 0;
            }

            try
            {
                await api.PostAndReadAsync("/api/auth/logout", new { refreshToken = stored.RefreshToken })
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The server is unreachable; the local session is still
                // cleared so the user is not stuck with dead credentials.
            }

            await file.RemoveAsync(server).ConfigureAwait(false);
            api.Output.WriteLine($"Logged out of {server}.");
            return 0;
        });
        return cmd;
    }

    private static async Task<StatusCredential?> ResolveCredentialAsync(MohistCliApi api, string server)
    {
        var environment = api.Invocation.Environment;
        var token = environment.Get(CliCredentialProvider.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            return new StatusCredential(server, "admin", "MOHIST_TOKEN", null, null);

        var file = new CliCredentialFile(api.FileSystem, CliCredentialFile.PathFor(api.GetUserHome));
        var stored = await file.FindAsync(server).ConfigureAwait(false);
        if (stored is not null)
            return new StatusCredential(
                stored.Server, "admin", "device session (credentials.json)",
                stored.AccessExpiresAt, stored.RefreshExpiresAt);

        var adminEnvironment = environment.Get(CliCredentialProvider.AdminTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(adminEnvironment))
            return new StatusCredential(server, "admin", "machine-local admin credential", null, null);

        var adminPath = environment.Get(CliCredentialProvider.AdminTokenPathEnvironmentVariable);
        var home = api.GetUserHome();
        var defaultPath = System.IO.Path.Combine(home, ".mohist", CliCredentialProvider.DefaultTokenFileName);
        if (api.FileSystem.Exists(string.IsNullOrWhiteSpace(adminPath) ? defaultPath : adminPath))
            return new StatusCredential(server, "admin", "machine-local admin credential", null, null);

        return null;
    }

    private static async Task<bool?> ProbeSessionAsync(MohistCliApi api)
    {
        try
        {
            using var response = await api.Http.GetAsync("/api/auth/session").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Posts to a device-flow endpoint without printing the envelope:
    /// polling failures (authorization_pending, slow_down) are part of
    /// the protocol, not command errors.
    /// </summary>
    private static async Task<DevicePostResult> PostDeviceAsync(
        MohistCliApi api, string path, object body, CancellationToken ct)
    {
        try
        {
            using var response = await api.Http.PostAsync(
                path,
                JsonContent.Create(body),
                ct).ConfigureAwait(false);
            var node = await JsonNode.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                cancellationToken: ct).ConfigureAwait(false);
            if (node is not JsonObject envelope)
                return new DevicePostResult(null, null, "The server returned an invalid response.");
            if (envelope["success"]?.GetValue<bool>() != true)
                return new DevicePostResult(
                    null,
                    envelope["code"]?.GetValue<string>(),
                    envelope["error"]?.GetValue<string>() ?? "Sign-in failed.");
            return new DevicePostResult(envelope["data"] as JsonObject, null, null);
        }
        catch (HttpRequestException)
        {
            return new DevicePostResult(null, null, MohistCliApi.ServerUnavailableMessage);
        }
    }

    private static async Task TryOpenBrowserAsync(MohistCliApi api, string url, CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                await api.CommandExecutor.ExecuteAsync("cmd.exe", ["/c", "start", "", url], cancellationToken: ct).ConfigureAwait(false);
            else if (OperatingSystem.IsMacOS())
                await api.CommandExecutor.ExecuteAsync("open", [url], cancellationToken: ct).ConfigureAwait(false);
            else
                await api.CommandExecutor.ExecuteAsync("xdg-open", [url], cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            // No browser available — the printed link is the fallback.
        }
    }

    private static string DisplayUserCode(string userCode) =>
        userCode.Length == 8 ? $"{userCode[..4]}-{userCode[4..]}" : userCode;

    private sealed record DevicePostResult(JsonObject? Data, string? Code, string? Error);

    private sealed record StatusCredential(
        string Server,
        string Identity,
        string Source,
        DateTimeOffset? AccessExpiresAt,
        DateTimeOffset? RefreshExpiresAt);
}
