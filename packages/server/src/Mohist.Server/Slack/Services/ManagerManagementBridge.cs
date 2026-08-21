using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// The management capability is deliberately a small semantic boundary. It
/// validates the operation envelope, reauthenticates the current Slack actor,
/// checks the current target scope, and only then delegates to the application
/// service. It does not know about EF, grains, credentials, or HTTP handlers.
/// </summary>
public sealed class ManagerManagementBridge : IScopedService
{
    private readonly ManagerActorAccessDecider _access;
    private readonly SlackManagerApplicationService _manager;

    public ManagerManagementBridge(
        ManagerActorAccessDecider access,
        SlackManagerApplicationService manager)
    {
        _access = access;
        _manager = manager;
    }

    public async Task<ManagerCommandResult> ExecuteAsync(
        JsonElement rawRequest,
        ManagerExecutionCredentialContext credential,
        CancellationToken ct = default)
    {
        if (credential.Kind != ManagerExecutionLeaseKind.Management)
            return Denied("manager_management_credential_required", "This credential cannot invoke management operations.");

        var actor = await _access.AuthenticateAsync(
            credential.Lease.Origin.WorkspaceId,
            credential.Lease.Origin.ActorId,
            ct);
        if (!actor.Allowed || actor.Actor is null
            || !string.Equals(actor.Actor.EnrollmentId, credential.Lease.Origin.EnrollmentId, StringComparison.Ordinal))
            return Denied("manager_actor_not_authorized", "Manager authorization is no longer active; start a fresh turn.");

        var parsed = Parse(rawRequest);
        if (!parsed.IsValid)
            return Validation(parsed.ErrorCode!, parsed.ErrorMessage!);
        var argumentError = ValidateArguments(parsed.Operation!, parsed.Args!.Value);
        if (argumentError is not null)
            return Validation(argumentError.Value.Code, argumentError.Value.Message);

        if (!credential.Lease.Capabilities.Contains(CapabilityFor(parsed.Operation!), StringComparer.Ordinal))
            return Denied("manager_capability_not_available", "This Manager operation is outside the execution capability allowlist.");

        try
        {
            return parsed.Operation switch
            {
                ManagerManagementOperations.List => await ListAsync(actor.Actor, ct),
                ManagerManagementOperations.Diagnostics => await DiagnosticsAsync(actor.Actor, ct),
                ManagerManagementOperations.View => await ViewAsync(actor.Actor, parsed.Args!.Value, ct),
                ManagerManagementOperations.Create => await CreateAsync(actor.Actor, parsed.Args!.Value, ct),
                ManagerManagementOperations.Edit => await EditAsync(actor.Actor, parsed.Args!.Value, ct),
                ManagerManagementOperations.Enable => await StateAsync(actor.Actor, parsed.Args!.Value, DesiredStateKind.Enabled, ct),
                ManagerManagementOperations.Disable => await StateAsync(actor.Actor, parsed.Args!.Value, DesiredStateKind.Disabled, ct),
                ManagerManagementOperations.ClaimOwner => await OwnerAsync(actor.Actor, parsed.Args!.Value, SlackOwnerClaimCodeKinds.Initial, ct),
                ManagerManagementOperations.TransferOwner => await OwnerAsync(actor.Actor, parsed.Args!.Value, SlackOwnerClaimCodeKinds.Transfer, ct),
                _ => Denied("manager_operation_not_available", "This Manager operation is not available."),
            };
        }
        catch (SlackManagerValidationException ex)
        {
            return Validation(ex.Code, ex.Message);
        }
        catch (SlackManagerConflictException ex)
        {
            return Conflict(ex.Code, ex.Message);
        }
        catch (SlackConnectionAccessValidationException ex)
        {
            return Validation(ex.Code, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Unavailable("manager_service_unavailable", ex.Message);
        }
    }

    private async Task<ManagerCommandResult> ListAsync(ManagerActorContext actor, CancellationToken ct)
    {
        var status = await _manager.GetStatusAsync(actor.WorkspaceTeamId, ct);
        return status is null
            ? NotFound("manager_workspace_not_found", "The enrolled Manager workspace was not found.")
            : Confirmed(status, status.NextAction, "workspace_status");
    }

    private async Task<ManagerCommandResult> DiagnosticsAsync(ManagerActorContext actor, CancellationToken ct)
    {
        var status = await _manager.GetStatusAsync(actor.WorkspaceTeamId, ct);
        return status is null
            ? NotFound("manager_workspace_not_found", "The enrolled Manager workspace was not found.")
            : Confirmed(status, status.NextAction, "workspace_diagnostics");
    }

    private async Task<ManagerCommandResult> ViewAsync(
        ManagerActorContext actor,
        JsonElement args,
        CancellationToken ct)
    {
        var projectId = RequiredString(args, "projectId");
        var targetKind = RequiredString(args, "targetKind");
        var targetId = RequiredString(args, "targetId");
        var target = targetKind switch
        {
            ManagerResourceKinds.Agent => new ManagerResourceTarget(ManagerResourceKinds.Agent, projectId, targetId),
            ManagerResourceKinds.Connection => new ManagerResourceTarget(ManagerResourceKinds.Connection, projectId, targetId),
            _ => throw new BridgeValidationException("target_kind_invalid", "targetKind must be agent or connection."),
        };
        var authorization = await _access.AuthorizeAsync(actor, target, ct);
        if (!authorization.Allowed)
            return NotFound(authorization.Reason ?? "manager_resource_not_found", "The requested Manager target was not found.");

        if (targetKind == ManagerResourceKinds.Agent)
        {
            var agent = await _manager.GetAgentAsync(projectId, targetId, ct);
            return agent is null
                ? NotFound("manager_resource_not_found", "The requested Agent was not found.")
                : Confirmed(agent, null, "agent_view");
        }

        var connection = await _manager.InspectConnectionAsync(projectId, targetId, ct);
        return connection is null
            ? NotFound("manager_resource_not_found", "The requested Connection was not found.")
            : Confirmed(connection, null, "connection_view");
    }

    private async Task<ManagerCommandResult> CreateAsync(
        ManagerActorContext actor,
        JsonElement args,
        CancellationToken ct)
    {
        var projectId = RequiredString(args, "projectId");
        var agentId = OptionalString(args, "agentId");
        var agentName = OptionalString(args, "agentName");
        var responsibility = OptionalString(args, "responsibility");
        var accessPolicy = OptionalString(args, "accessPolicy");
        if ((agentId is null) == (agentName is null))
            throw new BridgeValidationException("agent_reference_required", "Exactly one of agentId or agentName is required.");

        AgentInfo? existing = agentId is not null
            ? await _manager.GetAgentAsync(projectId, agentId, ct)
            : await _manager.GetAgentByNameAsync(projectId, agentName!, ct);
        var resource = existing is null
            ? new ManagerResourceTarget(ManagerResourceKinds.Project, projectId)
            : new ManagerResourceTarget(ManagerResourceKinds.Agent, projectId, existing.Id);
        var authorization = await _access.AuthorizeAsync(actor, resource, ct);
        if (!authorization.Allowed)
            return NotFound(authorization.Reason ?? "manager_resource_not_found", "The requested Project or Agent was not found.");

        if (agentId is not null && existing is null)
            return NotFound("agent_not_found", "The requested Agent was not found.");
        if (existing is not null && responsibility is not null)
            throw new BridgeValidationException(
                "responsibility_not_allowed",
                "responsibility is allowed only when agentName creates a new Agent.");
        if (existing is null && agentName is not null && responsibility is null)
            throw new BridgeValidationException(
                "responsibility_required",
                "responsibility is required when agentName does not resolve to an existing Agent.");

        var result = await _manager.CreateOrMountAsync(
            projectId,
            agentId,
            agentName,
            responsibility,
            actor.WorkspaceTeamId,
            actor.SlackUserId,
            accessPolicy,
            ct);
        return result.Created
            ? Confirmed(result, result.ManagedApp.NextAction, "create_confirmed")
            : Idempotent(result, result.ManagedApp.NextAction, "already_mounted");
    }

    private async Task<ManagerCommandResult> EditAsync(
        ManagerActorContext actor,
        JsonElement args,
        CancellationToken ct)
    {
        var projectId = RequiredString(args, "projectId");
        var connectionId = RequiredString(args, "connectionId");
        var accessPolicy = RequiredString(args, "accessPolicy");
        var authorization = await AuthorizeConnectionAsync(actor, projectId, connectionId, ct);
        if (!authorization.Allowed)
            return NotFound(authorization.Reason ?? "manager_resource_not_found", "The requested Connection was not found.");
        var result = await _manager.EditAccessPolicyAsync(projectId, connectionId, accessPolicy, null, ct);
        return result is null
            ? NotFound("manager_resource_not_found", "The requested Connection was not found.")
            : Confirmed(result, null, "access_policy_updated");
    }

    private async Task<ManagerCommandResult> StateAsync(
        ManagerActorContext actor,
        JsonElement args,
        string desiredState,
        CancellationToken ct)
    {
        var projectId = RequiredString(args, "projectId");
        var connectionId = RequiredString(args, "connectionId");
        var authorization = await AuthorizeConnectionAsync(actor, projectId, connectionId, ct);
        if (!authorization.Allowed)
            return NotFound(authorization.Reason ?? "manager_resource_not_found", "The requested Connection was not found.");
        var current = await _manager.InspectConnectionAsync(projectId, connectionId, ct);
        if (current is null)
            return NotFound("manager_resource_not_found", "The requested Connection was not found.");
        var updated = await _manager.SetDesiredStateAsync(projectId, connectionId, desiredState, ct);
        if (updated is null)
            return NotFound("manager_resource_not_found", "The requested Connection was not found.");
        return string.Equals(current.Connection.DesiredState, desiredState, StringComparison.Ordinal)
            ? Idempotent(updated, null, "already_in_requested_state")
            : Confirmed(updated, null, $"connection_{desiredState}");
    }

    private async Task<ManagerCommandResult> OwnerAsync(
        ManagerActorContext actor,
        JsonElement args,
        string kind,
        CancellationToken ct)
    {
        var projectId = RequiredString(args, "projectId");
        var connectionId = RequiredString(args, "connectionId");
        var authorization = await AuthorizeConnectionAsync(actor, projectId, connectionId, ct);
        if (!authorization.Allowed)
            return NotFound(authorization.Reason ?? "manager_resource_not_found", "The requested Connection was not found.");
        var workflow = await _manager.IssueOwnerWorkflowAsync(projectId, connectionId, kind, ct);
        return workflow is null
            ? NotFound("manager_resource_not_found", "The requested Connection was not found.")
            : Confirmed(workflow, workflow.NextAction, "owner_workflow_issued");
    }

    private async Task<ManagerAccessDecision> AuthorizeConnectionAsync(
        ManagerActorContext actor,
        string projectId,
        string connectionId,
        CancellationToken ct) =>
        await _access.AuthorizeAsync(
            actor,
            new ManagerResourceTarget(ManagerResourceKinds.Connection, projectId, connectionId),
            ct);

    private static string CapabilityFor(string operation) => operation switch
    {
        ManagerManagementOperations.List => ManagerCapabilityCatalog.WorkspaceStatus,
        ManagerManagementOperations.Diagnostics => ManagerCapabilityCatalog.ConnectionDiagnostics,
        ManagerManagementOperations.View => ManagerCapabilityCatalog.ConnectionView,
        ManagerManagementOperations.Create => ManagerCapabilityCatalog.AgentCreateOrMount,
        ManagerManagementOperations.Edit => ManagerCapabilityCatalog.ConnectionAccessPolicy,
        ManagerManagementOperations.Enable => ManagerCapabilityCatalog.ConnectionEnable,
        ManagerManagementOperations.Disable => ManagerCapabilityCatalog.ConnectionDisable,
        ManagerManagementOperations.ClaimOwner => ManagerCapabilityCatalog.OwnerClaim,
        ManagerManagementOperations.TransferOwner => ManagerCapabilityCatalog.OwnerTransfer,
        _ => string.Empty,
    };

    private static (string Code, string Message)? ValidateArguments(string operation, JsonElement args)
    {
        var allowed = operation switch
        {
            ManagerManagementOperations.List or ManagerManagementOperations.Diagnostics => Array.Empty<string>(),
            ManagerManagementOperations.View => new[] { "projectId", "targetKind", "targetId" },
            ManagerManagementOperations.Create => new[] { "projectId", "agentId", "agentName", "accessPolicy", "responsibility" },
            ManagerManagementOperations.Edit => new[] { "projectId", "connectionId", "accessPolicy" },
            ManagerManagementOperations.Enable or ManagerManagementOperations.Disable
                or ManagerManagementOperations.ClaimOwner or ManagerManagementOperations.TransferOwner =>
                new[] { "projectId", "connectionId" },
            _ => Array.Empty<string>(),
        };
        if (args.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            return ("manager_arguments_invalid", "The request contains an unsupported argument or authority override.");
        if (operation is ManagerManagementOperations.List or ManagerManagementOperations.Diagnostics)
            return args.EnumerateObject().Any() ? ("manager_arguments_invalid", "This operation does not accept arguments.") : null;
        return null;
    }

    private static ParsedManagementRequest Parse(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
            return ParsedManagementRequest.Invalid("manager_request_invalid", "The management request must be an object.");
        if (request.EnumerateObject().Count() != 2
            || !request.TryGetProperty("operation", out var operation)
            || !request.TryGetProperty("args", out var args)
            || operation.ValueKind != JsonValueKind.String
            || args.ValueKind != JsonValueKind.Object)
            return ParsedManagementRequest.Invalid("manager_request_invalid", "The management request must contain exactly operation and args.");
        var name = operation.GetString()?.Trim();
        if (!ManagerManagementOperations.All.Contains(name ?? string.Empty))
            return ParsedManagementRequest.Invalid("manager_operation_not_available", "The requested operation is not in the Manager allowlist.");
        return ParsedManagementRequest.Valid(name!, args);
    }

    private static string RequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new BridgeValidationException("manager_arguments_invalid", $"{name} is required.");
        var result = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(result)
            ? throw new BridgeValidationException("manager_arguments_invalid", $"{name} is required.")
            : result;
    }

    private static string? OptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new BridgeValidationException("manager_arguments_invalid", $"{name} must be a string.");
        var result = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static ManagerCommandResult Confirmed(object state, string? nextAction, string code) =>
        new(true, "confirmed_state", code, "The operation completed with confirmed state.", state, nextAction);

    private static ManagerCommandResult Idempotent(object state, string? nextAction, string code) =>
        new(true, "idempotent", code, "The requested state already existed.", state, nextAction);

    private static ManagerCommandResult Validation(string code, string message) =>
        new(false, "validation_error", code, message);

    private static ManagerCommandResult Conflict(string code, string message) =>
        new(false, "conflict", code, message);

    private static ManagerCommandResult NotFound(string code, string message) =>
        new(false, "not_found", code, message);

    private static ManagerCommandResult Unavailable(string code, string message) =>
        new(false, "unavailable", code, message);

    private static ManagerCommandResult Denied(string code, string message) =>
        new(false, "unavailable", code, message);

    private sealed record ParsedManagementRequest(
        bool IsValid,
        string? Operation,
        JsonElement? Args,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static ParsedManagementRequest Valid(string operation, JsonElement args) => new(true, operation, args, null, null);
        public static ParsedManagementRequest Invalid(string code, string message) => new(false, null, null, code, message);
    }

    private sealed class BridgeValidationException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}

public static class ManagerManagementOperations
{
    public const string List = "list";
    public const string View = "view";
    public const string Create = "create";
    public const string ClaimOwner = "claim-owner";
    public const string Edit = "edit";
    public const string Enable = "enable";
    public const string Disable = "disable";
    public const string TransferOwner = "transfer-owner";
    public const string Diagnostics = "diagnostics";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        List, View, Create, ClaimOwner, Edit, Enable, Disable, TransferOwner, Diagnostics,
    };
}

public sealed record ManagerCommandResult(
    bool Succeeded,
    string Outcome,
    string Code,
    string Message,
    object? State = null,
    string? NextAction = null);
