using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Sessions;

public class SessionCommandContractTests
{
    [Fact]
    public void CompactRequestAndResult_HaveSourceIndependentWireShape()
    {
        var request = new SessionCommandRequest(
            SessionId: "session-1",
            Runtime: "opencode",
            RuntimeSessionId: "runtime-1",
            RunnerId: "runner-1",
            WorkDir: "/work/project",
            Command: SessionCommandKind.Compact,
            OperationId: "compact-operation");

        var requestJson = JSON.SerializeToElement(request);
        Assert.Equal(
            ["command", "operationId", "runnerId", "runtime", "runtimeSessionId", "sessionId", "workDir"],
            PropertyNames(requestJson));
        Assert.Equal("compact", requestJson.GetProperty("command").GetString());
        Assert.False(requestJson.TryGetProperty("expectedRuntimeSessionId", out _));

        var resultJson = JSON.SerializeToElement(new SessionCommandResult(Ok: true));
        Assert.Equal(["ok"], PropertyNames(resultJson));
        Assert.True(resultJson.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ResetRequestAndResult_CarryExpectedAndReplacementRuntimeSessionIds()
    {
        var request = new SessionCommandRequest(
            SessionId: "session-1",
            Runtime: "opencode",
            RuntimeSessionId: "runtime-1",
            RunnerId: "runner-1",
            WorkDir: null,
            Command: SessionCommandKind.Reset,
            ExpectedRuntimeSessionId: "runtime-1",
            OperationId: "reset-operation");

        var requestJson = JSON.SerializeToElement(request);
        Assert.Equal(
            ["command", "expectedRuntimeSessionId", "operationId", "runnerId", "runtime", "runtimeSessionId", "sessionId", "workDir"],
            PropertyNames(requestJson));
        Assert.Equal("reset", requestJson.GetProperty("command").GetString());
        Assert.Equal("runtime-1", requestJson.GetProperty("expectedRuntimeSessionId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, requestJson.GetProperty("workDir").ValueKind);

        var resultJson = JSON.SerializeToElement(new SessionCommandResult(
            Ok: true,
            RuntimeSessionId: "runtime-2"));
        Assert.Equal(["ok", "runtimeSessionId"], PropertyNames(resultJson));
        Assert.Equal("runtime-2", resultJson.GetProperty("runtimeSessionId").GetString());
    }

    [Theory]
    [InlineData(SessionCommandError.Conflict, "conflict")]
    [InlineData(SessionCommandError.Missing, "missing")]
    [InlineData(SessionCommandError.Unavailable, "unavailable")]
    public void ErrorResult_UsesClosedErrorVocabulary(
        SessionCommandError error,
        string expectedWireValue)
    {
        var resultJson = JSON.SerializeToElement(new SessionCommandResult(Ok: false, Error: error));

        Assert.Equal(["error", "ok"], PropertyNames(resultJson));
        Assert.False(resultJson.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedWireValue, resultJson.GetProperty("error").GetString());
    }

    private static string[] PropertyNames(System.Text.Json.JsonElement value) =>
        value.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
