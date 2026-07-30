using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Agent.Services;

public static class ConnectionDiagnosticState
{
    public const string SetupIncomplete = "setup_incomplete";
    public const string CredentialsInvalid = "credentials_invalid";
    public const string ServiceOffline = "service_offline";
    public const string OwnerUnavailable = "owner_unavailable";
    public const string AgentNeedsSetup = "agent_needs_setup";
    public const string Disabled = "disabled";
    public const string IdentityDrift = "identity_drift";
    public const string Healthy = "healthy";
}

public static class OwnerAvailabilityKind
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
    public const string Unknown = "unknown";
    public const string NotConfigured = "not_configured";
}

public static class CredentialStatusKind
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Unknown = "unknown";
}

public sealed record DiagnosticInputs(
    bool AdapterOnline = true,
    string OwnerAvailability = OwnerAvailabilityKind.Unknown,
    string AgentReadiness = AgentReadinessKind.Unknown,
    string? AgentName = null);

public sealed record ConnectionDiagnosticResult(
    string PrimaryState,
    string Reason,
    string NextAction,
    ConnectionDiagnosticFacts Facts);

public sealed record ConnectionDiagnosticFacts(
    string SetupProgress,
    string DesiredState,
    string ConnectionHealth,
    string? HealthReason,
    string CredentialStatus,
    bool AdapterOnline,
    string OwnerAvailability,
    string AgentReadiness,
    ConnectionIdentityFacts Identity)
{
    public bool IdentityDrift => Identity.HasDrift;
}

public sealed record ConnectionIdentityFacts(
    string VerificationStatus,
    string? VerifiedBotName,
    string BotName,
    string? AgentName,
    string? VerifiedBotIconUrl,
    string? AvatarHash,
    IReadOnlyList<string> DriftKinds)
{
    public bool HasDrift => DriftKinds.Count > 0;
}

public static class ConnectionDiagnostic
{
    public static ConnectionDiagnosticResult Compute(AgentConnection connection, DiagnosticInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(inputs);

        var identity = BuildIdentityFacts(connection, inputs.AgentName);
        var facts = new ConnectionDiagnosticFacts(
            connection.SetupProgress,
            connection.DesiredState,
            connection.ConnectionHealth,
            connection.HealthReason,
            GetCredentialStatus(connection),
            inputs.AdapterOnline,
            inputs.OwnerAvailability,
            inputs.AgentReadiness,
            identity);

        if (!string.Equals(connection.SetupProgress, SetupProgressKind.Complete, StringComparison.Ordinal))
            return new(
                ConnectionDiagnosticState.SetupIncomplete,
                $"Slack setup is incomplete at '{connection.SetupProgress}'.",
                "Advance the current setup step.",
                facts);

        if (IsCredentialFailure(connection))
            return new(
                ConnectionDiagnosticState.CredentialsInvalid,
                connection.HealthReason ?? "Stored Slack credentials failed verification.",
                "Rotate credentials.",
                facts);

        if (!inputs.AdapterOnline || IsServiceFailure(connection))
            return new(
                ConnectionDiagnosticState.ServiceOffline,
                IsServiceFailure(connection)
                    ? connection.HealthReason ?? "Slack service could not be reached."
                    : "The Slack adapter heartbeat is stale.",
                "Start mohist-slack / check Slack connectivity.",
                facts);

        if (string.Equals(inputs.OwnerAvailability, OwnerAvailabilityKind.Unavailable, StringComparison.Ordinal))
            return new(
                ConnectionDiagnosticState.OwnerUnavailable,
                "The current Slack Owner is no longer an eligible workspace member.",
                "Transfer ownership.",
                facts);

        if (string.Equals(inputs.AgentReadiness, AgentReadinessKind.NeedsSetup, StringComparison.Ordinal))
            return new(
                ConnectionDiagnosticState.AgentNeedsSetup,
                "The bound Agent is missing required runtime configuration.",
                "Configure Agent runtime/model.",
                facts);

        if (string.Equals(connection.DesiredState, DesiredStateKind.Disabled, StringComparison.Ordinal))
            return new(
                ConnectionDiagnosticState.Disabled,
                "The Connection is disabled by operator choice.",
                "Enable the Connection.",
                facts);

        if (identity.HasDrift)
            return new(
                ConnectionDiagnosticState.IdentityDrift,
                DescribeIdentityDrift(identity),
                "Review the name/avatar difference.",
                facts);

        return new(
            ConnectionDiagnosticState.Healthy,
            "The Connection is ready and operating normally.",
            "No action needed.",
            facts);
    }

    private static bool IsCredentialFailure(AgentConnection connection) =>
        connection.ConnectionHealth == ConnectionHealthKind.Unhealthy
        && ContainsAny(connection.HealthReason, "token", "scope", "credential", "invalid_auth", "app and bot", "missing required");

    private static string GetCredentialStatus(AgentConnection connection) =>
        IsCredentialFailure(connection)
            ? CredentialStatusKind.Invalid
            : connection.SetupProgress == SetupProgressKind.Complete
                && connection.ConnectionHealth != ConnectionHealthKind.Unhealthy
                ? CredentialStatusKind.Valid
                : CredentialStatusKind.Unknown;

    private static bool IsServiceFailure(AgentConnection connection) =>
        connection.ConnectionHealth == ConnectionHealthKind.Unhealthy
        && ContainsAny(connection.HealthReason, "could not be reached", "unreachable", "service offline", "mohist-slack");

    private static bool ContainsAny(string? value, params string[] needles) =>
        value is not null && needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static ConnectionIdentityFacts BuildIdentityFacts(AgentConnection connection, string? agentName)
    {
        var driftKinds = new List<string>();
        var verified = connection.VerifiedBotName is not null || connection.VerifiedBotIconUrl is not null;
        if (verified)
        {
            if (connection.VerifiedBotName is not null
                && !string.Equals(connection.VerifiedBotName, connection.BotName, StringComparison.Ordinal))
                driftKinds.Add("presentation_name");
            if (agentName is not null
                && !string.Equals(connection.BotName, agentName, StringComparison.Ordinal))
                driftKinds.Add("agent_name");
            if (connection.VerifiedBotIconUrl is not null
                && !string.Equals(connection.VerifiedBotIconUrl, connection.AvatarHash, StringComparison.Ordinal))
                driftKinds.Add("avatar");
        }

        return new(
            connection.VerifiedBotName is null && connection.VerifiedBotIconUrl is null
                ? "not_yet_verified"
                : "verified",
            connection.VerifiedBotName,
            connection.BotName,
            agentName,
            connection.VerifiedBotIconUrl,
            connection.AvatarHash,
            driftKinds);
    }

    private static string DescribeIdentityDrift(ConnectionIdentityFacts identity)
    {
        var differences = new List<string>();
        if (identity.DriftKinds.Contains("presentation_name", StringComparer.Ordinal))
            differences.Add($"presentation name Slack='{identity.VerifiedBotName}' vs Connection='{identity.BotName}'");
        if (identity.DriftKinds.Contains("agent_name", StringComparer.Ordinal))
            differences.Add($"Agent name Connection='{identity.BotName}' vs Agent='{identity.AgentName}'");
        if (identity.DriftKinds.Contains("avatar", StringComparer.Ordinal))
            differences.Add($"avatar Slack='{identity.VerifiedBotIconUrl}' vs Connection='{identity.AvatarHash}'");
        return $"Identity drift detected: {string.Join("; ", differences)}.";
    }
}
