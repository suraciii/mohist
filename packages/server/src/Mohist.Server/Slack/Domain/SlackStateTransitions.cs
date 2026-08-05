namespace Mohist.Server.Slack.Domain;

public static class SlackOAuthStateOutcome
{
    public const string Accepted = "accepted";
    public const string Expired = "expired";
}

public static class SlackOAuthAttemptStatus
{
    public const string Issued = "issued";
    public const string Consumed = "consumed";
    public const string SecretStored = "secret_stored";
    public const string Applied = "applied";
    public const string Expired = "expired";
    public const string RecoveryRequired = "recovery_required";
}

public static class SlackStateTransitions
{
    public static void RequireEnrollmentLifecycleTransition(string current, string next)
    {
        RequireKnown(current, SlackEnrollmentLifecycle.Active, SlackEnrollmentLifecycle.Disabled, SlackEnrollmentLifecycle.Removed);
        RequireKnown(next, SlackEnrollmentLifecycle.Active, SlackEnrollmentLifecycle.Disabled, SlackEnrollmentLifecycle.Removed);
        if (current == next)
            return;
        if (current != SlackEnrollmentLifecycle.Removed
            && (next == SlackEnrollmentLifecycle.Removed
                || (current == SlackEnrollmentLifecycle.Active && next == SlackEnrollmentLifecycle.Disabled)
                || (current == SlackEnrollmentLifecycle.Disabled && next == SlackEnrollmentLifecycle.Active)))
            return;
        throw InvalidTransition("enrollment lifecycle", current, next);
    }

    public static void RequireKnownManagerCapability(string value) =>
        RequireKnown(value,
            SlackManagerCapability.Unknown,
            SlackManagerCapability.Available,
            SlackManagerCapability.Unauthorized,
            SlackManagerCapability.CapacityLimited);

    public static void RequireChildAppLifecycleTransition(string current, string next)
    {
        var known = new[]
        {
            SlackAppLifecycle.NotCreated,
            SlackAppLifecycle.Creating,
            SlackAppLifecycle.CreateUnknown,
            SlackAppLifecycle.Created,
            SlackAppLifecycle.Deleting,
            SlackAppLifecycle.DeleteUnknown,
            SlackAppLifecycle.Deleted,
        };
        RequireKnown(current, known);
        RequireKnown(next, known);
        if (current == next)
            return;
        if (current == SlackAppLifecycle.NotCreated && next == SlackAppLifecycle.Creating
            || current == SlackAppLifecycle.Creating && IsOneOf(next, SlackAppLifecycle.Created, SlackAppLifecycle.CreateUnknown, SlackAppLifecycle.NotCreated)
            || current == SlackAppLifecycle.CreateUnknown && IsOneOf(next, SlackAppLifecycle.Created, SlackAppLifecycle.NotCreated, SlackAppLifecycle.Creating)
            || current == SlackAppLifecycle.Created && next == SlackAppLifecycle.Deleting
            || current == SlackAppLifecycle.Deleting && IsOneOf(next, SlackAppLifecycle.Deleted, SlackAppLifecycle.DeleteUnknown, SlackAppLifecycle.Created)
            || current == SlackAppLifecycle.DeleteUnknown && IsOneOf(next, SlackAppLifecycle.Deleted, SlackAppLifecycle.Created, SlackAppLifecycle.Deleting))
            return;
        throw InvalidTransition("Child App lifecycle", current, next);
    }

    public static void RequireAuthorizationTransition(string current, string next)
    {
        var known = new[]
        {
            SlackAuthorizationState.NotStarted,
            SlackAuthorizationState.AwaitingUser,
            SlackAuthorizationState.PendingAdmin,
            SlackAuthorizationState.Authorized,
            SlackAuthorizationState.ExpiredOrCancelled,
            SlackAuthorizationState.Revoked,
        };
        RequireKnown(current, known);
        RequireKnown(next, known);
        if (current == next)
            return;
        if (current == SlackAuthorizationState.NotStarted
            && IsOneOf(next, SlackAuthorizationState.AwaitingUser, SlackAuthorizationState.PendingAdmin, SlackAuthorizationState.Authorized, SlackAuthorizationState.ExpiredOrCancelled)
            || current == SlackAuthorizationState.AwaitingUser
            && IsOneOf(next, SlackAuthorizationState.PendingAdmin, SlackAuthorizationState.Authorized, SlackAuthorizationState.ExpiredOrCancelled)
            || current == SlackAuthorizationState.PendingAdmin
            && IsOneOf(next, SlackAuthorizationState.AwaitingUser, SlackAuthorizationState.Authorized, SlackAuthorizationState.ExpiredOrCancelled)
            || current == SlackAuthorizationState.Authorized && next == SlackAuthorizationState.Revoked
            || current == SlackAuthorizationState.ExpiredOrCancelled && next == SlackAuthorizationState.AwaitingUser
            || current == SlackAuthorizationState.Revoked && IsOneOf(next, SlackAuthorizationState.AwaitingUser, SlackAuthorizationState.Authorized))
            return;
        throw InvalidTransition("authorization", current, next);
    }

    public static void RequireManagerTransportKind(string value) =>
        RequireKnown(value, SlackManagerTransportKind.Socket);

    public static void RequireManagerReadiness(string value) =>
        RequireKnown(value,
            SlackManagerReadiness.Unknown,
            SlackManagerReadiness.Ready,
            SlackManagerReadiness.NotReady,
            SlackManagerReadiness.Degraded);

    public static void RequireManagerAppLifecycleTransition(string current, string next)
    {
        var known = new[]
        {
            SlackManagerAppLifecycle.NotCreated,
            SlackManagerAppLifecycle.Creating,
            SlackManagerAppLifecycle.Created,
            SlackManagerAppLifecycle.CreateUnknown,
        };
        RequireKnown(current, known);
        RequireKnown(next, known);
        if (current == next)
            return;
        if (current == SlackManagerAppLifecycle.NotCreated && next == SlackManagerAppLifecycle.Creating
            || current == SlackManagerAppLifecycle.Creating
            && IsOneOf(next, SlackManagerAppLifecycle.Created, SlackManagerAppLifecycle.CreateUnknown)
            || current == SlackManagerAppLifecycle.CreateUnknown
            && IsOneOf(next, SlackManagerAppLifecycle.Creating, SlackManagerAppLifecycle.Created))
            return;
        throw InvalidTransition("Manager App lifecycle", current, next);
    }

    public static void RequireRuntimeCredentialValidationTransition(string current, string next)
    {
        var known = new[]
        {
            SlackRuntimeCredentialValidationState.NotProvided,
            SlackRuntimeCredentialValidationState.Candidate,
            SlackRuntimeCredentialValidationState.AwaitingSocket,
            SlackRuntimeCredentialValidationState.Verified,
            SlackRuntimeCredentialValidationState.Failed,
        };
        RequireKnown(current, known);
        RequireKnown(next, known);
        if (current == next)
            return;
        if (current == SlackRuntimeCredentialValidationState.NotProvided
            && next == SlackRuntimeCredentialValidationState.Candidate
            || current == SlackRuntimeCredentialValidationState.Candidate
            && IsOneOf(next, SlackRuntimeCredentialValidationState.AwaitingSocket, SlackRuntimeCredentialValidationState.Failed)
            || current == SlackRuntimeCredentialValidationState.AwaitingSocket
            && IsOneOf(next, SlackRuntimeCredentialValidationState.Verified, SlackRuntimeCredentialValidationState.Failed)
            || current == SlackRuntimeCredentialValidationState.Failed
            && next == SlackRuntimeCredentialValidationState.Candidate)
            return;
        throw InvalidTransition("runtime credential validation", current, next);
    }

    public static void RequireBindingTransition(string current, string next)
    {
        RequireKnownBinding(current);
        RequireKnownBinding(next);
        if (current == next)
            return;
        if (current == SlackChildAppBindingState.Pending
            && IsOneOf(next, SlackChildAppBindingState.InProgress, SlackChildAppBindingState.Bound, SlackChildAppBindingState.ConnectionDeleted, SlackChildAppBindingState.Conflict)
            || current == SlackChildAppBindingState.InProgress && IsOneOf(next, SlackChildAppBindingState.Bound, SlackChildAppBindingState.ConnectionDeleted, SlackChildAppBindingState.Conflict)
            || current == SlackChildAppBindingState.Bound && next == SlackChildAppBindingState.ConnectionDeleted
            || IsOneOf(current, SlackChildAppBindingState.ConnectionDeleted, SlackChildAppBindingState.Conflict)
                && IsOneOf(next, SlackChildAppBindingState.Pending, SlackChildAppBindingState.InProgress))
            return;
        throw InvalidTransition("Child App binding", current, next);
    }

    public static void RequireOAuthAttemptTransition(string current, string next)
    {
        var known = new[]
        {
            SlackOAuthAttemptStatus.Issued,
            SlackOAuthAttemptStatus.Consumed,
            SlackOAuthAttemptStatus.SecretStored,
            SlackOAuthAttemptStatus.Applied,
            SlackOAuthAttemptStatus.Expired,
            SlackOAuthAttemptStatus.RecoveryRequired,
        };
        RequireKnown(current, known);
        RequireKnown(next, known);
        if (current == next)
            return;
        if (current == SlackOAuthAttemptStatus.Issued
            && IsOneOf(next, SlackOAuthAttemptStatus.Consumed, SlackOAuthAttemptStatus.Expired)
            || current == SlackOAuthAttemptStatus.Consumed
            && IsOneOf(next, SlackOAuthAttemptStatus.SecretStored, SlackOAuthAttemptStatus.RecoveryRequired)
            || current == SlackOAuthAttemptStatus.SecretStored
            && IsOneOf(next, SlackOAuthAttemptStatus.Applied, SlackOAuthAttemptStatus.RecoveryRequired)
            || current == SlackOAuthAttemptStatus.RecoveryRequired
            && IsOneOf(next, SlackOAuthAttemptStatus.SecretStored, SlackOAuthAttemptStatus.Applied))
            return;
        throw InvalidTransition("OAuth attempt", current, next);
    }

    public static void RequireOAuthOutcome(string? value)
    {
        if (value is not null && !IsOneOf(value, SlackOAuthStateOutcome.Accepted, SlackOAuthStateOutcome.Expired))
            throw new ArgumentException($"Unknown OAuth state outcome '{value}'.", nameof(value));
    }

    public static void RequireBindingObligationStatus(string value) => RequireKnownBinding(value);

    private static void RequireKnownBinding(string value) =>
        RequireKnown(value,
            SlackChildAppBindingState.Pending,
            SlackChildAppBindingState.InProgress,
            SlackChildAppBindingState.Bound,
            SlackChildAppBindingState.ConnectionDeleted,
            SlackChildAppBindingState.Conflict);

    private static void RequireKnown(string value, params string[] allowed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!IsOneOf(value, allowed))
            throw new ArgumentException($"Unknown Slack state '{value}'.", nameof(value));
    }

    private static bool IsOneOf(string value, params string[] allowed) => allowed.Contains(value, StringComparer.Ordinal);

    private static InvalidOperationException InvalidTransition(string name, string current, string next) =>
        new($"Invalid {name} transition from '{current}' to '{next}'.");
}
