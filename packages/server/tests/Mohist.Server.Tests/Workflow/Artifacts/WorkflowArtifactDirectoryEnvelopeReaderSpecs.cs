using System.Security.Cryptography;
using System.Text;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Artifacts;

[Trait("level", "L0")]
public class WorkflowArtifactDirectoryEnvelopeReaderSpecs
{
    [Fact]
    public async Task ReadAsync_YieldsEveryEntryWithPathSizeHashTypeAndContent()
    {
        var alpha = Encoding.UTF8.GetBytes("alpha");
        var beta = Encoding.UTF8.GetBytes("beta!");
        var alphaHash = Sha256(alpha);
        var envelope = DirectoryEnvelopeTestData.Create(
            new DirectoryEnvelopeTestFile("a.md", alpha, "text/markdown", alphaHash),
            new DirectoryEnvelopeTestFile("sub/b.md", beta, "application/json"));

        var entries = await ReadAllAsync(envelope, envelope.LongLength);

        Assert.Equal(["a.md", "sub/b.md"], entries.Select(entry => entry.RelativePath));
        Assert.Equal([alpha.LongLength, beta.LongLength], entries.Select(entry => entry.Size));
        Assert.Equal("text/markdown", entries[0].ContentType);
        Assert.Equal("application/json", entries[1].ContentType);
        Assert.Equal(alphaHash, entries[0].ContentHash);
        Assert.Null(entries[1].ContentHash);
        Assert.Equal(alpha, ReadAllBytes(entries[0]));
        Assert.Equal(beta, ReadAllBytes(entries[1]));
    }

    [Fact]
    public async Task ReadAsync_DeclaredHashIsPassedThroughForStorageVerification()
    {
        var content = Encoding.UTF8.GetBytes("alpha");
        var declared = $"sha256:{new string('a', 64)}";
        var envelope = DirectoryEnvelopeTestData.Create(
            new DirectoryEnvelopeTestFile("a.md", content, ContentHash: declared));

        var entries = await ReadAllAsync(envelope, envelope.LongLength);

        Assert.Equal(declared, Assert.Single(entries).ContentHash);
    }

    [Fact]
    public async Task ReadAsync_MissingSizeFallsBackToDecodedLength()
    {
        var content = Encoding.UTF8.GetBytes("alpha");
        var envelope = DirectoryEnvelopeTestData.Create(
            new DirectoryEnvelopeTestFile("a.md", content));

        var entries = await ReadAllAsync(envelope, envelope.LongLength);

        Assert.Equal(content.LongLength, Assert.Single(entries).Size);
    }

    [Fact]
    public async Task ReadAsync_EmptyEnvelopeIsInvalid()
    {
        await AssertInvalidAsync(Array.Empty<byte>(), 0, contains: "empty");
    }

    [Fact]
    public async Task ReadAsync_WrongKindIsInvalid()
    {
        var envelope = Encoding.UTF8.GetBytes("{\"kind\":\"file\"}");

        await AssertInvalidAsync(envelope, envelope.LongLength, contains: "kind");
    }

    [Fact]
    public async Task ReadAsync_EmptyDirectoryIsInvalid()
    {
        var envelope = Encoding.UTF8.GetBytes("{\"kind\":\"directory\"}");

        await AssertInvalidAsync(envelope, envelope.LongLength, contains: "at least one");
    }

    [Fact]
    public async Task ReadAsync_MissingPathIsInvalid()
    {
        var envelope = Encoding.UTF8.GetBytes(
            "{\"kind\":\"directory\"}\n{\"data\":\"YQ==\"}");

        await AssertInvalidAsync(envelope, envelope.LongLength, contains: "path is required");
    }

    [Fact]
    public async Task ReadAsync_InvalidBase64IsInvalid()
    {
        var envelope = Encoding.UTF8.GetBytes(
            "{\"kind\":\"directory\"}\n{\"path\":\"a.md\",\"data\":\"!!!\"}");

        await AssertInvalidAsync(envelope, envelope.LongLength, contains: "base64");
    }

    [Fact]
    public async Task ReadAsync_MalformedJsonIsInvalid()
    {
        var envelope = Encoding.UTF8.GetBytes("{\"kind\":\"directory\"");

        await AssertInvalidAsync(envelope, envelope.LongLength, contains: "not a valid artifact envelope");
    }

    [Fact]
    public async Task ReadAsync_DeclaredSizeLargerThanActualIsInvalid()
    {
        var envelope = DirectoryEnvelopeTestData.Create(
            new DirectoryEnvelopeTestFile("a.md", Encoding.UTF8.GetBytes("alpha")));

        await AssertInvalidAsync(envelope, envelope.LongLength + 100, contains: "mismatch");
    }

    [Fact]
    public async Task ReadAsync_DeclaredSizeSmallerThanActualTrailingBytesIsInvalid()
    {
        // The declared size ends exactly at a well-formed value boundary,
        // so the parser completes without seeing the trailing bytes; the
        // one-byte EOF probe must still reject the body.
        var valid = DirectoryEnvelopeTestData.Create(
            new DirectoryEnvelopeTestFile("a.md", Encoding.UTF8.GetBytes("alpha")));
        var trailing = Encoding.UTF8.GetBytes("{\"path\":\"b.md\",\"data\":\"YQ==\"}");
        var actual = valid.Concat(trailing).ToArray();

        await AssertInvalidAsync(actual, valid.LongLength, contains: "larger");
    }

    [Fact]
    public async Task ReadAsync_EnvelopeLimitCapsReadsAndRejectsTrailingBody()
    {
        // A lying declared size cannot make the reader retain more than
        // the configured envelope cap: reads stop at the cap and the
        // probe fails the body closed without buffering the remainder.
        var valid = DirectoryEnvelopeTestData.Create(
            new DirectoryEnvelopeTestFile("a.md", Encoding.UTF8.GetBytes("alpha")));
        var trailing = Encoding.UTF8.GetBytes(
            "{\"path\":\"b.md\",\"data\":\"YQ==\"}{\"path\":\"c.md\",\"data\":\"YQ==\"}");
        var actual = valid.Concat(trailing).ToArray();
        var limits = new WorkflowArtifactDirectoryLimits { MaxEnvelopeBytes = valid.LongLength };
        var stream = new CountingStream(actual);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in WorkflowArtifactDirectoryEnvelopeReader.ReadAsync(
                stream, actual.LongLength, limits, CancellationToken.None))
            {
            }
        });

        Assert.Contains("larger", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            stream.BytesRead <= limits.MaxEnvelopeBytes + 1,
            $"reader pulled {stream.BytesRead} bytes past the {limits.MaxEnvelopeBytes}-byte cap");
    }

    private static async Task AssertInvalidAsync(
        byte[] envelope,
        long declaredSize,
        string contains)
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await ReadAllAsync(envelope, declaredSize);
        });

        Assert.Contains(contains, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<WorkflowArtifactDirectoryEntryInput>> ReadAllAsync(
        byte[] envelope,
        long declaredSize,
        WorkflowArtifactDirectoryLimits? limits = null)
    {
        var entries = new List<WorkflowArtifactDirectoryEntryInput>();
        await foreach (var entry in WorkflowArtifactDirectoryEnvelopeReader.ReadAsync(
            new MemoryStream(envelope, writable: false),
            declaredSize,
            limits ?? WorkflowArtifactDirectoryLimits.Default,
            CancellationToken.None))
        {
            entries.Add(entry);
        }
        return entries;
    }

    private static byte[] ReadAllBytes(WorkflowArtifactDirectoryEntryInput entry)
    {
        using var content = entry.OpenContent();
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Sha256(byte[] content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";

    private sealed class CountingStream : Stream
    {
        private readonly MemoryStream _inner;

        public CountingStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = _inner.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
