using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Contracts;

public sealed class RunnerControlContractTests
{
    private static readonly IReadOnlyDictionary<int, string> StandardErrors = new Dictionary<int, string>
    {
        [-32700] = "Parse error",
        [-32600] = "Invalid Request",
        [-32601] = "Method not found",
        [-32602] = "Invalid params",
        [-32603] = "Internal error",
        [-32001] = "Response too large",
    };

    private static readonly string[] RequestMethods =
    [
        "workspace.diff",
        "workspace.commits",
        "workspace.commit-diff",
        "workspace.status",
        "workspace.file-content",
        "workspace.remove",
        "session.followup",
        "session.stop",
        "session.command",
    ];

    [Fact]
    public void Shared_catalog_decodes_every_typed_request_success_and_standard_error()
    {
        var catalog = ReadCatalog();
        var requests = catalog.RootElement.GetProperty("requests").EnumerateArray().ToArray();

        Assert.Equal(RequestMethods, requests.Select(entry => entry.GetProperty("method").GetString()));
        Assert.Equal(
            ["workspace.diff", "workspace.commits", "workspace.commit-diff"],
            requests.Where(entry => entry.TryGetProperty("nullableSuccess", out _))
                .Select(entry => entry.GetProperty("method").GetString()));
        foreach (var entry in requests)
        {
            AssertCamelCaseObjectNames(entry);
            DecodeMethod(entry.GetProperty("method").GetString()!, entry);
        }

        Assert.Equal(
            StandardErrors.Keys.Order(),
            requests.Select(entry => entry.GetProperty("error").GetProperty("error").GetProperty("code").GetInt32()).Distinct().Order());
    }

    [Fact]
    public void Shared_catalog_decodes_the_workflow_status_notification()
    {
        var catalog = ReadCatalog();
        var entries = catalog.RootElement.GetProperty("notifications").EnumerateArray().ToArray();
        var entry = Assert.Single(entries);
        Assert.Equal("workflow.status-changed", entry.GetProperty("method").GetString());

        var notification = Deserialize<JsonRpcNotification<WorkflowRunStatusNotification>>(
            entry.GetProperty("notification"));
        AssertCanonical(entry.GetProperty("notification"), notification);
        Assert.Equal("2.0", notification.JsonRpc);
        Assert.Equal("workflow.status-changed", notification.Method);
        Assert.Equal("run_101", notification.Params.WorkflowRunId);
        Assert.Equal("Completed", notification.Params.Status);
        Assert.False(entry.GetProperty("notification").TryGetProperty("id", out _));
        AssertCamelCaseObjectNames(entry);
    }

    private static void DecodeMethod(string method, JsonElement entry)
    {
        switch (method)
        {
            case "workspace.diff":
                Decode<WorkspaceQueryParams, RunnerWorkspaceDiffResult>(entry, request =>
                {
                    AssertWorkspaceQuery(request.Params.Query);
                    Assert.Single(request.Success.Result.Files);
                });
                break;
            case "workspace.commits":
                Decode<WorkspaceQueryParams, RunnerWorkspaceCommitsResult>(entry, request =>
                {
                    AssertWorkspaceQuery(request.Params.Query);
                    Assert.Single(request.Success.Result.Commits);
                });
                break;
            case "workspace.commit-diff":
                Decode<WorkspaceCommitDiffParams, RunnerWorkspaceCommitDiffResult>(entry, request =>
                {
                    AssertWorkspaceQuery(request.Params.Query);
                    Assert.Equal("def4567890", request.Params.Hash);
                    Assert.Contains("+new", request.Success.Result.Diff, StringComparison.Ordinal);
                });
                break;
            case "workspace.status":
                Decode<WorkspaceQueryParams, WorkspaceStatus>(entry, request =>
                {
                    AssertWorkspaceQuery(request.Params.Query);
                    Assert.True(request.Success.Result.Exists);
                    Assert.Null(request.Success.Result.Reason);
                });
                break;
            case "workspace.file-content":
                Decode<WorkspaceFileContentParams, RunnerWorkspaceFileContentResult>(entry, request =>
                {
                    AssertWorkspaceQuery(request.Params.Query);
                    Assert.Equal("src/control.ts", request.Params.Path);
                    Assert.Equal("new\n", request.Success.Result.Head);
                });
                break;
            case "workspace.remove":
                Decode<WorkspaceQueryParams, WorkspaceRemovalResult>(entry, request =>
                {
                    AssertWorkspaceQuery(request.Params.Query);
                    Assert.True(request.Success.Result.Removed);
                    Assert.Null(request.Success.Result.Reason);
                });
                break;
            case "session.followup":
                Decode<FollowupParams, RunnerFollowupDeliveryResult>(entry, request =>
                {
                    Assert.Equal("generic", request.Params.Target.Kind);
                    Assert.Equal("session_1", request.Params.Target.SessionId);
                    Assert.Null(request.Params.Target.WorkflowRunId);
                    Assert.Null(request.Params.Target.SessionName);
                    Assert.False(string.IsNullOrWhiteSpace(request.Params.OperationId));
                    Assert.False(string.IsNullOrWhiteSpace(request.Params.TurnId));
                    Assert.NotNull(request.Params.Target.Definition);
                    Assert.NotNull(request.Params.SlackExecutionContext);
                    Assert.Single(request.Params.Attachments!);
                    Assert.True(request.Success.Result.Accepted);
                });
                break;
            case "session.stop":
                Decode<SessionStopParams, RunnerStopReply>(entry, request =>
                {
                    Assert.Equal("workflow", request.Params.Target.Kind);
                    Assert.Equal("run_101", request.Params.Target.WorkflowRunId);
                    Assert.Equal("implementation", request.Params.Target.SessionName);
                    Assert.Null(request.Params.Target.Definition);
                    Assert.False(string.IsNullOrWhiteSpace(request.Params.SessionId));
                    Assert.False(string.IsNullOrWhiteSpace(request.Params.OperationId));
                    Assert.False(string.IsNullOrWhiteSpace(request.Params.TurnId));
                    Assert.Equal("stopped", request.Success.Result.State);
                });
                break;
            case "session.command":
                Decode<SessionCommandRequest, SessionCommandResult>(entry, request =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(request.Params.OperationId));
                    Assert.Equal(SessionCommandKind.Reset, request.Params.Command);
                    Assert.True(request.Success.Result.Ok);
                });
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Unknown fixture method {method}");
        }
    }

    private static void Decode<TParams, TResult>(
        JsonElement entry,
        Action<DecodedFixture<TParams, TResult>> assertContract)
        where TResult : class
    {
        var request = Deserialize<JsonRpcRequest<TParams>>(entry.GetProperty("request"));
        var success = Deserialize<JsonRpcSuccessResponse<TResult>>(entry.GetProperty("success"));
        var error = Deserialize<JsonRpcErrorResponse>(entry.GetProperty("error"));

        AssertCanonical(entry.GetProperty("request"), request);

        Assert.Equal("2.0", request.JsonRpc);
        Assert.Equal(entry.GetProperty("method").GetString(), request.Method);
        Assert.False(string.IsNullOrWhiteSpace(request.Id));
        Assert.Equal(request.Id, success.Id);
        Assert.True(error.Id is null || error.Id == request.Id);
        Assert.Equal("2.0", success.JsonRpc);
        Assert.Equal("2.0", error.JsonRpc);
        AssertStandardError(error.Error);
        AssertContract(assertContract, new(request.Params, success));

        if (entry.TryGetProperty("nullableSuccess", out var nullableSuccessElement))
        {
            var nullableSuccess = Deserialize<JsonRpcSuccessResponse<TResult?>>(nullableSuccessElement);
            Assert.Null(nullableSuccess.Result);
            Assert.Equal(request.Id, nullableSuccess.Id);
        }
    }

    private static void AssertContract<TParams, TResult>(
        Action<DecodedFixture<TParams, TResult>> assertion,
        DecodedFixture<TParams, TResult> fixture) => assertion(fixture);

    private static void AssertWorkspaceQuery(RunnerWorkspaceQuery query)
    {
        Assert.Equal("run_101", query.WorkflowRunId);
        Assert.Equal("project_1", query.ProjectId);
        Assert.Equal(657, query.IssueNumber);
        Assert.Equal("main", query.BaseBranch);
    }

    private static void AssertStandardError(JsonRpcError error)
    {
        Assert.True(StandardErrors.TryGetValue(error.Code, out var expectedMessage));
        Assert.Equal(expectedMessage, error.Message);
        Assert.Null(error.Data);
    }

    private static void AssertCanonical<T>(JsonElement fixture, T value) =>
        Assert.Equal(
            JsonSerializer.Serialize(fixture, JSON.Options),
            JsonSerializer.Serialize(value, JSON.Options));

    private static T Deserialize<T>(JsonElement value) =>
        JsonSerializer.Deserialize<T>(value, JSON.Options)
        ?? throw new Xunit.Sdk.XunitException($"Could not decode {typeof(T).Name}");

    private static JsonDocument ReadCatalog()
    {
        using var stream = typeof(RunnerControlContractTests).Assembly
            .GetManifestResourceStream("Mohist.RunnerControlFixtures.json")
            ?? throw new Xunit.Sdk.XunitException("Shared Runner control fixtures were not embedded");
        return JsonDocument.Parse(stream);
    }

    private static void AssertCamelCaseObjectNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                Assert.False(char.IsUpper(property.Name[0]), $"Property '{property.Name}' is not camelCase");
                AssertCamelCaseObjectNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                AssertCamelCaseObjectNames(item);
        }
    }

    private sealed record DecodedFixture<TParams, TResult>(
        TParams Params,
        JsonRpcSuccessResponse<TResult> Success);
}
