using System.Buffers;

namespace Mohist.Server.Otel;

/// <summary>
/// Cap on the decoded OTLP request body. The reader is fail-closed: when
/// the next read would push the running total past the limit, the
/// underlying read is not consulted and the reader raises
/// <see cref="OtlpBodyTooLargeException"/> after the previously read
/// bytes remain the only thing retained. Cancellation is honored and
/// never rethrows as a size-limit error.
/// </summary>
/// <remarks>
/// Design D2. The 16 MiB constant is the fixed design limit; it is
/// duplicated here so the test seam can override it without touching
/// production code paths. The reader is read-only and never buffers the
/// excess.
/// </remarks>
public sealed class LimitedOtlpBodyReader
{
    public const int DefaultMaxBytes = 16 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly int _maxBytes;
    private long _totalRead;

    public LimitedOtlpBodyReader(Stream inner, int maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Max bytes must be positive.");
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public int MaxBytes => _maxBytes;

    public long TotalRead => _totalRead;

    public bool Truncated => _totalRead > _maxBytes;

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count <= 0)
            return 0;
        if (offset + count > buffer.Length)
            throw new ArgumentException("Offset and count exceed buffer length.", nameof(count));

        var remaining = _maxBytes - (int)Math.Min(_totalRead, _maxBytes);
        if (remaining <= 0)
        {
            var probe = await _inner.ReadAsync(buffer.AsMemory(offset, 1), ct).ConfigureAwait(false);
            if (probe > 0)
            {
                _totalRead = _maxBytes + 1;
                throw new OtlpBodyTooLargeException(_maxBytes);
            }
            return 0;
        }

        var toRead = Math.Min(count, remaining);
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, toRead), ct).ConfigureAwait(false);
        _totalRead += read;
        if (read == 0)
            return 0;
        if (_totalRead > _maxBytes)
        {
            _totalRead = _maxBytes + 1;
            throw new OtlpBodyTooLargeException(_maxBytes);
        }
        return read;
    }

    public async Task CopyToAsync(Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var remaining = _maxBytes - (int)Math.Min(_totalRead, _maxBytes);
                if (remaining <= 0)
                {
                    var probe = await _inner.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
                    if (probe > 0)
                    {
                        _totalRead = _maxBytes + 1;
                        throw new OtlpBodyTooLargeException(_maxBytes);
                    }
                    return;
                }
                var toRead = Math.Min(buffer.Length, remaining);
                var read = await _inner.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0)
                    return;
                _totalRead += read;
                if (_totalRead > _maxBytes)
                {
                    _totalRead = _maxBytes + 1;
                    throw new OtlpBodyTooLargeException(_maxBytes);
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<byte[]> ReadAllAsync(CancellationToken ct)
    {
        using var output = new MemoryStream();
        await CopyToAsync(output, ct).ConfigureAwait(false);
        return output.ToArray();
    }
}

public sealed class OtlpBodyTooLargeException : Exception
{
    public OtlpBodyTooLargeException(int maxBytes)
        : base($"Decoded telemetry request exceeds {maxBytes} bytes.")
    {
        MaxBytes = maxBytes;
    }

    public int MaxBytes { get; }
}
