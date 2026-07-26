using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace Mohist.Server.Otel;

public sealed class OtlpTraceResponseWriter
{
    public const string JsonContentType = "application/json";
    public const string ProtobufContentType = "application/x-protobuf";
    public const int ResourceExhaustedCode = 8;
    public const string DecodedSizeExceededMessage = "Decoded telemetry request exceeds 16 MiB.";
    public const string TemporaryAdmissionMessage = "Telemetry receiver is at capacity.";
    public const int RetryAfterSeconds = 1;

    public Task WriteSuccessAsync(HttpResponse response, string? requestContentType) =>
        WriteAsync(response, requestContentType, StatusCodes.Status200OK, null);

    public Task WriteOutcomeAsync(HttpResponse response, string? requestContentType, IngestOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.ResponseDisposition switch
        {
            IngestResponseDisposition.Success => WriteSuccessAsync(response, requestContentType),
            IngestResponseDisposition.PartialSuccess => WritePartialSuccessAsync(response, requestContentType, outcome),
            IngestResponseDisposition.RetryableFailure => WriteStatusAsync(
                response, requestContentType, StatusCodes.Status503ServiceUnavailable, 14, "Telemetry storage is temporarily unavailable."),
            IngestResponseDisposition.Cancelled => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }

    public Task WriteInvalidArgumentAsync(HttpResponse response, string? requestContentType, string message) =>
        WriteStatusAsync(response, requestContentType, StatusCodes.Status400BadRequest, 3, message);

    public Task WriteUnsupportedMediaAsync(HttpResponse response, string? message) =>
        WriteStatusAsync(response, JsonContentType, StatusCodes.Status415UnsupportedMediaType, 3, message ?? "Unsupported media type.");

    public Task WriteDecodedSizeExceededAsync(HttpResponse response, string? requestContentType) =>
        WriteResourceExhaustedAsync(response, requestContentType, StatusCodes.Status413PayloadTooLarge, DecodedSizeExceededMessage, retryAfterSeconds: null);

    public Task WriteTemporaryAdmissionAsync(HttpResponse response, string? requestContentType, int retryAfterSeconds = RetryAfterSeconds) =>
        WriteResourceExhaustedAsync(response, requestContentType, StatusCodes.Status429TooManyRequests, TemporaryAdmissionMessage, retryAfterSeconds);

    private Task WritePartialSuccessAsync(HttpResponse response, string? requestContentType, IngestOutcome outcome)
    {
        var count = checked(outcome.Rejected + outcome.Dropped);
        var message = BuildPartialMessage(outcome);
        return WriteAsync(response, requestContentType, StatusCodes.Status200OK, (count, message));
    }

    private async Task WriteAsync(
        HttpResponse response,
        string? requestContentType,
        int statusCode,
        (long RejectedSpans, string Message)? partial)
    {
        var protobuf = IsProtobuf(requestContentType);
        response.StatusCode = statusCode;
        response.ContentType = protobuf ? ProtobufContentType : JsonContentType;
        if (partial is null)
        {
            if (protobuf)
                return;
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes("{}"));
            return;
        }

        if (protobuf)
        {
            var output = new MemoryStream();
            using (var coded = new CodedOutputStream(output, leaveOpen: true))
            {
                coded.WriteTag(10);
                var size = CodedOutputStream.ComputeTagSize(1)
                    + CodedOutputStream.ComputeInt64Size(partial.Value.RejectedSpans)
                    + CodedOutputStream.ComputeTagSize(2)
                    + CodedOutputStream.ComputeStringSize(partial.Value.Message);
                coded.WriteLength(size);
                coded.WriteTag(8);
                coded.WriteInt64(partial.Value.RejectedSpans);
                coded.WriteTag(18);
                coded.WriteString(partial.Value.Message);
                coded.Flush();
            }
            await response.Body.WriteAsync(output.ToArray());
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            partialSuccess = new
            {
                rejectedSpans = partial.Value.RejectedSpans.ToString(System.Globalization.CultureInfo.InvariantCulture),
                errorMessage = partial.Value.Message,
            },
        });
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(json));
    }

    private Task WriteStatusAsync(
        HttpResponse response,
        string? requestContentType,
        int statusCode,
        int code,
        string message) =>
        WriteStatusCoreAsync(response, requestContentType, statusCode, code, message);

    private Task WriteResourceExhaustedAsync(
        HttpResponse response,
        string? requestContentType,
        int statusCode,
        string message,
        int? retryAfterSeconds)
    {
        if (retryAfterSeconds is int seconds && seconds > 0)
        {
            response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return WriteStatusCoreAsync(response, requestContentType, statusCode, ResourceExhaustedCode, message);
    }

    private async Task WriteStatusCoreAsync(
        HttpResponse response,
        string? requestContentType,
        int statusCode,
        int code,
        string message)
    {
        var bounded = Bound(message);
        var protobuf = IsProtobuf(requestContentType);
        response.StatusCode = statusCode;
        response.ContentType = protobuf ? ProtobufContentType : JsonContentType;
        if (!protobuf)
        {
            var json = JsonSerializer.Serialize(new { code, message = bounded });
            await response.Body.WriteAsync(Encoding.UTF8.GetBytes(json));
            return;
        }

        var output = new MemoryStream();
        using (var coded = new CodedOutputStream(output, leaveOpen: true))
        {
            coded.WriteTag(8);
            coded.WriteInt32(code);
            coded.WriteTag(18);
            coded.WriteString(bounded);
            coded.Flush();
        }
        await response.Body.WriteAsync(output.ToArray());
    }

    private static bool IsProtobuf(string? contentType) =>
        string.Equals(contentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase)
        || string.Equals(contentType, "application/protobuf", StringComparison.OrdinalIgnoreCase);

    private static string BuildPartialMessage(IngestOutcome outcome)
    {
        if (outcome.Rejected != 0 && outcome.Dropped != 0)
            return "Some spans were rejected and malformed spans were dropped.";
        if (outcome.Rejected != 0)
            return "Some spans were rejected by telemetry protection.";
        return "Malformed spans were dropped.";
    }

    private static string Bound(string message) =>
        message.Length <= 256 ? message : message[..256];
}
