using System.Text;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api.DirectApi;
using Xunit;

namespace Mohist.Server.UnitTests.DirectApi;

public sealed class DirectApiWriteValidationTests
{
    [Fact]
    public void IdempotencyKeyValidation_UsesThePrintableAsciiBoundaries()
    {
        var headers = new HeaderDictionary
        {
            ["Idempotency-Key"] = "k",
        };
        Assert.Equal(IdempotencyKeyDisposition.Valid,
            DirectApiWriteValidation.ReadIdempotencyKey(headers).Disposition);

        headers["Idempotency-Key"] = new string('k', 128);
        Assert.Equal(IdempotencyKeyDisposition.Valid,
            DirectApiWriteValidation.ReadIdempotencyKey(headers).Disposition);

        headers["Idempotency-Key"] = string.Empty;
        Assert.Equal(IdempotencyKeyDisposition.Invalid,
            DirectApiWriteValidation.ReadIdempotencyKey(headers).Disposition);

        headers["Idempotency-Key"] = new string('k', 129);
        Assert.Equal(IdempotencyKeyDisposition.Invalid,
            DirectApiWriteValidation.ReadIdempotencyKey(headers).Disposition);

        foreach (var invalid in new[] { "\u001fkey", "key\u007f" })
        {
            headers["Idempotency-Key"] = invalid;
            Assert.Equal(IdempotencyKeyDisposition.Invalid,
                DirectApiWriteValidation.ReadIdempotencyKey(headers).Disposition);
        }

        headers.Remove("Idempotency-Key");
        Assert.Equal(IdempotencyKeyDisposition.Required,
            DirectApiWriteValidation.ReadIdempotencyKey(headers).Disposition);
    }

    [Fact]
    public async Task TextBodyReader_RejectsNonContractJsonBeforeAdmission()
    {
        var invalidBodies = new[]
        {
            "",
            "{",
            "[]",
            "{}",
            "{\"text\":null}",
            "{\"text\":\"\"}",
            "{\"text\":1}",
            "{\"text\":\"work\",\"attachments\":[]}",
            "{\"text\":\"one\",\"text\":\"two\"}",
            "{\"text\":\"work\",}",
        };

        foreach (var invalid in invalidBodies)
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
            var result = await DirectApiWriteValidation.ReadTextBodyAsync(stream);
            Assert.False(result.IsValid, invalid);
            Assert.Null(result.Text);
        }
    }

    [Fact]
    public async Task TextBodyReader_PreservesTheParsedTextExactly()
    {
        const string text = " Fix the bug \n ";
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes($"{{\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}"));

        var result = await DirectApiWriteValidation.ReadTextBodyAsync(stream);

        Assert.True(result.IsValid);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void FingerprintsAndScopes_UseOnlyCanonicalRouteInputs()
    {
        const string projectId = "project-a";
        const string agentId = "agent-a";
        const string sessionId = "session-a";
        const string turnId = "turn-a";
        const string key = "request-key";

        Assert.NotEqual(
            DirectApiWriteValidation.LaunchFingerprint(projectId, agentId, "Fix the bug"),
            DirectApiWriteValidation.LaunchFingerprint(projectId, agentId, "Fix the bug "));
        Assert.Equal(
            "project-a|agent-a|request-key",
            DirectApiWriteValidation.LaunchScopeKey(projectId, agentId, key));
        Assert.Equal(
            "session-a|request-key",
            DirectApiWriteValidation.FollowupScopeKey(sessionId, key));
        Assert.Equal(
            "turn-a|caller-a|request-key",
            DirectApiWriteValidation.StopScopeKey(turnId, "caller-a", key));
        Assert.NotEqual(
            DirectApiWriteValidation.StopFingerprint("turn-a"),
            DirectApiWriteValidation.StopFingerprint("turn-b"));
    }
}
