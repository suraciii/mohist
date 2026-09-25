using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Idempotency;

namespace Mohist.Server.Api;

/// <summary>
/// Applies the durable request fence to control-plane writes. A keyed
/// request claims one fence row before it executes and replays the recorded
/// outcome instead of executing twice; a request without a key executes
/// exactly as before and records nothing.
/// </summary>
public static class KeyedControlWrites
{
    public static string IssueStartScopeKey(string projectId, int issueNumber, string callerKeyId, string publicKey) =>
        $"{projectId}|{issueNumber}|{callerKeyId}|{publicKey}";

    public static string IssueStartFingerprint(string projectId, int issueNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", DirectApiWriteValidation.FingerprintVersion);
            writer.WriteString("command", IdempotencyCommands.IssueStart);
            writer.WriteString("target", $"{projectId}|{issueNumber}");
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan)).ToLowerInvariant();
    }

    public static string WorkflowControlScopeKey(string workflowRunId, string callerKeyId, string publicKey) =>
        $"{workflowRunId}|{callerKeyId}|{publicKey}";

    /// <summary>
    /// Fingerprints the accepted payload only: version, command, target,
    /// verb, and the accepted body. Server-derived state never enters the
    /// fingerprint, so two requests that differ only in derived state stay
    /// the same request.
    /// </summary>
    public static string WorkflowControlFingerprint(string workflowRunId, string verb, object? acceptedBody = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);

        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", DirectApiWriteValidation.FingerprintVersion);
            writer.WriteString("command", IdempotencyCommands.WorkflowControl);
            writer.WriteString("target", workflowRunId);
            writer.WriteString("verb", verb);
            writer.WritePropertyName("body");
            if (acceptedBody is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                JsonSerializer.Serialize(writer, acceptedBody, acceptedBody.GetType(), JSON.Options);
            }
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>
    /// One operation outcome: the response envelope plus its HTTP status. A
    /// classified outcome (accepted, or a rejection with a stable code) is
    /// recorded on the fence row; an unclassified failure propagates.
    /// </summary>
    public sealed record Outcome(int StatusCode, object Envelope)
    {
        public bool IsClassified => StatusCode is >= 200 and < 500;

        public static Outcome Accepted(object envelope) => new(StatusCodes.Status200OK, envelope);

        public static Outcome Rejected(int statusCode, ApiResponse<object> envelope) => new(statusCode, envelope);
    }

    /// <summary>
    /// Quotes one shell word so a recovery command carrying caller values (a
    /// key, a message, a stage) stays executable as written.
    /// </summary>
    public static string ShellWord(string value)
    {
        var plain = value.Length > 0
            && value.All(character => char.IsAsciiLetterOrDigit(character) || "._:/@%+=,-".Contains(character));
        return plain ? value : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// One optional flag of a recovery command. The flag is omitted when the
    /// caller supplied no value, and the value is quoted so the printed command
    /// reproduces the accepted payload — and therefore the same fingerprint.
    /// </summary>
    public static string Flag(string name, string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $" --{name} {ShellWord(value)}";

    /// <summary>The recorded response of a completed fence row.</summary>
    private sealed record RecordedOutcome(int StatusCode, JsonElement Body);

    public static async Task<IResult> ExecuteAsync(
        IdempotencyKeyValidation key,
        ICurrentUser currentUser,
        IdempotencyFence fence,
        TimeProvider timeProvider,
        string command,
        string scopeKey,
        string fingerprint,
        string recoveryCommand,
        Func<Task<Outcome>> operation)
    {
        if (key.Disposition == IdempotencyKeyDisposition.Required)
            return Write(await operation());

        if (key.Disposition == IdempotencyKeyDisposition.Invalid)
            return Results.Json(
                ApiResults.Failure(
                    "The Idempotency-Key header must be 1 to 128 printable ASCII characters",
                    StatusCodes.Status400BadRequest,
                    "idempotency_key_invalid",
                    effect: ApiEffect.None,
                    retrySafe: false),
                statusCode: StatusCodes.Status400BadRequest);

        var claim = await fence.GetOrCreateAsync(
            command,
            scopeKey,
            currentUser.Principal.Id,
            fingerprint,
            turnId: null,
            initialOutcome: null);
        if (!claim.Created
            && !string.Equals(claim.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Results.Json(
                ApiResults.Failure(
                    "This Idempotency-Key was already used for a different request payload",
                    StatusCodes.Status409Conflict,
                    "idempotency_key_reused",
                    effect: ApiEffect.None,
                    retrySafe: false,
                    nextAction: recoveryCommand + " <new-key>"),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (claim.State is IdempotencyMappingStates.Completed or IdempotencyMappingStates.Rejected)
        {
            var recorded = IdempotencyFence.ReadOutcome<RecordedOutcome>(claim.Outcome);
            return Results.Json(recorded.Body, statusCode: recorded.StatusCode);
        }

        if (!claim.Created
            && claim.CreatedAt > timeProvider.GetUtcNow() - IdempotencyFence.PendingLease)
        {
            return new PendingResult(
                ApiResults.Failure(
                    "The same Idempotency-Key is still executing",
                    StatusCodes.Status503ServiceUnavailable,
                    "operation_pending",
                    effect: ApiEffect.Unknown,
                    retrySafe: true,
                    nextAction: recoveryCommand + " " + ShellWord(key.Value!)));
        }

        Outcome outcome;
        try
        {
            outcome = await operation();
        }
        catch
        {
            // An unclassified failure is not a decision. The attempt that
            // claimed the row removes it so the operation stays retryable under
            // the same key; an attempt that took the fence over leaves the
            // creator's row in place.
            await fence.AbandonPendingAsync(command, scopeKey, claim.Created);
            throw;
        }

        if (outcome.IsClassified)
        {
            var body = JSON.Serialize(outcome.Envelope);
            var record = new RecordedOutcome(
                outcome.StatusCode,
                JsonDocument.Parse(body).RootElement.Clone());
            var state = outcome.StatusCode >= 400
                ? IdempotencyMappingStates.Rejected
                : IdempotencyMappingStates.Completed;
            var recorded = JSON.Serialize(record);
            try
            {
                await fence.CompleteAsync(command, scopeKey, state, recorded);
            }
            catch (InvalidOperationException)
            {
                // The attempt that claimed the row abandoned it while this one
                // was executing, so the decision this request already made has
                // nowhere to go. Re-create the fence and record it: the caller
                // acted, and a replay must observe that decision rather than a
                // conflict it cannot act on.
                await fence.GetOrCreateAsync(
                    command, scopeKey, currentUser.Principal.Id, fingerprint, turnId: null, initialOutcome: null);
                await fence.CompleteAsync(command, scopeKey, state, recorded);
            }
        }

        return Write(outcome);

        static IResult Write(Outcome result) =>
            Results.Json(result.Envelope, statusCode: result.StatusCode);
    }

    /// <summary>A 503 that carries the pending-lease retry signal.</summary>
    private sealed class PendingResult(ApiResponse<object> envelope) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            httpContext.Response.Headers.RetryAfter = "1";
            return httpContext.Response.WriteAsJsonAsync(envelope, httpContext.RequestAborted);
        }
    }
}
