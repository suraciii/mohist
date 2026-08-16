using System.Text.Json;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.UnitTests.DirectApi;

/// <summary>
/// The serialization contract of the public execution read shape: the
/// exact allowlisted key set with every key always present and
/// explicit nulls, a strict round trip that fails on any unknown key,
/// and the guarantee that no internal read shape or prompt content is
/// ever serialized into it.
/// </summary>
public sealed class PublicExecutionReadContractTests
{
    private static readonly string[] AllowlistedKeys =
    [
        "projectId", "agentId", "jobId", "sessionId", "inputId", "turnId",
        "status", "jobStatus", "sessionActivity", "admission", "inputStatus",
        "turnStatus", "outcome", "reasonCode", "output", "error",
        "acceptedAt", "queuedAt", "startedAt", "terminalAt", "observedAt",
        "sequence",
    ];

    private static PublicExecutionRead Sample(bool withContent = true) => new()
    {
        ProjectId = "proj_123",
        AgentId = "agent_123",
        JobId = "job_123",
        SessionId = "session_123",
        InputId = withContent ? "input_123" : null,
        TurnId = withContent ? "turn_123" : null,
        Status = PublicExecutionFieldValues.StatusQueued,
        JobStatus = PublicExecutionFieldValues.JobQueued,
        SessionActivity = PublicExecutionFieldValues.SessionActive,
        Admission = PublicExecutionFieldValues.AdmissionBlocked,
        InputStatus = withContent ? PublicExecutionFieldValues.InputAccepted : null,
        TurnStatus = withContent ? PublicExecutionFieldValues.TurnQueued : null,
        Outcome = null,
        ReasonCode = null,
        Output = null,
        Error = null,
        AcceptedAt = new DateTimeOffset(2026, 8, 9, 10, 15, 30, TimeSpan.Zero),
        QueuedAt = new DateTimeOffset(2026, 8, 9, 10, 15, 31, TimeSpan.Zero),
        StartedAt = null,
        TerminalAt = null,
        ObservedAt = new DateTimeOffset(2026, 8, 9, 10, 15, 31, TimeSpan.Zero),
        Sequence = withContent ? 18 : null,
    };

    [Fact]
    public void SerializedShape_ContainsExactlyTheAllowlistedKeys()
    {
        var json = SerializePublic(Sample());

        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();

        Assert.True(
            keys.SetEquals(AllowlistedKeys),
            $"Expected exactly the allowlisted keys, got: {string.Join(",, ", keys.OrderBy(key => key))} :: missing=[{string.Join(", ", AllowlistedKeys.Except(keys))}] extra=[{string.Join(", ", keys.Except(AllowlistedKeys))}]");
    }

    [Fact]
    public void SerializedShape_KeepsExplicitNulls_ForEveryAllowlistedNullableKey()
    {
        // Every allowlisted nullable fact is an explicit JSON null when
        // the canonical fact does not exist — never a missing key.
        var json = SerializePublic(Sample(withContent: false));

        using var document = JsonDocument.Parse(json);
        foreach (var nullableKey in AllowlistedKeys.Where(key => key != "projectId" && key != "status" && key != "observedAt"))
        {
            Assert.True(
                document.RootElement.TryGetProperty(nullableKey, out _),
                $"The key '{nullableKey}' must always be present, null when the canonical fact does not exist.");
        }

        var minimal = Sample(withContent: false);
        Assert.Null(minimal.InputId);
        Assert.Null(minimal.TurnId);
        Assert.Null(minimal.Sequence);
    }

    [Fact]
    public void StrictRoundTrip_FailsOnAnyUnknownKey()
    {
        var json = SerializePublic(Sample());
        AssertRoundTrips(json);

        var withUnknownKey = json.Insert(json.Length - 1, ",\"runtimeSessionId\":\"rt_1\"");
        Assert.Throws<JsonException>(() => StrictRead(withUnknownKey));

        var withInternalShape = """{"jobKey":"job_1","prompt":"Investigate","runnerId":"runner_1","state":{}}""";
        Assert.Throws<JsonException>(() => StrictRead(withInternalShape));
    }

    [Fact]
    public void PromptText_NeverAppearsInAnySerializedValue()
    {
        var prompt = "Investigate the failed deployment";

        var dto = Sample() with
        {
            Output = new PublicExecutionOutput { Text = "Final answer only." },
            Error = new PublicExecutionError { Code = "failed", Message = "The agent execution failed." },
        };

        var json = SerializePublic(dto);
        Assert.DoesNotContain(prompt, json, StringComparison.Ordinal);
        Assert.Contains("Final answer only.", json, StringComparison.Ordinal);

        // The internal stack-trace-style detail never appears either.
        Assert.DoesNotContain("at Mohist.Server", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamps_AreRfc3339Utc()
    {
        var json = SerializePublic(Sample());

        using var document = JsonDocument.Parse(json);
        foreach (var key in new[] { "acceptedAt", "queuedAt", "observedAt" })
        {
            var value = document.RootElement.GetProperty(key).GetString();
            Assert.NotNull(value);
            Assert.EndsWith("Z", value, StringComparison.Ordinal);
            Assert.True(DateTimeOffset.TryParse(value, out var parsed), $"{key} must parse as RFC 3339");
            Assert.Equal(TimeSpan.Zero, parsed.Offset);
        }
    }

    [Fact]
    public void FieldVocabulary_IsTheFixedPublicSet()
    {
        var dto = Sample();
        Assert.Equal(PublicExecutionFieldValues.StatusQueued, dto.Status);
        Assert.Equal(PublicExecutionFieldValues.JobQueued, dto.JobStatus);
        Assert.Equal(PublicExecutionFieldValues.SessionActive, dto.SessionActivity);
        Assert.Equal(PublicExecutionFieldValues.AdmissionBlocked, dto.Admission);
        Assert.Equal(PublicExecutionFieldValues.InputAccepted, dto.InputStatus);
        Assert.Equal(PublicExecutionFieldValues.TurnQueued, dto.TurnStatus);

        Assert.Equal(
            ["input.accepted", "input.rejected", "turn.queued", "turn.running", "turn.outcome_pending", "turn.terminal", "session.unknown", "session.context_reset"],
            PublicSessionEventTypes.All);
    }

    private static string SerializePublic(PublicExecutionRead dto) =>
        JsonSerializer.Serialize(dto, JSON.PublicApi);

    private static void AssertRoundTrips(string json)
    {
        var dto = StrictRead(json);
        Assert.Equal("proj_123", dto.ProjectId);
        Assert.Equal(PublicExecutionFieldValues.StatusQueued, dto.Status);
    }

    private static PublicExecutionRead StrictRead(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!AllowlistedKeys.Contains(property.Name))
            {
                throw new JsonException($"Unknown public execution key '{property.Name}'.");
            }
        }

        return JsonSerializer.Deserialize<PublicExecutionRead>(json)
            ?? throw new JsonException("The public execution payload was null.");
    }
}
