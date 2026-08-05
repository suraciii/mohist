namespace Mohist.Server.Slack.Domain;

public sealed record ManagedSlackAgentAppStatus(
    string AppLifecycle,
    string Authorization,
    string ManifestState,
    string TransportReadiness,
    string NextAction);

public static class ManagedSlackAgentAppStatusDeriver
{
    public static ManagedSlackAgentAppStatus Derive(ManagedSlackAgentApp child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ValidateKnownState(child);
        var manifestState = DeriveManifestState(child);
        var transportReadiness = DeriveTransportReadiness(child);
        var nextAction = DeriveNextAction(child, manifestState, transportReadiness);
        return new(child.AppLifecycle, child.Authorization, manifestState, transportReadiness, nextAction);
    }

    private static void ValidateKnownState(ManagedSlackAgentApp child)
    {
        SlackStateTransitions.RequireChildAppLifecycleTransition(child.AppLifecycle, child.AppLifecycle);
        SlackStateTransitions.RequireAuthorizationTransition(child.Authorization, child.Authorization);
        SlackStateTransitions.RequireBindingTransition(child.BindingState, child.BindingState);
    }

    public static string DeriveManifestState(ManagedSlackAgentApp child) => DeriveManifestState(
        child.DesiredManifestVersion,
        child.DesiredManifestHash,
        child.AppliedManifestVersion,
        child.AppliedManifestHash);

    public static string DeriveManifestState(
        int desiredManifestVersion,
        string? desiredManifestHash,
        int? appliedManifestVersion,
        string? appliedManifestHash) =>
        desiredManifestVersion > 0
            && !string.IsNullOrWhiteSpace(desiredManifestHash)
            && appliedManifestVersion == desiredManifestVersion
            && string.Equals(appliedManifestHash, desiredManifestHash, StringComparison.Ordinal)
            ? SlackManifestState.Applied
            : appliedManifestVersion is null || string.IsNullOrWhiteSpace(appliedManifestHash)
                ? SlackManifestState.Desired
                : SlackManifestState.DriftKnown;

    public static string DeriveTransportReadiness(ManagedSlackAgentApp child) => DeriveTransportReadiness(
        child.AppLevelTokenRef,
        child.BotTokenRef);

    public static string DeriveTransportReadiness(
        string? appLevelTokenRef,
        string? botTokenRef)
    {
        return !string.IsNullOrWhiteSpace(appLevelTokenRef)
            && !string.IsNullOrWhiteSpace(botTokenRef)
            ? SlackTransportReadiness.Ready
            : SlackTransportReadiness.NotReady;
    }

    public static string DeriveNextAction(
        ManagedSlackAgentApp child,
        string manifestState,
        string transportReadiness) => DeriveNextAction(
            child.AppLifecycle,
            child.Authorization,
            manifestState,
            transportReadiness,
            child.BindingState);

    public static string DeriveNextAction(
        string appLifecycle,
        string authorization,
        string manifestState,
        string transportReadiness,
        string bindingState)
    {
        if (appLifecycle == SlackAppLifecycle.CreateUnknown)
            return SlackChildAppNextAction.ReconcileCreate;
        if (appLifecycle == SlackAppLifecycle.DeleteUnknown)
            return SlackChildAppNextAction.ReconcileDelete;
        if (appLifecycle == SlackAppLifecycle.NotCreated)
            return SlackChildAppNextAction.CreateChildApp;
        if (appLifecycle is SlackAppLifecycle.Creating or SlackAppLifecycle.Deleting)
            return SlackChildAppNextAction.WaitForOperation;
        if (appLifecycle == SlackAppLifecycle.Deleted)
            return SlackChildAppNextAction.Deleted;
        if (authorization != SlackAuthorizationState.Authorized)
            return SlackChildAppNextAction.AuthorizeChildApp;
        if (manifestState != SlackManifestState.Applied)
            return SlackChildAppNextAction.ApplyManifest;
        if (transportReadiness != SlackTransportReadiness.Ready)
            return SlackChildAppNextAction.ConfigureSocketCredentials;
        if (bindingState != SlackChildAppBindingState.Bound)
            return SlackChildAppNextAction.BindConnection;
        return SlackChildAppNextAction.Ready;
    }
}

public static class SlackChildAppBindingState
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Bound = "bound";
    public const string ConnectionDeleted = "connection_deleted";
    public const string Conflict = "conflict";
}

public static class SlackChildAppBindingObligationStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Bound = "bound";
    public const string ConnectionDeleted = "connection_deleted";
    public const string Conflict = "conflict";
}

public static class SlackChildAppNextAction
{
    public const string ReconcileCreate = "reconcile_create";
    public const string ReconcileDelete = "reconcile_delete";
    public const string CreateChildApp = "create_child_app";
    public const string WaitForOperation = "wait_for_operation";
    public const string AuthorizeChildApp = "authorize_child_app";
    public const string ApplyManifest = "apply_manifest";
    public const string ConfigureSocketCredentials = "configure_socket_credentials";
    public const string BindConnection = "bind_connection";
    public const string Ready = "ready";
    public const string Deleted = "deleted";
}
