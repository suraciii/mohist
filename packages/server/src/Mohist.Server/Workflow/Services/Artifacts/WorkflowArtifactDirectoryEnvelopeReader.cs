using System.Runtime.CompilerServices;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Services.Artifacts;

/// <summary>
/// Streams a directory artifact envelope one entry at a time. The
/// envelope is newline-delimited JSON: the first top-level value must
/// declare <c>{"kind":"directory"}</c> and every following value is one
/// contained file with <c>path</c>, <c>size</c>, <c>contentHash</c>,
/// <c>contentType</c>, and base64 <c>data</c>.
/// </summary>
internal static class WorkflowArtifactDirectoryEnvelopeReader
{
    public const string ContentType = "application/x-mohist-artifact-directory";

    public static bool IsDirectoryContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && string.Equals(contentType, ContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses the envelope lazily. The caller retains at most the entry it
    /// is currently writing: the next value is not parsed until the
    /// returned enumerable is advanced again. Reads are capped at
    /// <c>min(limits.MaxEnvelopeBytes, declaredSize)</c> and a one-byte
    /// EOF probe after the enumeration fails closed when the actual body
    /// is larger than the declared size.
    /// </summary>
    public static IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> ReadAsync(
        Stream content,
        long declaredSize,
        WorkflowArtifactDirectoryLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        if (declaredSize < 0)
            throw new InvalidDataException("Directory envelope declared size must be zero or positive.");

        // Never pull more than the effective envelope cap from the
        // underlying stream; an over-limit body is detected by the EOF
        // probe after the capped read reaches its artificial end.
        var cap = Math.Min(limits.MaxEnvelopeBytes, declaredSize);
        var bounded = new BoundedReadStream(content, cap);
        return ReadCoreAsync(bounded, declaredSize, cancellationToken);
    }

    private static async IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> ReadCoreAsync(
        BoundedReadStream content,
        long declaredSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var headerSeen = false;
        var entryCount = 0;

        var values = WrapJsonErrors(
            JsonSerializer.DeserializeAsyncEnumerable<DirectoryEnvelopeValueDto>(
                content, topLevelValues: true, JSON.Options, cancellationToken));

        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (value is null)
                throw new InvalidDataException("Directory upload envelope contains a null value.");

            if (!headerSeen)
            {
                if (!string.Equals(value.Kind, "directory", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Directory upload envelope must declare kind: \"directory\".");
                headerSeen = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(value.Path))
                throw new InvalidDataException("Directory entry path is required.");

            byte[] data;
            try
            {
                data = Convert.FromBase64String(value.Data ?? string.Empty);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Directory entry '{value.Path}' data is not valid base64: {ex.Message}", ex);
            }

            entryCount++;
            yield return new WorkflowArtifactDirectoryEntryInput
            {
                RelativePath = value.Path,
                Size = value.Size ?? data.LongLength,
                // Pass the declared hash through so the storage can verify
                // it; the storage records the computed hash.
                ContentHash = value.ContentHash,
                ContentType = value.ContentType,
                OpenContent = () => new MemoryStream(data, writable: false),
            };
        }

        if (!headerSeen)
            throw new InvalidDataException(
                "Directory upload envelope is empty; it must start with a kind: \"directory\" header.");
        if (entryCount == 0)
            throw new InvalidDataException(
                "Directory upload envelope must contain at least one contained file.");

        // The parser can complete a well-formed value sequence exactly at
        // the capped artificial EOF, leaving the actual trailing bytes
        // unread. A one-byte probe fails the request closed before any
        // byte beyond the cap is retained.
        if (await content.HasMoreBytesAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException(
                $"Directory envelope size mismatch: declared {declaredSize} bytes, but the content is larger.");

        if (content.BytesRead != declaredSize)
            throw new InvalidDataException(
                $"Directory envelope size mismatch: declared {declaredSize} bytes, read {content.BytesRead} bytes.");
    }

    /// <summary>
    /// Surfaces parser failures as <see cref="InvalidDataException"/> so
    /// malformed envelopes become a diagnosable client error instead of
    /// an opaque 500.
    /// </summary>
    private static async IAsyncEnumerable<T> WrapJsonErrors<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            T current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;
                current = enumerator.Current;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Directory upload content is not a valid artifact envelope: {ex.Message}", ex);
            }

            yield return current;
        }
    }

    private sealed class DirectoryEnvelopeValueDto
    {
        public string? Kind { get; set; }
        public string? Path { get; set; }
        public long? Size { get; set; }
        public string? ContentHash { get; set; }
        public string? ContentType { get; set; }
        public string? Data { get; set; }
    }

    /// <summary>
    /// Read-only view over the envelope stream that stops at an artificial
    /// EOF after at most <c>maxBytes</c> bytes and never pulls more from
    /// the underlying stream.
    /// </summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _bytesRead;

        public BoundedReadStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public long BytesRead => _bytesRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _maxBytes - _bytesRead;
            if (remaining <= 0)
                return 0;
            var toRead = (int)Math.Min(count, remaining);
            if (toRead <= 0)
                return 0;
            var read = _inner.Read(buffer, offset, toRead);
            _bytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ReadBoundedAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadBoundedAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public async Task<bool> HasMoreBytesAsync(CancellationToken cancellationToken)
        {
            var probe = new byte[1];
            var read = await _inner
                .ReadAsync(probe.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);
            return read > 0;
        }

        private async ValueTask<int> ReadBoundedAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var remaining = _maxBytes - _bytesRead;
            if (remaining <= 0)
                return 0;
            var toRead = (int)Math.Min(buffer.Length, remaining);
            if (toRead <= 0)
                return 0;
            var read = await _inner
                .ReadAsync(buffer[..toRead], cancellationToken)
                .ConfigureAwait(false);
            _bytesRead += read;
            return read;
        }
    }
}
