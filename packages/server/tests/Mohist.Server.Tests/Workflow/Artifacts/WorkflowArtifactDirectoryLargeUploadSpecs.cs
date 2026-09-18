using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Artifacts;

/// <summary>
/// The configured directory limits exercised by the large-directory Specs.
/// Declared as constants so the measurement bounds can name the same numbers
/// the ingestion pipeline is configured with.
/// </summary>
internal static class LargeDirectorySpecLimits
{
    public const long MaxFileBytes = 512 * 1024;
    public const long MaxTotalBytes = 4 * 1024 * 1024;
    public const int MaxFileCount = 32;
    public const long MaxEnvelopeBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Deterministic large-directory payload. Content is unique per file so
    /// every recorded content hash is distinct.
    /// </summary>
    public static DirectoryEnvelopeTestFile[] BuildFiles(int count = 6, int bytesEach = 160 * 1024)
    {
        var files = new DirectoryEnvelopeTestFile[count];
        for (var i = 0; i < count; i++)
        {
            var content = new byte[bytesEach];
            for (var b = 0; b < bytesEach; b++)
                content[b] = (byte)((b * 31 + i * 17) % 251);
            files[i] = new DirectoryEnvelopeTestFile(
                Path: $"specs/part-{i:D2}.bin",
                Content: content,
                ContentType: "application/octet-stream",
                Size: bytesEach);
        }
        return files;
    }

    public static WorkflowArtifactDirectoryLimits ToLimits() => new()
    {
        MaxFileBytes = MaxFileBytes,
        MaxTotalBytes = MaxTotalBytes,
        MaxFileCount = MaxFileCount,
        MaxEnvelopeBytes = MaxEnvelopeBytes,
    };
}

/// <summary>
/// Acceptance Specs for bounded retention while ingesting a large directory
/// artifact.
///
/// Measurement method: a deterministic read-ahead probe, never GC sampling.
/// A <see cref="CountingStream"/> wraps the envelope and reports total bytes
/// read. A probe <see cref="IWorkflowArtifactStorage"/> enumerates entries and,
/// because the Spec generated the exact NDJSON byte length of every entry,
/// records:
/// <code>
/// PeakEnvelopeLookaheadBytes = max(bytesRead - encodedBytesOfEntriesAlreadyYielded)
/// PeakOpenContentBytes       = max(decoded bytes of entry content streams open at once)
/// </code>
/// It also captures the bytes-read when the first entry arrived, which is the
/// eager-reader regression signature: an eager reader would have consumed the
/// whole envelope before yielding entry one.
/// </summary>
[Trait("level", "L0")]
public class WorkflowArtifactDirectoryLargeUploadProbeSpecs
{
    /// <summary>Fixed allowance for the streaming JSON parser's read buffer.</summary>
    private const long ParserReadBufferSlack = 64 * 1024;

    /// <summary>Fixed allowance for <c>WorkflowArtifactStreamCopier</c>'s 80 KiB copy buffer.</summary>
    private const long StorageCopyBufferSlack = 80 * 1024;

    private static readonly DateTimeOffset RecordedAt =
        new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LargeDirectory_StreamsOneEntryAtATimeWithBoundedRetention()
    {
        var limits = LargeDirectorySpecLimits.ToLimits();
        var files = LargeDirectorySpecLimits.BuildFiles();
        var envelope = DirectoryEnvelopeTestData.Create(files);
        var entryEncodedLengths = DirectoryEnvelopeTestData.EntryEncodedLengths(files);

        var counting = new CountingStream(envelope);
        var inner = new InMemoryWorkflowArtifactStorage();
        var probe = new ReadAheadProbeStorage(
            inner,
            () => counting.BytesRead,
            envelope.LongLength,
            entryEncodedLengths);
        var storagePath = inner.GenerateStoragePath(
            "wr_probe", "build", "art_probe", WorkflowArtifactStorageKind.Directory);

        var entries = WorkflowArtifactDirectoryEnvelopeReader.ReadAsync(
            counting, envelope.LongLength, limits);
        var result = await probe.WriteDirectoryAsync(
            storagePath, entries, WriteFor("specs/", envelope.LongLength), RecordedAt, limits);

        Assert.Equal(files.Length, result.FileCount);

        var maxEntryEncodedBytes = entryEncodedLengths.Max();
        Assert.True(
            probe.PeakEnvelopeLookaheadBytes <= maxEntryEncodedBytes + ParserReadBufferSlack,
            $"peak envelope lookahead {probe.PeakEnvelopeLookaheadBytes} exceeded one entry "
            + $"({maxEntryEncodedBytes}) plus the parser buffer ({ParserReadBufferSlack})");
        Assert.True(
            probe.PeakOpenContentBytes <= LargeDirectorySpecLimits.MaxFileBytes + StorageCopyBufferSlack,
            $"peak open decoded content {probe.PeakOpenContentBytes} exceeded one file "
            + $"({LargeDirectorySpecLimits.MaxFileBytes}) plus the storage copy buffer ({StorageCopyBufferSlack})");
        Assert.True(
            probe.FirstEntryProcessedBeforeWholeEnvelopeRead,
            $"the reader consumed all {envelope.LongLength} envelope bytes before the first entry was processed");
    }

    [Fact]
    public async Task LargeDirectory_DeclaredSmallerThanActualTrailingBytes_IsRejectedWithoutRetainingTrailingBody()
    {
        var limits = LargeDirectorySpecLimits.ToLimits();
        var files = LargeDirectorySpecLimits.BuildFiles();
        var actual = DirectoryEnvelopeTestData.Create(files);
        var entryEncodedLengths = DirectoryEnvelopeTestData.EntryEncodedLengths(files);
        var headerBytes = DirectoryEnvelopeTestData.Create().LongLength;

        // Declare a size that ends exactly at the newline after the first
        // entry: a well-formed value boundary, so the parser can complete the
        // sequence at the artificial EOF while the real body continues.
        var declaredSize = headerBytes + entryEncodedLengths[0];
        var counting = new CountingStream(actual);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in WorkflowArtifactDirectoryEnvelopeReader.ReadAsync(
                counting, declaredSize, limits, CancellationToken.None))
            {
            }
        });

        Assert.Contains("larger", exception.Message, StringComparison.OrdinalIgnoreCase);

        // The trailing body is never retained: the reader stops at the declared
        // cap and the EOF probe pulls at most one more byte to detect it.
        Assert.True(
            counting.BytesRead <= declaredSize + 1,
            $"reader pulled {counting.BytesRead} bytes past the {declaredSize}-byte declared cap");
        Assert.True(
            counting.BytesRead <= LargeDirectorySpecLimits.MaxEnvelopeBytes + 1,
            $"reader pulled {counting.BytesRead} bytes past the {LargeDirectorySpecLimits.MaxEnvelopeBytes}-byte envelope cap");
        Assert.True(
            counting.BytesRead < actual.LongLength,
            $"the reader consumed the trailing body ({counting.BytesRead} of {actual.LongLength} bytes)");
    }

    [Fact]
    public async Task LargeDirectory_DeclaredLargerThanActual_IsRejectedWithSizeMismatch()
    {
        var limits = LargeDirectorySpecLimits.ToLimits();
        var files = LargeDirectorySpecLimits.BuildFiles();
        var actual = DirectoryEnvelopeTestData.Create(files);
        var declaredSize = actual.LongLength + 4096;
        var counting = new CountingStream(actual);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in WorkflowArtifactDirectoryEnvelopeReader.ReadAsync(
                counting, declaredSize, limits, CancellationToken.None))
            {
            }
        });

        Assert.Contains("mismatch", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(actual.LongLength, counting.BytesRead);
    }

    [Fact]
    public async Task LargeDirectory_RoundTripsEveryPathSizeAndRecordedHash()
    {
        var limits = LargeDirectorySpecLimits.ToLimits();
        var files = LargeDirectorySpecLimits.BuildFiles();
        var envelope = DirectoryEnvelopeTestData.Create(files);

        var storage = new InMemoryWorkflowArtifactStorage();
        var storagePath = storage.GenerateStoragePath(
            "wr_roundtrip", "build", "art_roundtrip", WorkflowArtifactStorageKind.Directory);

        var entries = WorkflowArtifactDirectoryEnvelopeReader.ReadAsync(
            new CountingStream(envelope), envelope.LongLength, limits);
        var result = await storage.WriteDirectoryAsync(
            storagePath, entries, WriteFor("specs/", envelope.LongLength), RecordedAt, limits);

        Assert.Equal(files.Length, result.FileCount);

        var metadata = await storage.ReadMetadataAsync(storagePath);
        Assert.NotNull(metadata);
        var expected = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            expected.Select(file => file.Path),
            metadata!.Entries!.Select(entry => entry.RelativePath));
        Assert.Equal(
            expected.Select(file => file.Content.LongLength),
            metadata.Entries!.Select(entry => entry.Size));
        Assert.Equal(
            expected.Select(file => Sha256(file.Content)),
            metadata.Entries!.Select(entry => entry.ContentHash));

        foreach (var file in expected)
        {
            await using var content = storage.OpenDirectoryEntry(storagePath, file.Path);
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer);
            Assert.Equal(file.Content, buffer.ToArray());
        }
    }

    private static WorkflowArtifactFileWrite WriteFor(string sourcePath, long size) => new()
    {
        SourcePath = sourcePath,
        Size = size,
        ContentType = WorkflowArtifactDirectoryEnvelopeReader.ContentType,
        ContentHash = "sha256:dir",
    };

    private static string Sha256(byte[] content) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}";

    /// <summary>
    /// Wraps the envelope reader and records peak read-ahead and peak open
    /// decoded content while delegating the actual write to the in-memory
    /// adapter.
    /// </summary>
    private sealed class ReadAheadProbeStorage : IWorkflowArtifactStorage
    {
        private readonly InMemoryWorkflowArtifactStorage _inner;
        private readonly Func<long> _envelopeBytesRead;
        private readonly long _totalEnvelopeBytes;
        private readonly IReadOnlyList<long> _entryEncodedLengths;
        private long _openContentBytes;

        public ReadAheadProbeStorage(
            InMemoryWorkflowArtifactStorage inner,
            Func<long> envelopeBytesRead,
            long totalEnvelopeBytes,
            IReadOnlyList<long> entryEncodedLengths)
        {
            _inner = inner;
            _envelopeBytesRead = envelopeBytesRead;
            _totalEnvelopeBytes = totalEnvelopeBytes;
            _entryEncodedLengths = entryEncodedLengths;
        }

        public long PeakEnvelopeLookaheadBytes { get; private set; }

        public long PeakOpenContentBytes { get; private set; }

        public bool FirstEntryProcessedBeforeWholeEnvelopeRead { get; private set; }

        public async Task<WorkflowArtifactStorageWriteResult> WriteDirectoryAsync(
            string storagePath,
            IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> entries,
            WorkflowArtifactFileWrite write,
            DateTimeOffset recordedAt,
            WorkflowArtifactDirectoryLimits? limits = null,
            CancellationToken cancellationToken = default) =>
            await _inner.WriteDirectoryAsync(
                storagePath,
                Observe(entries, cancellationToken),
                write,
                recordedAt,
                limits,
                cancellationToken).ConfigureAwait(false);

        private async IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> Observe(
            IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> entries,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            long yieldedEncodedBytes = 0;
            var index = 0;
            var first = true;

            await foreach (var entry in entries
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                var bytesRead = _envelopeBytesRead();
                PeakEnvelopeLookaheadBytes = Math.Max(
                    PeakEnvelopeLookaheadBytes, bytesRead - yieldedEncodedBytes);
                if (first)
                {
                    FirstEntryProcessedBeforeWholeEnvelopeRead = bytesRead < _totalEnvelopeBytes;
                    first = false;
                }

                yieldedEncodedBytes += _entryEncodedLengths[index++];

                yield return new WorkflowArtifactDirectoryEntryInput
                {
                    RelativePath = entry.RelativePath,
                    Size = entry.Size,
                    ContentHash = entry.ContentHash,
                    ContentType = entry.ContentType,
                    OpenContent = () =>
                    {
                        var opened = entry.OpenContent();
                        _openContentBytes += entry.Size;
                        PeakOpenContentBytes = Math.Max(PeakOpenContentBytes, _openContentBytes);
                        return new OpenContentTrackingStream(opened, () => _openContentBytes -= entry.Size);
                    },
                };
            }
        }

        public string GenerateStoragePath(
            string workflowRunId,
            string actionAttemptId,
            string artifactId,
            WorkflowArtifactStorageKind kind) =>
            _inner.GenerateStoragePath(workflowRunId, actionAttemptId, artifactId, kind);

        public Task<WorkflowArtifactStorageWriteResult> WriteFileAsync(
            string storagePath,
            Stream content,
            WorkflowArtifactFileWrite write,
            DateTimeOffset recordedAt,
            CancellationToken cancellationToken = default) =>
            _inner.WriteFileAsync(storagePath, content, write, recordedAt, cancellationToken);

        public Stream OpenFileContent(string storagePath) => _inner.OpenFileContent(storagePath);

        public Task<WorkflowArtifactDirectoryListing> ListDirectoryEntriesAsync(
            string storagePath,
            CancellationToken cancellationToken = default) =>
            _inner.ListDirectoryEntriesAsync(storagePath, cancellationToken);

        public Stream OpenDirectoryEntry(string storagePath, string relativePath) =>
            _inner.OpenDirectoryEntry(storagePath, relativePath);

        public Task<WorkflowArtifactStorageMetadata?> ReadMetadataAsync(
            string storagePath,
            CancellationToken cancellationToken = default) =>
            _inner.ReadMetadataAsync(storagePath, cancellationToken);

        public Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(storagePath, cancellationToken);

        public string ResolveAbsolutePath(string storagePath) => _inner.ResolveAbsolutePath(storagePath);

        public string StorageRoot => _inner.StorageRoot;
    }

    /// <summary>
    /// Decrements the open-content accounting when the storage releases the
    /// decoded entry stream.
    /// </summary>
    private sealed class OpenContentTrackingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action _onDispose;
        private bool _disposed;

        public OpenContentTrackingStream(Stream inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _inner.Dispose();
                    _onDispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Counts every byte pulled from the envelope, including the read-ahead the
    /// parser performs. The probe treats this count as the retained envelope
    /// high-water mark.
    /// </summary>
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

/// <summary>
/// Acceptance Specs that prove a failed or cancelled large-directory upload
/// leaves no pending upload row and no listable or readable storage content,
/// and that cancellation releases the content/enumerator resources.
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public class WorkflowArtifactDirectoryLargeUploadCleanupSpecs
{
    private static readonly DateTimeOffset RecordedAt =
        new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly MohistDbFixture _fixture;
    private readonly InMemoryWorkflowArtifactStorage _storage = new();

    public WorkflowArtifactDirectoryLargeUploadCleanupSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UploadAsync_LargeDirectoryTotalSizeFailure_LeavesNoPendingRowOrListableContent()
    {
        var limits = LargeDirectorySpecLimits.ToLimits();
        limits.MaxTotalBytes = 400 * 1024;
        var (service, workflowRunId, workId) = BuildService(limits);

        // Three 160 KiB files: the first two fit under the total, the third
        // breaches the limit after partial content has already been written.
        var files = LargeDirectorySpecLimits.BuildFiles(count: 3, bytesEach: 160 * 1024);
        var envelope = DirectoryEnvelopeTestData.Create(files);

        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = WorkflowArtifactDirectoryEnvelopeReader.ContentType,
            ContentHash = "sha256:total-breach",
            Size = envelope.LongLength,
            OpenContent = () => new MemoryStream(envelope, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Invalid, result.Kind);
        await AssertRolledBackAsync(workflowRunId);
    }

    [Fact]
    public async Task UploadAsync_LargeDirectoryDecodeFailure_LeavesNoPendingRowOrListableContent()
    {
        var (service, workflowRunId, workId) = BuildService();

        // A valid large first entry followed by a value whose base64 is
        // invalid: the reader fails after the storage has already written
        // the first entry.
        var large = LargeDirectorySpecLimits.BuildFiles(count: 1, bytesEach: 160 * 1024);
        var prefix = DirectoryEnvelopeTestData.Serialize(large[0]);
        var malformed = prefix + "{\"path\":\"specs/bad.bin\",\"data\":\"!!!\"}\n";
        var envelope = Encoding.UTF8.GetBytes(malformed);

        var result = await service.UploadAsync(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            Path = "specs",
            ContentType = WorkflowArtifactDirectoryEnvelopeReader.ContentType,
            ContentHash = "sha256:decode-breach",
            Size = envelope.LongLength,
            OpenContent = () => new MemoryStream(envelope, writable: false),
        });

        Assert.Equal(WorkflowArtifactUploadResultKind.Invalid, result.Kind);
        await AssertRolledBackAsync(workflowRunId);
    }

    [Fact]
    public async Task UploadAsync_CancelledMidStream_ReleasesContentAndRollsBackWithIndependentToken()
    {
        var (service, workflowRunId, workId) = BuildService();
        var files = LargeDirectorySpecLimits.BuildFiles();
        var envelope = DirectoryEnvelopeTestData.Create(files);
        var entryEncodedLengths = DirectoryEnvelopeTestData.EntryEncodedLengths(files);
        var headerBytes = DirectoryEnvelopeTestData.Create().LongLength;
        var prefix = envelope.AsSpan(0, (int)(headerBytes + entryEncodedLengths[0])).ToArray();

        using var cancellation = new CancellationTokenSource();
        var stream = new CancelAfterPrefixStream(prefix, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(
            new WorkflowArtifactUploadRequest
            {
                WorkflowRunId = workflowRunId,
                WorkId = workId,
                Path = "specs",
                ContentType = WorkflowArtifactDirectoryEnvelopeReader.ContentType,
                ContentHash = "sha256:cancelled",
                Size = envelope.LongLength,
                OpenContent = () => stream,
            },
            cancellation.Token));

        Assert.True(stream.Disposed, "the content stream must be released when the upload is cancelled");
        Assert.Equal(CancellationToken.None, _storage.LastDeleteCancellationToken);
        await AssertRolledBackAsync(workflowRunId);
    }

    [Fact]
    public async Task WriteDirectoryAsync_CancellationDisposesTheEntryEnumerator()
    {
        var limits = LargeDirectorySpecLimits.ToLimits();
        var storage = new InMemoryWorkflowArtifactStorage();
        var storagePath = storage.GenerateStoragePath(
            "wr_enum", "build", "art_enum", WorkflowArtifactStorageKind.Directory);
        using var cancellation = new CancellationTokenSource();
        var enumeratorDisposed = false;

        async IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput> Entries(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return Entry("specs/a.bin", new byte[1024]);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                enumeratorDisposed = true;
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.WriteDirectoryAsync(
            storagePath,
            Entries(),
            WriteFor("specs/", 1024),
            RecordedAt,
            limits,
            cancellation.Token));

        Assert.True(enumeratorDisposed, "the storage must dispose the entry enumerator when the write is cancelled");
        Assert.False(storage.Contains(storagePath));
    }

    private (WorkflowArtifactUploadService Service, string WorkflowRunId, string WorkId) BuildService(
        WorkflowArtifactDirectoryLimits? limits = null)
    {
        var workflowRunId = $"wr_{Guid.NewGuid():N}";
        var workId = $"task-1.1_{Guid.NewGuid():N}";
        var resolver = new StubWorkContextResolver();
        resolver.Register(workflowRunId, workId, "task-1.1");

        var service = new WorkflowArtifactUploadService(
            _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            _storage,
            resolver,
            NullLogger<WorkflowArtifactUploadService>.Instance,
            new FixedTimeProvider(RecordedAt),
            TimeSpan.FromHours(24),
            Options.Create(new WorkflowArtifactStorageOptions
            {
                DirectoryLimits = limits ?? LargeDirectorySpecLimits.ToLimits(),
            }));

        return (service, workflowRunId, workId);
    }

    private async Task AssertRolledBackAsync(string workflowRunId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var rows = await db.WorkflowArtifactPendingUploads
            .AsNoTracking()
            .Where(row => row.WorkflowRunId == workflowRunId)
            .ToListAsync();
        Assert.Empty(rows);

        var storagePath = _storage.LastGeneratedStoragePath;
        Assert.NotNull(storagePath);
        Assert.False(_storage.Contains(storagePath!));
        await Assert.ThrowsAsync<WorkflowArtifactNotFoundException>(() =>
            _storage.ListDirectoryEntriesAsync(storagePath!));
    }

    private static WorkflowArtifactDirectoryEntryInput Entry(string path, byte[] content) => new()
    {
        RelativePath = path,
        Size = content.LongLength,
        ContentType = "application/octet-stream",
        OpenContent = () => new MemoryStream(content, writable: false),
    };

    private static WorkflowArtifactFileWrite WriteFor(string sourcePath, long size) => new()
    {
        SourcePath = sourcePath,
        Size = size,
        ContentType = WorkflowArtifactDirectoryEnvelopeReader.ContentType,
        ContentHash = "sha256:enum",
    };

    /// <summary>
    /// Serves the header plus the first entry, then cancels the upload so the
    /// reader observes cancellation while it waits for the next value.
    /// </summary>
    private sealed class CancelAfterPrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly CancellationTokenSource _cancellation;
        private int _position;

        public CancelAfterPrefixStream(byte[] prefix, CancellationTokenSource cancellation)
        {
            _prefix = prefix;
            _cancellation = cancellation;
        }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _prefix.LongLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _prefix.Length)
                Cancel();
            var read = Math.Min(count, _prefix.Length - _position);
            Array.Copy(_prefix, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= _prefix.Length)
                Cancel();
            var read = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private void Cancel()
        {
            _cancellation.Cancel();
            throw new OperationCanceledException(_cancellation.Token);
        }
    }
}
