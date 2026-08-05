namespace Mohist.Server.Slack.Domain;

public sealed record ManagedSlackAgentAppStatus(
    string AppLifecycle,
    string Authorization,
    string ManifestState,
    string TransportReadiness,
    string NextAction);

public static class ManagedSlackAgentAppStatusDeriver
{
    public static ManagedSlackAgentAppStatus Derive(ManagedSlackAgentApp agentApp)
    {
        ArgumentNullException.ThrowIfNull(agentApp);
        ValidateKnownState(agentApp);
        var manifestState = DeriveManifestState(agentApp);
        var transportReadiness = DeriveTransportReadiness(agentApp);
        var nextAction = DeriveNextAction(agentApp, manifestState, transportReadiness);
        return new(agentApp.AppLifecycle, agentApp.Authorization, manifestState, transportReadiness, nextAction);
    }

    private static void ValidateKnownState(ManagedSlackAgentApp agentApp)
    {
        SlackStateTransitions.RequireAgentAppLifecycleTransition(agentApp.AppLifecycle, agentApp.AppLifecycle);
        SlackStateTransitions.RequireAuthorizationTransition(agentApp.Authorization, agentApp.Authorization);
        SlackStateTransitions.RequireBindingTransition(agentApp.BindingState, agentApp.BindingState);
    }

    public static string DeriveManifestState(ManagedSlackAgentApp agentApp) => DeriveManifestState(
        agentApp.DesiredManifestVersion,
        agentApp.DesiredManifestHash,
        agentApp.AppliedManifestVersion,
        agentApp.AppliedManifestHash);

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

    public static string DeriveTransportReadiness(ManagedSlackAgentApp agentApp) => DeriveTransportReadiness(
        agentApp.AppLevelTokenRef,
        agentApp.BotTokenRef);

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
        ManagedSlackAgentApp agentApp,
        string manifestState,
        string transportReadiness) => DeriveNextAction(
            agentApp.AppLifecycle,
            agentApp.Authorization,
            manifestState,
            transportReadiness,
            agentApp.BindingState);

    public static string DeriveNextAction(
        string appLifecycle,
        string authorization,
        string manifestState,
        string transportReadiness,
        string bindingState)
    {
        if (appLifecycle == SlackAppLifecycle.CreateUnknown)
            return SlackAgentAppNextAction.ReconcileCreate;
        if (appLifecycle == SlackAppLifecycle.DeleteUnknown)
            return SlackAgentAppNextAction.ReconcileDelete;
        if (appLifecycle == SlackAppLifecycle.NotCreated)
            return SlackAgentAppNextAction.CreateAgentApp;
        if (appLifecycle is SlackAppLifecycle.Creating or SlackAppLifecycle.Deleting)
            return SlackAgentAppNextAction.WaitForOperation;
        if (appLifecycle == SlackAppLifecycle.Deleted)
            return SlackAgentAppNextAction.Deleted;
        if (authorization != SlackAuthorizationState.Authorized)
            return SlackAgentAppNextAction.AuthorizeAgentApp;
        if (manifestState != SlackManifestState.Applied)
            return SlackAgentAppNextAction.ApplyManifest;
        if (transportReadiness != SlackTransportReadiness.Ready)
            return SlackAgentAppNextAction.ConfigureSocketCredentials;
        if (bindingState != SlackAgentAppBindingState.Bound)
            return SlackAgentAppNextAction.BindConnection;
        return SlackAgentAppNextAction.Ready;
    }
}

public static class SlackAgentAppBindingState
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Bound = "bound";
    public const string ConnectionDeleted = "connection_deleted";
    public const string Conflict = "conflict";
}

public static class SlackAgentAppBindingObligationStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Bound = "bound";
    public const string ConnectionDeleted = "connection_deleted";
    public const string Conflict = "conflict";
}

public static class SlackAgentAppNextAction
{
    public const string ReconcileCreate = "reconcile_create";
    public const string ReconcileDelete = "reconcile_delete";
    public const string CreateAgentApp = "create_child_app";
    public const string WaitForOperation = "wait_for_operation";
    public const string AuthorizeAgentApp = "authorize_child_app";
    public const string ApplyManifest = "apply_manifest";
    public const string ConfigureSocketCredentials = "configure_socket_credentials";
    public const string ProvideCredentials = "provide_credentials";
    public const string BindConnection = "bind_connection";
    public const string Ready = "ready";
    public const string Deleted = "deleted";
}
