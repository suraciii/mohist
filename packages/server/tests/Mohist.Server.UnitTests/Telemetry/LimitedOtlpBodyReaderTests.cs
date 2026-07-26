using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class LimitedOtlpBodyReaderTests
{
    [Fact]
    public async Task ReadAllAsync_BodyAtLimit_ReturnsBytes()
    {
        var bytes = new byte[LimitedOtlpBodyReader.DefaultMaxBytes];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i & 0xFF);

        using var stream = new MemoryStream(bytes);
        var reader = new LimitedOtlpBodyReader(stream);

        var result = await reader.ReadAllAsync(CancellationToken.None);

        Assert.Equal(bytes.Length, result.Length);
        Assert.Equal(bytes, result);
    }

    [Fact]
    public async Task ReadAllAsync_BodyOverLimit_ThrowsOtlpBodyTooLarge()
    {
        var bytes = new byte[LimitedOtlpBodyReader.DefaultMaxBytes + 1];

        using var stream = new MemoryStream(bytes);
        var reader = new LimitedOtlpBodyReader(stream);

        await Assert.ThrowsAsync<OtlpBodyTooLargeException>(() => reader.ReadAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAllAsync_BodyFarOverLimit_ThrowsOtlpBodyTooLarge()
    {
        var bytes = new byte[LimitedOtlpBodyReader.DefaultMaxBytes * 2];

        using var stream = new MemoryStream(bytes);
        var reader = new LimitedOtlpBodyReader(stream);

        await Assert.ThrowsAsync<OtlpBodyTooLargeException>(() => reader.ReadAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAllAsync_EmptyStream_ReturnsEmpty()
    {
        using var stream = new MemoryStream();
        var reader = new LimitedOtlpBodyReader(stream);

        var result = await reader.ReadAllAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadAllAsync_SmallerLimitWithOverrun_Throws()
    {
        var bytes = new byte[128];
        using var stream = new MemoryStream(bytes);
        var reader = new LimitedOtlpBodyReader(stream, maxBytes: 64);

        await Assert.ThrowsAsync<OtlpBodyTooLargeException>(() => reader.ReadAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadAllAsync_CustomLimit_AcceptsPayloadAtBound()
    {
        var bytes = new byte[64];
        using var stream = new MemoryStream(bytes);
        var reader = new LimitedOtlpBodyReader(stream, maxBytes: 64);

        var result = await reader.ReadAllAsync(CancellationToken.None);

        Assert.Equal(bytes, result);
    }

    [Fact]
    public void Constructor_ZeroOrNegativeMax_Throws()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new LimitedOtlpBodyReader(stream, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LimitedOtlpBodyReader(stream, -1));
    }

    [Fact]
    public void OtlpBodyTooLargeException_ExposesMaxBytes()
    {
        var ex = new OtlpBodyTooLargeException(1024);
        Assert.Equal(1024, ex.MaxBytes);
        Assert.Contains("1024", ex.Message);
    }
}
