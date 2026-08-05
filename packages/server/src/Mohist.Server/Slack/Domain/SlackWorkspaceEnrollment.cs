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
    public string ConfigurationCredentialRef { get; set; } = string.Empty;
    public int ConfigurationCredentialGeneration { get; set; }
    public DateTimeOffset? ConfigurationCredentialExpiresAt { get; set; }
    public string ManagerCredentialRef { get; set; } = string.Empty;
    public string ManagerAppId { get; set; } = string.Empty;
    public string ManagerBotUserId { get; set; } = string.Empty;
    public string ManagerTransportKind { get; set; } = SlackManagerTransportKind.Socket;
    public string ManagerReadiness { get; set; } = SlackManagerReadiness.Unknown;
    public string ManagerAppLifecycle { get; set; } = SlackManagerAppLifecycle.NotCreated;
    public int ManagerAppOperationFence { get; set; }
    public string? ManagerAppOperationId { get; set; }
    public string? ManagerAppOperationOutcome { get; set; }
    public string ManagerAppManifestHash { get; set; } = string.Empty;
    public string ManagerAppInstallUrl { get; set; } = string.Empty;
    public string RuntimeCredentialValidationState { get; set; } = SlackRuntimeCredentialValidationState.NotProvided;
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

    public void RecordConfigurationCredentialRotation(
        string workspaceTeamId,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceTeamId);
        if (!string.Equals(WorkspaceTeamId, workspaceTeamId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Configuration credential workspace does not match the enrollment.");
        if (expiresAt <= now)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        ConfigurationCredentialRef = Id;
        ConfigurationCredentialGeneration++;
        ConfigurationCredentialExpiresAt = expiresAt;
        UpdatedAt = now;
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

    public void BeginManagerAppCreate(string operationId, int expectedFence, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (ManagerAppOperationFence != expectedFence)
            throw new InvalidOperationException("The Manager App create operation fence does not match the enrollment.");
        SlackStateTransitions.RequireManagerAppLifecycleTransition(ManagerAppLifecycle, SlackManagerAppLifecycle.Creating);
        ManagerAppLifecycle = SlackManagerAppLifecycle.Creating;
        ManagerAppOperationFence = expectedFence + 1;
        ManagerAppOperationId = operationId.Trim();
        ManagerAppOperationOutcome = null;
        UpdatedAt = now;
    }

    public void ApplyManagerAppCreateResult(string lifecycle, string redactedOutcome, int expectedFence, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redactedOutcome);
        if (lifecycle is not (SlackManagerAppLifecycle.Created
            or SlackManagerAppLifecycle.CreateUnknown
            or SlackManagerAppLifecycle.NotCreated))
            throw new ArgumentException("A Manager App create result must be 'created', 'create_unknown' or 'not_created'.", nameof(lifecycle));
        if (ManagerAppOperationFence != expectedFence)
            throw new InvalidOperationException("The Manager App create operation fence does not match the enrollment.");
        SlackStateTransitions.RequireManagerAppLifecycleTransition(ManagerAppLifecycle, lifecycle);
        ManagerAppLifecycle = lifecycle;
        ManagerAppOperationOutcome = redactedOutcome.Trim();
        UpdatedAt = now;
    }

    public void RecordManagerAppCreated(string appId, string manifestHash, string installUrl, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(installUrl);
        if (ManagerAppLifecycle != SlackManagerAppLifecycle.Created)
            throw new InvalidOperationException("The Manager App must be created before recording its identity.");
        if (!string.IsNullOrWhiteSpace(ManagerAppId)
            && !string.Equals(ManagerAppId, appId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Manager App identity cannot be changed after setup.");
        ManagerAppId = appId.Trim();
        ManagerAppManifestHash = manifestHash.Trim();
        ManagerAppInstallUrl = installUrl.Trim();
        UpdatedAt = now;
    }

    public void StageManagerRuntimeCredentials(string botUserId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);
        if (ManagerAppLifecycle != SlackManagerAppLifecycle.Created || string.IsNullOrWhiteSpace(ManagerAppId))
            throw new InvalidOperationException("The Manager App must be created before staging runtime credentials.");
        if (!string.IsNullOrWhiteSpace(ManagerBotUserId)
            && !string.Equals(ManagerBotUserId, botUserId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Manager Bot identity cannot be changed after setup.");
        SlackStateTransitions.RequireRuntimeCredentialValidationTransition(
            RuntimeCredentialValidationState, SlackRuntimeCredentialValidationState.Candidate);
        ManagerBotUserId = botUserId.Trim();
        RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Candidate;
        UpdatedAt = now;
    }

    public void CompleteSocketVerification(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ManagerAppId))
            throw new InvalidOperationException("The Manager App must be created before Socket verification.");
        SlackStateTransitions.RequireRuntimeCredentialValidationTransition(
            RuntimeCredentialValidationState, SlackRuntimeCredentialValidationState.Verified);
        RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Verified;
        ManagerReadiness = SlackManagerReadiness.Ready;
        UpdatedAt = now;
    }

    public void StageRuntimeCredentials(DateTimeOffset now)
    {
        SlackStateTransitions.RequireRuntimeCredentialValidationTransition(
            RuntimeCredentialValidationState,
            SlackRuntimeCredentialValidationState.Candidate);
        RuntimeCredentialValidationState = SlackRuntimeCredentialValidationState.Candidate;
        UpdatedAt = now;
    }

    public void ApplySocketValidation(string validationState, DateTimeOffset now)
    {
        SlackStateTransitions.RequireRuntimeCredentialValidationTransition(RuntimeCredentialValidationState, validationState);
        RuntimeCredentialValidationState = validationState;
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
}

public static class SlackManagerReadiness
{
    public const string Unknown = "unknown";
    public const string Ready = "ready";
    public const string NotReady = "not_ready";
    public const string Degraded = "degraded";
}

public static class SlackManagerAppLifecycle
{
    public const string NotCreated = "not_created";
    public const string Creating = "creating";
    public const string Created = "created";
    public const string CreateUnknown = "create_unknown";
}

public static class SlackRuntimeCredentialValidationState
{
    public const string NotProvided = "not_provided";
    public const string Candidate = "candidate";
    public const string AwaitingSocket = "awaiting_socket";
    public const string Verified = "verified";
    public const string Failed = "failed";
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
