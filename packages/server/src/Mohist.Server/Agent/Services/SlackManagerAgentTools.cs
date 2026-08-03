namespace Mohist.Server.Agent.Services;

public static class SlackManagerAgentTools
{
    public const string List = "list";
    public const string View = "view";
    public const string Create = "create";
    public const string ClaimOwner = "claim-owner";
    public const string Edit = "edit";
    public const string Enable = "enable";
    public const string Disable = "disable";
    public const string TransferOwner = "transfer-owner";
    public const string Diagnostics = "diagnostics";

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        List,
        View,
        Create,
        ClaimOwner,
        Edit,
        Enable,
        Disable,
        TransferOwner,
        Diagnostics,
    };

    public static bool IsAllowed(string? tool) =>
        tool is not null && Allowed.Contains(tool.Trim());

    public static bool IsForbidden(string? tool) => tool is
        "remove-binding" or "delete" or "permanent-delete" or "configure" or "rotate-credentials";
}
