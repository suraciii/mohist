using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

/// <summary>
/// <c>mo audit list</c>: the auth audit trail — credential
/// issuance/revocation, enrollment tokens,
/// device approvals and sessions, newest first. Records never contain
/// token values, so nothing this command prints can leak a credential.
/// </summary>
internal static class AuditCommands
{
    public static Command Build(MohistCliApi api)
    {
        var audit = new Command(
            "audit",
            "Authentication audit trail: credential issuance/revocation, enrollment tokens, approvals and sessions.");
        audit.Subcommands.Add(BuildList(api));
        return audit;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List auth audit events (newest first; records never contain token values)");
        var kind = new Option<string?>("--kind")
        {
            Description = "Event kind: credentialIssued, credentialRevoked, enrollmentTokenIssued, enrollmentTokenConsumed, deviceApproved or sessionEstablished",
        };
        var since = new Option<string?>("--since")
        {
            Description = "Only events at or after this UTC timestamp (ISO 8601)",
        };
        var limit = new Option<int?>("--limit")
        {
            Description = "Maximum number of events (default 100)",
        };
        cmd.Options.Add(kind);
        cmd.Options.Add(since);
        cmd.Options.Add(limit);
        cmd.SetAction(async ctx =>
        {
            var kindValue = ctx.GetValue(kind)?.Trim();
            var sinceValue = ctx.GetValue(since)?.Trim();
            var limitValue = ctx.GetValue(limit);

            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(kindValue))
                query.Add($"kind={Uri.EscapeDataString(kindValue)}");
            if (!string.IsNullOrWhiteSpace(sinceValue))
                query.Add($"since={Uri.EscapeDataString(sinceValue)}");
            if (limitValue is not null)
                query.Add($"limit={limitValue}");
            var path = query.Count == 0
                ? "/api/audit/events"
                : $"/api/audit/events?{string.Join("&", query)}";

            var (exit, data) = await api.GetDataOrPrintErrorAsync(path);
            if (exit != 0 || data is null)
                return exit;

            var events = data["events"] as JsonArray ?? new JsonArray();
            if (events.Count == 0)
            {
                api.Output.WriteLine("No audit events");
                return 0;
            }

            var headers = new[] { "TIME", "EVENT", "SUBJECT", "TARGET", "DETAIL" };
            var widths = new[] { 28, 24, 16, 36, 40 };
            var cells = new List<string[]>();
            foreach (var auditEvent in events.OfType<JsonObject>())
            {
                var metadata = auditEvent["metadata"] as JsonObject;
                var detail = metadata is null
                    ? ""
                    : string.Join(" ", metadata.Select(entry =>
                        $"{entry.Key}={entry.Value?.GetValue<string>() ?? ""}"));
                cells.Add(new[]
                {
                    auditEvent["occurredAt"]?.GetValue<string>() ?? "",
                    auditEvent["eventType"]?.GetValue<string>() ?? "",
                    auditEvent["subjectId"]?.GetValue<string>() ?? "",
                    $"{auditEvent["targetKind"]?.GetValue<string>() ?? ""}:{auditEvent["targetId"]?.GetValue<string>() ?? ""}",
                    detail,
                });
            }

            AuthCommands.WriteTable(api.Output, headers, widths, cells);
            return 0;
        });
        return cmd;
    }
}
