using System.Text.Json;
using Mohist.Server.Agent.Services;

namespace Mohist.Server.Slack.Services;

public sealed record SlackManagerToolInvocation(
    string Tool,
    string? ProjectId = null,
    string? AgentName = null,
    string? ConnectionId = null,
    string? AccessPolicy = null,
    string? DailyResponsibility = null)
{
    private const string EnvelopeProperty = "mohistManagerTool";

    public ManagerResourceTarget? Target => Tool switch
    {
        SlackManagerAgentTools.View or SlackManagerAgentTools.Create =>
            new(ManagerResourceKinds.Project, ProjectId ?? string.Empty),
        SlackManagerAgentTools.Edit or SlackManagerAgentTools.Enable or SlackManagerAgentTools.Disable
            or SlackManagerAgentTools.ClaimOwner or SlackManagerAgentTools.TransferOwner =>
            new(ManagerResourceKinds.Connection, ProjectId ?? string.Empty, ConnectionId),
        _ => null,
    };

    public static SlackManagerToolIntent Parse(string? assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
            return SlackManagerToolIntent.NotRequested;

        try
        {
            using var document = JsonDocument.Parse(assistantText);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(EnvelopeProperty, out var toolElement))
                return SlackManagerToolIntent.NotRequested;

            if (toolElement.ValueKind != JsonValueKind.Object
                || !toolElement.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString())
                || !toolElement.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(document.RootElement, EnvelopeProperty)
                || !HasOnlyProperties(toolElement, "name", "arguments")
                || !HasOnlyProperties(arguments, "projectId", "agentName", "connectionId", "accessPolicy", "dailyResponsibility"))
            {
                return SlackManagerToolIntent.Invalid("manager_tool_request_invalid");
            }

            if (!TryString(arguments, "projectId", out var projectId)
                || !TryString(arguments, "agentName", out var agentName)
                || !TryString(arguments, "connectionId", out var connectionId)
                || !TryString(arguments, "accessPolicy", out var accessPolicy)
                || !TryString(arguments, "dailyResponsibility", out var dailyResponsibility))
            {
                return SlackManagerToolIntent.Invalid("manager_tool_arguments_invalid");
            }

            return SlackManagerToolIntent.Requested(new(
                nameElement.GetString()!.Trim(),
                projectId,
                agentName,
                connectionId,
                accessPolicy,
                dailyResponsibility));
        }
        catch (JsonException)
        {
            return SlackManagerToolIntent.NotRequested;
        }
    }

    private static bool HasOnlyProperties(JsonElement element, params string[] allowed) =>
        element.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal));

    private static bool TryString(JsonElement arguments, string name, out string? value)
    {
        value = null;
        if (!arguments.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()?.Trim();
        return true;
    }
}

public sealed record SlackManagerToolIntent(
    bool IsRequested,
    SlackManagerToolInvocation? Invocation = null,
    string? Error = null)
{
    public static SlackManagerToolIntent NotRequested { get; } = new(false);

    public static SlackManagerToolIntent Requested(SlackManagerToolInvocation invocation) => new(true, invocation);

    public static SlackManagerToolIntent Invalid(string error) => new(true, null, error);
}
