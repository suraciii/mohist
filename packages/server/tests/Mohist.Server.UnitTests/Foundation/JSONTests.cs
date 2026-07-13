using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public class JSONTests
{
    [Fact]
    public void Options_EncoderPreservesNonAsciiCharacters()
    {
        Assert.NotNull(JSON.Options.Encoder);
        Assert.Equal(
            JavaScriptEncoder.Create(UnicodeRanges.All).ToString(),
            JSON.Options.Encoder!.ToString());
    }

    [Fact]
    public void Serialize_NonAsciiString_EmitsVerbatimCharacters()
    {
        var json = JSON.Serialize(new { text = "中文" });

        Assert.Contains("\"中文\"", json);
        Assert.DoesNotContain("\\u4e2d", json);
        Assert.DoesNotContain("\\u6587", json);
    }

    [Fact]
    public void Serialize_HtmlSignificantCharacters_RemainEscaped()
    {
        var json = JSON.Serialize(new { html = "<script>alert(\"x\")</script> & 'ok'" });

        Assert.Contains("\\u003C", json);
        Assert.Contains("\\u003E", json);
        Assert.Contains("\\u0026", json);
        Assert.DoesNotContain("<script>", json);
    }

    [Fact]
    public void Indented_ReusesOptionsAndEnablesWriteIndented()
    {
        var json = JsonSerializer.Serialize(new { greeting = "中文" }, JSON.Indented);

        Assert.Contains("\"greeting\": \"中文\"", json);
        Assert.Contains("\n", json);
        Assert.True(JSON.Indented.WriteIndented);
        Assert.Equal(JSON.Options.PropertyNameCaseInsensitive, JSON.Indented.PropertyNameCaseInsensitive);
        Assert.Equal(JSON.Options.DefaultIgnoreCondition, JSON.Indented.DefaultIgnoreCondition);
    }

    [Fact]
    public void FailureReason_RoundTrip_ForKnownValues_IsValueIdentical()
    {
        foreach (var reason in Enum.GetValues<FailureReason>())
        {
            var payload = new ReasonEnvelope(reason);

            var serialized = JSON.Serialize(payload);
            var deserialized = JSON.Deserialize<ReasonEnvelope>(serialized);

            Assert.NotNull(deserialized);
            Assert.Equal(reason, deserialized!.Reason);
            Assert.Equal(reason.ToString(), ExtractFieldValue(serialized, "reason"));
        }
    }

    [Fact]
    public void FailureReason_RoundTrip_ForUnknownString_FallsBackToTaskFailed()
    {
        const string legacy = "{\"reason\":\"RemovedReason\",\"stage\":\"build\"}";

        var deserialized = JSON.Deserialize<ReasonEnvelope>(legacy);

        Assert.NotNull(deserialized);
        Assert.Equal(FailureReason.TaskFailed, deserialized!.Reason);
        Assert.Equal("build", deserialized.Stage);
    }

    [Fact]
    public void FailureReason_RoundTrip_ForUnknownNumber_FallsBackToTaskFailed()
    {
        const string legacy = "{\"reason\":999,\"stage\":\"build\"}";

        var deserialized = JSON.Deserialize<ReasonEnvelope>(legacy);

        Assert.NotNull(deserialized);
        Assert.Equal(FailureReason.TaskFailed, deserialized!.Reason);
    }

    [Fact]
    public void FailureDetails_RoundTrip_ForKnownValues_MatchesGoldenMaster()
    {
        var details = new FailureDetails(
            FailureReason.ApprovalRejected,
            Stage: "build",
            TaskId: "task-1",
            CheckName: null,
            Message: "needs rework");

        var serialized = JSON.Serialize(details);

        Assert.Equal(
            "{\"reason\":\"ApprovalRejected\",\"stage\":\"build\",\"taskId\":\"task-1\",\"message\":\"needs rework\"}",
            serialized);

        var deserialized = JSON.Deserialize<FailureDetails>(serialized);
        Assert.Equal(details, deserialized);
    }

    [Fact]
    public void Options_RegistersUnknownFailureReasonConverter()
    {
        Assert.Contains(JSON.Options.Converters, c => c is JSON.UnknownFailureReasonJsonConverter);
    }

    [Fact]
    public void Serialize_EnumValue_UsesStringRepresentation()
    {
        var json = JSON.Serialize(new { status = "active", reason = FailureReason.CheckFailed });

        Assert.Contains("\"reason\":\"CheckFailed\"", json);
        Assert.DoesNotContain("\"reason\":1", json);
    }

    [Fact]
    public void Deserialize_PropertyNamesAreCaseInsensitive()
    {
        const string json = "{\"REASON\":\"TaskFailed\",\"STAGE\":\"build\"}";

        var deserialized = JSON.Deserialize<ReasonEnvelope>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(FailureReason.TaskFailed, deserialized!.Reason);
        Assert.Equal("build", deserialized.Stage);
    }

    [Fact]
    public void Deserialize_UnicodeMessage_RoundTripsVerbatim()
    {
        var original = new FailureDetails(
            FailureReason.TaskFailed,
            Stage: "构建",
            TaskId: null,
            CheckName: null,
            Message: "失败原因：中文");

        var serialized = JSON.Serialize(original);
        var deserialized = JSON.Deserialize<FailureDetails>(serialized);

        Assert.Equal(original, deserialized);
        Assert.Contains("\"stage\":\"构建\"", serialized);
        Assert.Contains("\"message\":\"失败原因：中文\"", serialized);
    }

    [Fact]
    public void Deserialize_NullProperties_AreIgnoredOnSerialize()
    {
        var original = new ReasonEnvelope(FailureReason.ApprovalRejected, Stage: null);

        var json = JSON.Serialize(original);

        Assert.DoesNotContain("Stage", json);
    }

    [Fact]
    public void Deserialize_PreChangeEscapedJson_ReadsBackAsVerbatimValues()
    {
        // Backward compat for the json-serialization spec scenario
        // "Previously persisted JSON reads back unchanged": JSON that was
        // persisted before this change — written with the default encoder as
        // \uXXXX escapes — must deserialize through JSON.* to the same values
        // as the verbatim form. The encoder governs output only; the decoder
        // accepts both representations.
        const string escapedJson =
            "{\"title\":\"\\u4fee\\u590d\\u4e2d\\u6587\\u4e71\\u7801\"," +
            "\"stage\":\"\\u6784\\u5efa\"," +
            "\"message\":\"\\u5931\\u8d25\\u539f\\u56e0\\uff1a\\u4e2d\\u6587\"," +
            "\"reason\":\"TaskFailed\"}";

        var deserialized = JSON.Deserialize<PersistedShape>(escapedJson);

        Assert.NotNull(deserialized);
        Assert.Equal("修复中文乱码", deserialized!.Title);
        Assert.Equal("构建", deserialized.Stage);
        Assert.Equal("失败原因：中文", deserialized.Message);
        Assert.Equal(FailureReason.TaskFailed, deserialized.Reason);
    }

    [Fact]
    public void Deserialize_PreChangeEscapedJson_EqualsVerbatimJson()
    {
        // Same scenario as above, asserting that the two representations
        // deserialize to value-equal objects (no migration step required).
        const string escaped =
            "{\"title\":\"\\u4fee\\u590d\\u4e2d\\u6587\\u4e71\\u7801\",\"stage\":\"build\"}";
        const string verbatim = "{\"title\":\"修复中文乱码\",\"stage\":\"build\"}";

        var fromEscaped = JSON.Deserialize<PersistedShape>(escaped);
        var fromVerbatim = JSON.Deserialize<PersistedShape>(verbatim);

        Assert.NotNull(fromEscaped);
        Assert.NotNull(fromVerbatim);
        Assert.Equal(fromVerbatim!.Title, fromEscaped!.Title);
        Assert.Equal(fromVerbatim.Stage, fromEscaped.Stage);
    }

    [Fact]
    public void Deserialize_PreChangeSessionJson_RoundTripsToSameValue()
    {
        // AgentSessionStore persisted JSON in the pre-change era used default
        // encoding (escape non-ASCII). Verify a representative persisted
        // payload reads back identically.
        const string preChangePersisted =
            "{\"sessionId\":\"sess-1\",\"transcript\":\"\\u4f1a\\u8bdd\\u8fdb\\u884c\\u4e2d\"," +
            "\"status\":\"active\"}";

        var deserialized = JSON.Deserialize<PersistedSession>(preChangePersisted);

        Assert.NotNull(deserialized);
        Assert.Equal("sess-1", deserialized!.SessionId);
        Assert.Equal("会话进行中", deserialized.Transcript);
        Assert.Equal("active", deserialized.Status);
    }

    private sealed record ReasonEnvelope(FailureReason Reason, string? Stage = null);

    private sealed record PersistedShape(
        string Title,
        string Stage,
        string Message,
        FailureReason Reason);

    private sealed record PersistedSession(
        string SessionId,
        string Transcript,
        string Status);

    private static string ExtractFieldValue(string json, string field)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(field).GetString() ?? string.Empty;
    }
}
