using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class ConnectionDiagnosticTests
{
    [Fact]
    public void Compute_applies_the_documented_precedence_table()
    {
        var cases = new[]
        {
            ("setup", ConnectionDiagnosticState.SetupIncomplete, "Advance the current setup step.",
                new AgentConnection { SetupProgress = SetupProgressKind.FixSlackSetup },
                new DiagnosticInputs()),
            ("credentials", ConnectionDiagnosticState.CredentialsInvalid, "Rotate credentials.",
                Configure(connection =>
                {
                    connection.ConnectionHealth = ConnectionHealthKind.Unhealthy;
                    connection.HealthReason = "Slack rejected the Bot token.";
                }),
                new DiagnosticInputs()),
            ("service", ConnectionDiagnosticState.ServiceOffline, "Start mohist-slack / check Slack connectivity.",
                HealthyConnection(),
                new DiagnosticInputs(AdapterOnline: false)),
            ("owner", ConnectionDiagnosticState.OwnerUnavailable, "Transfer ownership.",
                HealthyConnection(),
                new DiagnosticInputs(OwnerAvailability: OwnerAvailabilityKind.Unavailable)),
            ("agent", ConnectionDiagnosticState.AgentNeedsSetup, "Configure Agent runtime/model.",
                HealthyConnection(),
                new DiagnosticInputs(AgentReadiness: AgentReadinessKind.NeedsSetup)),
            ("disabled", ConnectionDiagnosticState.Disabled, "Enable the Connection.",
                Configure(connection => connection.DesiredState = DesiredStateKind.Disabled),
                new DiagnosticInputs()),
            ("identity", ConnectionDiagnosticState.IdentityDrift, "Review the name/avatar difference.",
                Configure(connection => connection.VerifiedBotName = "Renamed Bot"),
                new DiagnosticInputs()),
            ("healthy", ConnectionDiagnosticState.Healthy, "No action needed.",
                HealthyConnection(),
                new DiagnosticInputs()),
        };

        var results = cases.Select(test => (test.Item1, Result: ConnectionDiagnostic.Compute(test.Item4, test.Item5))).ToArray();

        Assert.Equal(cases.Select(test => test.Item2), results.Select(test => test.Result.PrimaryState));
        Assert.Equal(cases.Select(test => test.Item3), results.Select(test => test.Result.NextAction));
        Assert.Equal(results.Length, results.Select(test => test.Result.NextAction).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Compute_exposes_independent_facts_and_concrete_identity_drift()
    {
        var connection = Configure(connection =>
        {
            connection.BotName = "Configured Bot";
            connection.VerifiedBotName = "Slack Bot";
            connection.AvatarHash = "recorded-avatar";
            connection.VerifiedBotIconUrl = "https://slack/icon.png";
            connection.ConnectionHealth = ConnectionHealthKind.Degraded;
            connection.HealthReason = "backpressured";
            connection.DesiredState = DesiredStateKind.Disabled;
        });

        var result = ConnectionDiagnostic.Compute(
            connection,
            new DiagnosticInputs(
                OwnerAvailability: OwnerAvailabilityKind.Available,
                AgentReadiness: AgentReadinessKind.Ready,
                AgentName: "Agent Bot"));

        Assert.Equal(ConnectionDiagnosticState.Disabled, result.PrimaryState);
        Assert.Equal(SetupProgressKind.Complete, result.Facts.SetupProgress);
        Assert.Equal(DesiredStateKind.Disabled, result.Facts.DesiredState);
        Assert.Equal(ConnectionHealthKind.Degraded, result.Facts.ConnectionHealth);
        Assert.Equal("backpressured", result.Facts.HealthReason);
        Assert.Equal(CredentialStatusKind.Valid, result.Facts.CredentialStatus);
        Assert.Equal(OwnerAvailabilityKind.Available, result.Facts.OwnerAvailability);
        Assert.True(result.Facts.IdentityDrift);
        Assert.Contains("presentation_name", result.Facts.Identity.DriftKinds);
        Assert.Contains("agent_name", result.Facts.Identity.DriftKinds);
        Assert.Contains("avatar", result.Facts.Identity.DriftKinds);
        Assert.Contains("Slack Bot", result.Facts.Identity.VerifiedBotName);
        Assert.Contains("https://slack/icon.png", result.Facts.Identity.VerifiedBotIconUrl);
    }

    [Fact]
    public void Compute_does_not_report_drift_before_the_first_verification()
    {
        var result = ConnectionDiagnostic.Compute(
            HealthyConnection(),
            new DiagnosticInputs(AgentName: "Different Agent"));

        Assert.Equal(ConnectionDiagnosticState.Healthy, result.PrimaryState);
        Assert.Equal("not_yet_verified", result.Facts.Identity.VerificationStatus);
        Assert.Empty(result.Facts.Identity.DriftKinds);
    }

    private static AgentConnection HealthyConnection() => new()
    {
        SetupProgress = SetupProgressKind.Complete,
        DesiredState = DesiredStateKind.Enabled,
        ConnectionHealth = ConnectionHealthKind.Healthy,
        AgentReadiness = AgentReadinessKind.Ready,
        OwnerSlackUserId = "U_OWNER",
    };

    private static AgentConnection Configure(Action<AgentConnection> configure)
    {
        var connection = HealthyConnection();
        configure(connection);
        return connection;
    }
}
