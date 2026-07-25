namespace Mohist.Server.Infrastructure.Events;

public enum EventProducerFamily
{
    WorkflowRun,
    Issue,
    Epic,
    AgentSession,
    AgentJob,
    Runner,
    InboxItemPersisted,
}

public readonly record struct ProducerLineageContext(
    string? ProjectId = null,
    string? Issue = null,
    string? Epic = null,
    string? WorkflowRunId = null,
    string? AgentId = null,
    string? SessionId = null,
    string? RunnerId = null,
    string? Stage = null,
    bool StageRequired = false,
    bool WorkflowOrigin = false);

[GenerateSerializer]
public sealed class ProducerConformanceException : Exception
{
    public ProducerConformanceException(EventProducerFamily family, string message)
        : base($"{family} producer conformance failed: {message}")
    {
        Family = family;
    }

    [Id(0)] public EventProducerFamily Family { get; }
}

public static class ProducerConformance
{
    private static readonly string[] ForbiddenLegacyKeys = ["issueid", "epicid", "issueno", "epicno"];

    public static void Assert(
        EventProducerFamily family,
        IReadOnlyDictionary<string, string> extensions,
        ProducerLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        foreach (var key in ForbiddenLegacyKeys)
        {
            if (extensions.ContainsKey(key))
                Fail(family, $"legacy extension '{key}' is present");
        }

        EnsureNoEmptyCanonicalValues(family, extensions);

        switch (family)
        {
            case EventProducerFamily.WorkflowRun:
                Require(family, extensions, EventCatalog.Lineage.ProjectId, context.ProjectId);
                Require(family, extensions, EventCatalog.Lineage.WorkflowRunId, context.WorkflowRunId);
                Optional(family, extensions, EventCatalog.Lineage.Issue, context.Issue);
                Optional(family, extensions, EventCatalog.Lineage.Epic, context.Epic);
                Stage(family, extensions, context);
                break;
            case EventProducerFamily.Issue:
                Require(family, extensions, EventCatalog.Lineage.ProjectId, context.ProjectId);
                Require(family, extensions, EventCatalog.Lineage.Issue, context.Issue);
                Optional(family, extensions, EventCatalog.Lineage.Epic, context.Epic);
                break;
            case EventProducerFamily.Epic:
                Require(family, extensions, EventCatalog.Lineage.ProjectId, context.ProjectId);
                Require(family, extensions, EventCatalog.Lineage.Epic, context.Epic);
                break;
            case EventProducerFamily.AgentSession:
                Require(family, extensions, EventCatalog.Lineage.ProjectId, context.ProjectId);
                Require(family, extensions, EventCatalog.Lineage.SessionId, context.SessionId);
                if (context.WorkflowOrigin)
                {
                    Optional(family, extensions, EventCatalog.Lineage.Issue, context.Issue);
                    Optional(family, extensions, EventCatalog.Lineage.Epic, context.Epic);
                    Optional(family, extensions, EventCatalog.Lineage.WorkflowRunId, context.WorkflowRunId);
                    Optional(family, extensions, EventCatalog.Lineage.Stage, context.Stage);
                    Absent(family, extensions, EventCatalog.Lineage.AgentId);
                }
                else
                {
                    Optional(family, extensions, EventCatalog.Lineage.AgentId, context.AgentId);
                    Optional(family, extensions, EventCatalog.Lineage.Issue, context.Issue);
                    Optional(family, extensions, EventCatalog.Lineage.Epic, context.Epic);
                    Absent(family, extensions, EventCatalog.Lineage.WorkflowRunId);
                    Absent(family, extensions, EventCatalog.Lineage.Stage);
                }
                break;
            case EventProducerFamily.AgentJob:
                // AgentJob failure events always carry the agent that
                // failed when one is known; raw-prompt-only validation
                // jobs (no resolved Agent profile) still emit so the
                // owner can observe the failure but stamp no agentid.
                Optional(family, extensions, EventCatalog.Lineage.AgentId, context.AgentId);
                Optional(family, extensions, EventCatalog.Lineage.ProjectId, context.ProjectId);
                Optional(family, extensions, EventCatalog.Lineage.Issue, context.Issue);
                Optional(family, extensions, EventCatalog.Lineage.Epic, context.Epic);
                Optional(family, extensions, EventCatalog.Lineage.WorkflowRunId, context.WorkflowRunId);
                Absent(family, extensions, EventCatalog.Lineage.SessionId);
                Absent(family, extensions, EventCatalog.Lineage.Stage);
                Absent(family, extensions, EventCatalog.Lineage.RunnerId);
                break;
            case EventProducerFamily.Runner:
                Require(family, extensions, EventCatalog.Lineage.RunnerId, context.RunnerId);
                Optional(family, extensions, EventCatalog.Lineage.ProjectId, context.ProjectId);
                break;
            case EventProducerFamily.InboxItemPersisted:
                Require(family, extensions, EventCatalog.Lineage.ProjectId, context.ProjectId);
                Require(family, extensions, EventCatalog.Lineage.Issue, context.Issue);
                Optional(family, extensions, EventCatalog.Lineage.Epic, context.Epic);
                Optional(family, extensions, EventCatalog.Lineage.WorkflowRunId, context.WorkflowRunId);
                Optional(family, extensions, EventCatalog.Lineage.Stage, context.Stage);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family), family, null);
        }
    }

    private static void Stage(
        EventProducerFamily family,
        IReadOnlyDictionary<string, string> extensions,
        ProducerLineageContext context)
    {
        if (context.StageRequired)
        {
            Require(family, extensions, EventCatalog.Lineage.Stage, context.Stage);
            return;
        }

        Absent(family, extensions, EventCatalog.Lineage.Stage);
    }

    private static void Require(
        EventProducerFamily family,
        IReadOnlyDictionary<string, string> extensions,
        string key,
        string? expected)
    {
        if (!extensions.TryGetValue(key, out var actual) || string.IsNullOrWhiteSpace(actual))
            Fail(family, $"required extension '{key}' is missing or empty");
        if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
            Fail(family, $"extension '{key}' is '{actual}', expected '{expected}'");
    }

    private static void Optional(
        EventProducerFamily family,
        IReadOnlyDictionary<string, string> extensions,
        string key,
        string? expected)
    {
        if (expected is null)
        {
            Absent(family, extensions, key);
            return;
        }

        Require(family, extensions, key, expected);
    }

    private static void Absent(
        EventProducerFamily family,
        IReadOnlyDictionary<string, string> extensions,
        string key)
    {
        if (extensions.ContainsKey(key))
            Fail(family, $"extension '{key}' is present without local context");
    }

    private static void EnsureNoEmptyCanonicalValues(
        EventProducerFamily family,
        IReadOnlyDictionary<string, string> extensions)
    {
        foreach (var key in extensions.Keys)
        {
            if (key is EventCatalog.Lineage.ProjectId
                or EventCatalog.Lineage.Issue
                or EventCatalog.Lineage.Epic
                or EventCatalog.Lineage.WorkflowRunId
                or EventCatalog.Lineage.Stage
                or EventCatalog.Lineage.AgentId
                or EventCatalog.Lineage.SessionId
                or EventCatalog.Lineage.RunnerId)
            {
                if (string.IsNullOrWhiteSpace(extensions[key]))
                    Fail(family, $"canonical extension '{key}' is empty");
            }
        }
    }

    private static void Fail(EventProducerFamily family, string message) =>
        throw new ProducerConformanceException(family, message);
}
