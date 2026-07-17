using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-417 D2: persisted coordinator fence. Holds at most one in-flight
/// repository command so a lost response, activation deactivation, or
/// timed-out downstream cannot race a later command into an inconsistent
/// committed state. Successful and rejected outcomes both clear the fence;
/// the only persistent state is "an outcome is uncertain".
/// </summary>
[GenerateSerializer]
public sealed record RepositoryCoordinatorState(
    [property: Id(0)] PendingRepositoryCommand? Pending)
{
    public static RepositoryCoordinatorState Empty { get; } = new(Pending: null);
}

[GenerateSerializer]
public sealed record PendingRepositoryCommand(
    [property: Id(0)] string CommandId,
    [property: Id(1)] string Kind,
    [property: Id(2)] string RepositoryName,
    [property: Id(3)] long ExpectedRevision,
    [property: Id(4)] string PayloadJson);

internal static class RepositoryCommandPayloadKinds
{
    public const string Create = "create";
    public const string Change = "change";
    public const string Reopen = "reopen";
    public const string Remove = "remove";
}

/// <summary>
/// Snapshot of the coordinator's caller-supplied parameters, sufficient
/// to replay a command after activation loss without re-deriving any
/// routing decision. Stored only as JSON inside
/// <see cref="PendingRepositoryCommand.PayloadJson"/>; it never becomes
/// committed business state.
/// </summary>
[GenerateSerializer]
public abstract record RepositoryCommandPayload
{
    public abstract string Kind { get; }

    /// <summary>Create Issue command payload.</summary>
    [GenerateSerializer]
    public sealed record Create(
        string ProjectId,
        int IssueNumber,
        string IssueId,
        string RepositoryName,
        string Title,
        string? Body,
        IReadOnlyDictionary<string, string>? Labels,
        string? Priority,
        string? Risk,
        bool IsDraft,
        string[]? AttachmentIds,
        string? WorkflowProfileId,
        int[]? PrerequisiteNumbers) : RepositoryCommandPayload
    {
        public override string Kind => RepositoryCommandPayloadKinds.Create;
    }

    /// <summary>Reassign target repository command payload.</summary>
    [GenerateSerializer]
    public sealed record Change(
        string ProjectId,
        string IssueId,
        int IssueNumber,
        string RepositoryName,
        string? Body,
        IReadOnlyDictionary<string, string>? Labels,
        string? Priority,
        bool? IsDraft,
        string[]? AttachmentIds,
        string? WorkflowProfileId,
        IReadOnlySet<string>? PresentFields,
        string? Title) : RepositoryCommandPayload
    {
        public override string Kind => RepositoryCommandPayloadKinds.Change;
    }

    /// <summary>Reopen a cancelled Issue command payload.</summary>
    [GenerateSerializer]
    public sealed record Reopen(
        string ProjectId,
        string IssueId,
        int IssueNumber,
        string RepositoryName) : RepositoryCommandPayload
    {
        public override string Kind => RepositoryCommandPayloadKinds.Reopen;
    }

    /// <summary>Remove a Project repository command payload.</summary>
    [GenerateSerializer]
    public sealed record Remove(
        string ProjectId,
        string RepositoryName) : RepositoryCommandPayload
    {
        public override string Kind => RepositoryCommandPayloadKinds.Remove;
    }
}

internal static class RepositoryCommandPayloadCodec
{
    public static string Serialize(RepositoryCommandPayload payload)
    {
        var kind = payload.Kind;
        var data = JsonSerializer.Serialize(payload, payload.GetType(), JSON.Options);
        // Wrap the typed payload as a {kind,data} envelope so the
        // deserializer can pick the right record type without polymorphic
        // type discriminators baked into System.Text.Json.
        using var doc = JsonDocument.Parse(data);
        var envelope = new
        {
            kind,
            data = doc.RootElement.Clone(),
        };
        return JsonSerializer.Serialize(envelope, JSON.Options);
    }

    public static RepositoryCommandPayload Deserialize(string kind, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataElement))
            throw new InvalidOperationException("RepositoryCommandPayload envelope missing 'data' property");

        return kind switch
        {
            RepositoryCommandPayloadKinds.Create => JsonSerializer.Deserialize<RepositoryCommandPayload.Create>(
                dataElement.GetRawText(), JSON.Options)!,
            RepositoryCommandPayloadKinds.Change => JsonSerializer.Deserialize<RepositoryCommandPayload.Change>(
                dataElement.GetRawText(), JSON.Options)!,
            RepositoryCommandPayloadKinds.Reopen => JsonSerializer.Deserialize<RepositoryCommandPayload.Reopen>(
                dataElement.GetRawText(), JSON.Options)!,
            RepositoryCommandPayloadKinds.Remove => JsonSerializer.Deserialize<RepositoryCommandPayload.Remove>(
                dataElement.GetRawText(), JSON.Options)!,
            _ => throw new InvalidOperationException($"Unknown repository command kind '{kind}'"),
        };
    }
}