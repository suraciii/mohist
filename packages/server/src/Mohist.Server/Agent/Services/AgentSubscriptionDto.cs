using System.Text.Json.Serialization;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// Product wire shape for an <see cref="Domain.AgentSubscription"/>. Used
/// by the subscription CRUD API (issue-391 T-002) and the future Web
/// detail page (T-004). The filter is serialized as structured
/// sub-fields (Type/Source/Subject) per design D2 — not as a single
/// opaque string — so the Web UI can render the constraints without
/// re-parsing.
/// </summary>
public sealed record AgentSubscriptionDto(
    string Id,
    string ProjectId,
    string AgentId,
    string Name,
    AgentSubscriptionFilterDto Filter,
    string ResponsePrompt,
    int? Priority,
    string Status,
    string CreatedAt,
    string UpdatedAt);

public sealed record AgentSubscriptionFilterDto(string Type, string? Source, string? Subject);
