namespace Mohist.Server.Infrastructure.Security.Secrets;

public abstract record SecretOwnerAddress
{
    internal abstract string OwnerKind { get; }
    internal abstract string OwnerScope { get; }
    internal abstract string OwnerId { get; }

    public sealed record AgentConnection(string ProjectId, string ConnectionId) : SecretOwnerAddress
    {
        internal override string OwnerKind => SecretOwnerKinds.AgentConnection;
        internal override string OwnerScope => ProjectId;
        internal override string OwnerId => ConnectionId;
    }

    public sealed record WebhookSubscription(string ProjectId, string SubscriptionId) : SecretOwnerAddress
    {
        internal override string OwnerKind => SecretOwnerKinds.WebhookSubscription;
        internal override string OwnerScope => ProjectId;
        internal override string OwnerId => SubscriptionId;
    }

    public sealed record SlackWorkspaceEnrollment(string EnrollmentId) : SecretOwnerAddress
    {
        internal override string OwnerKind => SecretOwnerKinds.SlackWorkspaceEnrollment;
        internal override string OwnerScope => string.Empty;
        internal override string OwnerId => EnrollmentId;
    }

    public sealed record ManagedSlackAgentApp(string AgentAppId) : SecretOwnerAddress
    {
        internal override string OwnerKind => SecretOwnerKinds.ManagedSlackAgentApp;
        internal override string OwnerScope => string.Empty;
        internal override string OwnerId => AgentAppId;
    }
}

public static class SecretOwnerKinds
{
    public const string AgentConnection = "agent_connection";
    public const string WebhookSubscription = "webhook_subscription";
    public const string SlackWorkspaceEnrollment = "slack_workspace_enrollment";
    public const string ManagedSlackAgentApp = "managed_slack_agent_app";
}

public readonly record struct SecretStoreAddress
{
    public SecretStoreAddress(string projectId, string resourceId, SecretKind kind)
        : this(
            kind == SecretKind.WebhookSecret
                ? new SecretOwnerAddress.WebhookSubscription(projectId, resourceId)
                : new SecretOwnerAddress.AgentConnection(projectId, resourceId),
            kind)
    {
    }

    public SecretStoreAddress(SecretOwnerAddress owner, SecretKind kind)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Kind = kind;
        Validate(owner, kind);
    }

    public SecretOwnerAddress Owner { get; }
    public SecretKind Kind { get; }

    internal string OwnerKind => Owner.OwnerKind;
    internal string OwnerScope => Owner.OwnerScope;
    internal string OwnerId => Owner.OwnerId;

    public static SecretStoreAddress ForAgentConnection(string projectId, string connectionId, SecretKind kind) =>
        new(new SecretOwnerAddress.AgentConnection(projectId, connectionId), kind);

    public static SecretStoreAddress ForWebhookSubscription(string projectId, string subscriptionId) =>
        new(new SecretOwnerAddress.WebhookSubscription(projectId, subscriptionId), SecretKind.WebhookSecret);

    public static SecretStoreAddress ForSlackWorkspaceEnrollment(string enrollmentId, SecretKind kind) =>
        new(new SecretOwnerAddress.SlackWorkspaceEnrollment(enrollmentId), kind);

    public static SecretStoreAddress ForManagedSlackAgentApp(string agentAppId, SecretKind kind) =>
        new(new SecretOwnerAddress.ManagedSlackAgentApp(agentAppId), kind);

    private static void Validate(SecretOwnerAddress owner, SecretKind kind)
    {
        if (string.IsNullOrWhiteSpace(owner.OwnerId))
            throw new ArgumentException("Secret owner id is required.", nameof(owner));
        if (owner is SecretOwnerAddress.AgentConnection or SecretOwnerAddress.WebhookSubscription
            && string.IsNullOrWhiteSpace(owner.OwnerScope))
            throw new ArgumentException("Project-scoped secret owners require a project id.", nameof(owner));

        var valid = owner switch
        {
            SecretOwnerAddress.AgentConnection => kind is SecretKind.AppToken or SecretKind.BotToken,
            SecretOwnerAddress.WebhookSubscription => kind is SecretKind.WebhookSecret,
            SecretOwnerAddress.SlackWorkspaceEnrollment => kind is SecretKind.ConfigurationAccessToken
                or SecretKind.ConfigurationRefreshToken
                or SecretKind.AppToken
                or SecretKind.BotToken
                or SecretKind.ClientSecret
                or SecretKind.SigningSecret
                or SecretKind.PreviousBotToken
                or SecretKind.PreviousAppToken,
            SecretOwnerAddress.ManagedSlackAgentApp => kind is SecretKind.ClientSecret or SecretKind.SigningSecret or SecretKind.AppToken or SecretKind.BotToken or SecretKind.PreviousBotToken or SecretKind.PreviousAppToken,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException($"Secret kind '{kind}' is not valid for owner '{owner.GetType().Name}'.", nameof(kind));
    }
}
