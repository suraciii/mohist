namespace Mohist.Server.Slack.Domain;

public sealed class SlackWorkspaceEnrollment
{
    public string Id { get; set; } = string.Empty;
    public string WorkspaceTeamId { get; set; } = string.Empty;
    public string Lifecycle { get; set; } = SlackEnrollmentLifecycle.Active;
    public string ManagerCapability { get; set; } = SlackManagerCapability.Unknown;
    public string? CapabilityReason { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string PlanCode { get; set; } = string.Empty;
    public int ManagedAppLimit { get; set; }
    public string ManagerCredentialRef { get; set; } = string.Empty;
    public string ManagerAppId { get; set; } = string.Empty;
    public string ManagerBotUserId { get; set; } = string.Empty;
    public string ManagerTransportKind { get; set; } = SlackManagerTransportKind.Socket;
    public string ManagerReadiness { get; set; } = SlackManagerReadiness.Unknown;
    public string ManagerActorId { get; set; } = string.Empty;
    public string? ClaimedSlackUserId { get; set; }
    public string? ManagerClaimHash { get; set; }
    public DateTimeOffset? ManagerClaimIssuedAt { get; set; }
    public DateTimeOffset? ManagerClaimExpiresAt { get; set; }
    public DateTimeOffset? ManagerClaimConsumedAt { get; set; }
    public string AuditJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public void TransitionLifecycle(string nextLifecycle, DateTimeOffset? removedAt = null)
    {
        SlackStateTransitions.RequireEnrollmentLifecycleTransition(Lifecycle, nextLifecycle);
        Lifecycle = nextLifecycle;
        if (nextLifecycle == SlackEnrollmentLifecycle.Removed)
            DeletedAt ??= removedAt ?? throw new ArgumentNullException(nameof(removedAt));
    }

    public void SetManagerCapability(string managerCapability, string? capabilityReason, DateTimeOffset? lastVerifiedAt)
    {
        SlackStateTransitions.RequireKnownManagerCapability(managerCapability);
        ManagerCapability = managerCapability;
        CapabilityReason = capabilityReason;
        LastVerifiedAt = lastVerifiedAt;
    }

    public void UpdatePlan(string planCode, int managedAppLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        if (managedAppLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(managedAppLimit));
        PlanCode = planCode;
        ManagedAppLimit = managedAppLimit;
    }

    public void ConfigureManagerApp(
        string appId,
        string botUserId,
        string credentialRef,
        string transportKind,
        string readiness,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialRef);
        if (System.Text.RegularExpressions.Regex.IsMatch(
                credentialRef,
                "^(?:xapp|xox[a-z])-",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Manager credentials must be stored by reference, not as a token.", nameof(credentialRef));
        SlackStateTransitions.RequireManagerTransportKind(transportKind);
        SlackStateTransitions.RequireManagerReadiness(readiness);

        if (!string.IsNullOrWhiteSpace(ManagerAppId)
            || !string.IsNullOrWhiteSpace(ManagerBotUserId))
        {
            if (!string.Equals(ManagerAppId, appId, StringComparison.Ordinal)
                || !string.Equals(ManagerBotUserId, botUserId, StringComparison.Ordinal))
                throw new InvalidOperationException("The Manager App identity cannot be changed after setup.");
        }

        ManagerAppId = appId.Trim();
        ManagerBotUserId = botUserId.Trim();
        ManagerCredentialRef = credentialRef.Trim();
        ManagerTransportKind = transportKind;
        ManagerReadiness = readiness;
        UpdatedAt = now;
    }

    public void EnsureManagerActor(string actorId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (string.IsNullOrWhiteSpace(ManagerActorId))
            ManagerActorId = actorId.Trim();
        UpdatedAt = now;
    }

    public void BindManagerActor(string slackUserId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slackUserId);
        if (string.IsNullOrWhiteSpace(ManagerActorId))
            throw new InvalidOperationException("The Manager actor is not initialized.");
        ClaimedSlackUserId = slackUserId.Trim();
        ManagerClaimHash = null;
        ManagerClaimConsumedAt = now;
        AppendAudit("manager_claimed", ClaimedSlackUserId, now);
        UpdatedAt = now;
    }

    public void AppendAudit(string action, string? slackUserId, DateTimeOffset at)
    {
        var facts = System.Text.Json.JsonSerializer.Deserialize<List<SlackAuditFact>>(AuditJson)
            ?? [];
        facts.Add(new SlackAuditFact(action, slackUserId, at));
        AuditJson = System.Text.Json.JsonSerializer.Serialize(facts);
    }

    private sealed record SlackAuditFact(string Action, string? SlackUserId, DateTimeOffset At);
}

public static class SlackEnrollmentLifecycle
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Removed = "removed";
}

public static class SlackManagerCapability
{
    public const string Unknown = "unknown";
    public const string Available = "available";
    public const string Unauthorized = "unauthorized";
    public const string CapacityLimited = "capacity_limited";
}

public static class SlackManagerTransportKind
{
    public const string Socket = "socket";
    public const string Https = "https";
}

public static class SlackManagerReadiness
{
    public const string Unknown = "unknown";
    public const string Ready = "ready";
    public const string NotReady = "not_ready";
    public const string Degraded = "degraded";
}

public static class SlackManagerClaimOutcome
{
    public const string Accepted = "accepted";
    public const string NoClaim = "no_claim";
    public const string Invalid = "invalid";
    public const string Expired = "expired";
    public const string Consumed = "consumed";
    public const string Rejected = "rejected";
}

public sealed record SlackManagerClaimConsumption(
    string Outcome,
    string? EnrollmentId = null,
    string? WorkspaceTeamId = null,
    string? ManagerActorId = null,
    string? SlackUserId = null);
