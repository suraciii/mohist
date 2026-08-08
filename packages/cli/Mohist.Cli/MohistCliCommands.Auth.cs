using System.CommandLine;
using System.CommandLine.Invocation;
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
            "Authentication: personal access tokens (PAT) for scripts, CI and external agents.");

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
        cmd.Options.Add(name);
        cmd.Options.Add(scope);
        cmd.Options.Add(ttl);
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
}
