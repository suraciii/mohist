using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Contracts;

public sealed record JsonRpcRequest<TParams>(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] TParams Params);

public sealed record JsonRpcNotification<TParams>(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] TParams Params);

public sealed record JsonRpcSuccessResponse<TResult>(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] TResult Result);

public sealed record JsonRpcErrorResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Id,
    [property: JsonPropertyName("error")] JsonRpcError Error);

public sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data = null);

public sealed record WorkspaceQueryParams(
    [property: JsonPropertyName("query")] RunnerWorkspaceQuery Query);

public sealed record WorkspaceCommitDiffParams(
    [property: JsonPropertyName("query")] RunnerWorkspaceQuery Query,
    [property: JsonPropertyName("hash")] string Hash);

public sealed record WorkspaceFileContentParams(
    [property: JsonPropertyName("query")] RunnerWorkspaceQuery Query,
    [property: JsonPropertyName("path")] string Path);

public sealed record RunnerWorkspaceQuery(
    string? WorkflowRunId,
    string? ProjectId,
    int? IssueNumber,
    string? RepositoryName,
    string? GitUrl,
    string? WorkspacePath,
    string? Branch,
    string? BaseBranch);

public sealed record RunnerSessionBinding(
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("runtimeSessionId")] string RuntimeSessionId,
    [property: JsonPropertyName("runnerId")] string RunnerId,
    [property: JsonPropertyName("workDir")] string? WorkDir);

public sealed record RunnerSessionTarget(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("binding")] RunnerSessionBinding Binding,
    [property: JsonPropertyName("workflowRunId")] string? WorkflowRunId = null,
    [property: JsonPropertyName("sessionName")] string? SessionName = null,
    [property: JsonPropertyName("sessionId")] string? SessionId = null,
    [property: JsonPropertyName("definition")] AgentExecutionDefinition? Definition = null);

public sealed record FollowupAttachmentDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("size")] long Size);

public sealed record FollowupParams(
    [property: JsonPropertyName("target")] RunnerSessionTarget Target,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("inputId")] string? InputId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("executionSource")] string? ExecutionSource,
    [property: JsonPropertyName("slackExecutionContext")] AgentSlackExecutionContext? SlackExecutionContext,
    [property: JsonPropertyName("attachments")] IReadOnlyList<FollowupAttachmentDescriptor>? Attachments);

public sealed record SessionStopParams(
    [property: JsonPropertyName("target")] RunnerSessionTarget Target,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("operationId")] string OperationId);

public sealed record RunnerWorkspaceDiffResult(
    string Base,
    string Head,
    string MergeBase,
    int Ahead,
    int Behind,
    int CommitCount,
    int TotalAdditions,
    int TotalDeletions,
    IReadOnlyList<DiffFile> Files);

public sealed record RunnerWorkspaceCommitsResult(
    string Base,
    string Head,
    string MergeBase,
    int Ahead,
    int Behind,
    int FilesChanged,
    int TotalAdditions,
    int TotalDeletions,
    IReadOnlyList<GitCommit> Commits);

public sealed record RunnerWorkspaceCommitDiffResult(string Diff);

public sealed record RunnerWorkspaceFileContentResult(string? Base, string? Head, string? Reason = null);

public sealed record DiffFile(string File, int Additions, int Deletions, string Diff, bool IsBinary);

public sealed record GitCommit(string Hash, string ShortHash, string Message, string Author, string Date, string[] Files);

public sealed record WorkspaceRemovalResult(bool Removed, string Status, string? Path, string? Reason, string Message);

public sealed record RunnerFollowupDeliveryResult(bool Accepted, string? Error = null);

public sealed record WorkflowRunStatusNotification(string WorkflowRunId, string Status);
