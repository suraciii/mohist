namespace Mohist.Workflow.Definition;

/// <summary>
/// The logical management surface available to a Manager execution. This
/// catalog is intentionally independent of the model, CLI parser, and HTTP
/// framework so both admission boundaries use the same vocabulary.
/// </summary>
public static class ManagerCapabilityCatalog
{
    public const string ManagerModeEnvironmentVariable = "MOHIST_MANAGER_MODE";
    public const string ManagerModeHeader = "X-Mohist-Manager-Mode";

    public const string WorkspaceStatus = "workspace.status";
    public const string AgentList = "agent.list";
    public const string AgentView = "agent.view";
    public const string AgentCreateOrMount = "agent.create-or-mount";
    public const string ConnectionList = "connection.list";
    public const string ConnectionView = "connection.view";
    public const string ConnectionDiagnostics = "connection.diagnostics";
    public const string ConnectionAccessPolicy = "connection.access-policy";
    public const string ConnectionEnable = "connection.enable";
    public const string ConnectionDisable = "connection.disable";
    public const string OwnerClaim = "owner.claim";
    public const string OwnerTransfer = "owner.transfer";

    public static IReadOnlySet<string> ManagementCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkspaceStatus,
        AgentList,
        AgentView,
        AgentCreateOrMount,
        ConnectionList,
        ConnectionView,
        ConnectionDiagnostics,
        ConnectionAccessPolicy,
        ConnectionEnable,
        ConnectionDisable,
        OwnerClaim,
        OwnerTransfer,
    };

    public static bool IsManagement(string? capability) =>
        capability is not null && ManagementCapabilities.Contains(capability);

    public static bool IsManagerModeValue(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the command forms used by the CLI into logical capabilities.
    /// Options are deliberately inspected for the one compound operation
    /// (<c>slack edit</c>), so presentation edits cannot hide inside the
    /// access-policy capability.
    /// </summary>
    public static string? ResolveCli(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return null;

        if (Equals(args, 0, "slack"))
        {
            var verb = ValueAt(args, 1);
            return verb switch
            {
                "status" => WorkspaceStatus,
                "list" => HasOption(args, "--workspace-team") ? AgentList : ConnectionList,
                // The existing Slack CLI's view command is the diagnostic
                // projection; keep its operator behavior identical in Manager mode.
                "view" => ConnectionDiagnostics,
                "diagnostics" => ConnectionDiagnostics,
                "create" => AgentCreateOrMount,
                "enable" => ConnectionEnable,
                "disable" => ConnectionDisable,
                "claim-owner" => OwnerClaim,
                "transfer-owner" => OwnerTransfer,
                "edit" when HasOption(args, "--access-policy") || HasOption(args, "--allow-member") =>
                    !HasOption(args, "--bot-name") && !HasOption(args, "--avatar-hash")
                        ? ConnectionAccessPolicy
                        : null,
                _ => null,
            };
        }

        if (Equals(args, 0, "agent"))
        {
            return ValueAt(args, 1) switch
            {
                "list" => AgentList,
                "view" => AgentView,
                "create" => AgentCreateOrMount,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Resolves an HTTP request before route target lookup. The route
    /// matcher contains only the management shapes; a Manager-marked request
    /// for any other API is rejected by the Server admission middleware.
    /// </summary>
    public static string? ResolveHttp(string method, string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 3
            && Equals(segments, 0, "api")
            && Equals(segments, 1, "slack-manager")
            && Equals(segments, 2, "status")
            && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return WorkspaceStatus;

        if (segments.Length < 4
            || !Equals(segments, 0, "api")
            || !Equals(segments, 1, "projects"))
            return null;

        if (Equals(segments, 3, "agents"))
        {
            if (segments.Length == 4 && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return AgentList;
            if (segments.Length == 4 && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return AgentCreateOrMount;
            if (segments.Length == 5 && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return AgentView;
            if (segments.Length == 6 && Equals(segments, 5, "status")
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return AgentView;
            return null;
        }

        if (Equals(segments, 3, "slack-manager"))
        {
            if (segments.Length == 5 && Equals(segments, 4, "agents")
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return AgentList;
            if (segments.Length == 5 && Equals(segments, 4, "apps")
                && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return AgentCreateOrMount;
            if (segments.Length == 6 && Equals(segments, 4, "connections")
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return ConnectionView;
            if (segments.Length == 7 && Equals(segments, 4, "connections")
                && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return ConnectionCapability(segments[6]);
            return null;
        }

        if (Equals(segments, 3, "slack-connections"))
        {
            if (segments.Length == 4 && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return ConnectionList;
            if (segments.Length == 5 && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return ConnectionView;
            if (segments.Length == 6 && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                && Equals(segments, 5, "diagnostic"))
                return ConnectionDiagnostics;
            if (segments.Length == 6 && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return ConnectionCapability(segments[5]);
        }

        return null;
    }

    private static string? ConnectionCapability(string operation) => operation switch
    {
        "manage-access" => ConnectionAccessPolicy,
        "enable" => ConnectionEnable,
        "disable" => ConnectionDisable,
        "claim-owner" => OwnerClaim,
        "transfer-owner" => OwnerTransfer,
        _ => null,
    };

    private static bool HasOption(IReadOnlyList<string> args, string option)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, option, StringComparison.Ordinal)
                || arg.StartsWith(option + "=", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? ValueAt(IReadOnlyList<string> args, int index) =>
        index < args.Count ? args[index] : null;

    private static bool Equals(IReadOnlyList<string> values, int index, string expected) =>
        index < values.Count && string.Equals(values[index], expected, StringComparison.Ordinal);
}
