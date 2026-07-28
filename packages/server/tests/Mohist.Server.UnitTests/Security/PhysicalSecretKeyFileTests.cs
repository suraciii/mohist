using EnvironmentAbstractions.TestHelpers;
using Mohist.Server.Infrastructure.Security.Secrets;
using Xunit;

namespace Mohist.Server.UnitTests.Security;

public sealed class PhysicalSecretKeyFileTests
{
    private const string Path = "/mohist-tests/master.key";

    [Fact]
    public async Task EnsureKeyAsync_WritesKeyWhenFileMissing()
    {
        var ops = new FakeOperations();
        var store = NewStore(ops);

        var key = await store.EnsureKeyAsync(Path);

        Assert.Equal(32, key.Length);
        Assert.True(ops.Exists(Path));
        Assert.Equal(key, ops.ReadAllBytes(Path));
    }

    [Fact]
    public async Task EnsureKeyAsync_Persists0600ModeOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return;

        var ops = new FakeOperations();
        ops.ForceMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var store = NewStore(ops);

        await store.EnsureKeyAsync(Path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ops.ModeFor(Path));
    }

    [Fact]
    public async Task EnsureKeyAsync_ReusesExistingKeyWhenFilePresent()
    {
        var ops = new FakeOperations();
        var seed = NewKey(0xCD);
        ops.Seed(Path, seed);
        var store = NewStore(ops);

        var key = await store.EnsureKeyAsync(Path);

        Assert.Equal(seed, key);
        Assert.Empty(ops.Writes);
    }

    [Fact]
    public async Task TryLoadAsync_ReturnsKeyWhenFileExists()
    {
        var ops = new FakeOperations();
        var bytes = NewKey(0x42);
        ops.Seed(Path, bytes);
        var store = NewStore(ops);

        var loaded = await store.TryLoadAsync(Path);

        Assert.NotNull(loaded);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task TryLoadAsync_ReturnsNullWhenFileMissing()
    {
        var store = NewStore(new FakeOperations());

        var loaded = await store.TryLoadAsync(Path);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task TryLoadAsync_RejectsSymlinkBeforeReadingBytes()
    {
        var ops = new FakeOperations { ReparsePointAt = Path };
        ops.Seed(Path, NewKey(0x7F));
        var store = NewStore(ops);

        var error = await Assert.ThrowsAsync<SecretStoreAccessDeniedException>(
            () => store.TryLoadAsync(Path));
        Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryLoadAsync_RejectsFileWithOtherReadableOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return;

        var ops = new FakeOperations
        {
            ModeAt = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead,
        };
        ops.Seed(Path, NewKey(0x33));
        var store = NewStore(ops);

        var error = await Assert.ThrowsAsync<SecretStoreAccessDeniedException>(
            () => store.TryLoadAsync(Path));
        Assert.Contains("non-owner", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryLoadAsync_RejectsGroupReadableOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return;

        var ops = new FakeOperations
        {
            ModeAt = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
        };
        ops.Seed(Path, NewKey(0x33));
        var store = NewStore(ops);

        await Assert.ThrowsAsync<SecretStoreAccessDeniedException>(
            () => store.TryLoadAsync(Path));
    }

    [Fact]
    public async Task TryLoadAsync_RaisesSecretStoreKeyException_OnWrongLength()
    {
        var ops = new FakeOperations();
        ops.Seed(Path, new byte[12]);
        var store = NewStore(ops);

        await Assert.ThrowsAsync<SecretStoreKeyException>(
            () => store.TryLoadAsync(Path));
    }

    [Fact]
    public async Task WriteAsync_PersistsProvidedKeyAndAppliesOwnerOnlyMode()
    {
        var ops = new FakeOperations();
        var store = NewStore(ops);

        var bytes = NewKey(0x99);
        await store.WriteAsync(Path, bytes);

        Assert.Equal(bytes, ops.ReadAllBytes(Path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ops.ModeFor(Path));
        }
    }

    [Fact]
    public async Task WriteAsync_RejectsKeyOfWrongLength()
    {
        var store = NewStore(new FakeOperations());

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(Path, [0x01, 0x02, 0x03]));
    }

    [Fact]
    public void ResolvePath_PrefersEnvironmentOverride()
    {
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false)
        {
            [PhysicalSecretKeyFile.PathEnvironmentVariable] = "/custom/path/key",
            ["HOME"] = "/home/ignored",
        };

        var resolved = PhysicalSecretKeyFile.ResolvePath(environment);

        Assert.Equal("/custom/path/key", resolved);
    }

    [Fact]
    public void ResolvePath_FallsBackToHomeWhenNoOverride()
    {
        var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false)
        {
            ["HOME"] = "/home/agent",
        };

        var resolved = PhysicalSecretKeyFile.ResolvePath(environment);

        Assert.Equal("/home/agent/.mohist/slack-master.key", resolved);
    }

    private static PhysicalSecretKeyFile NewStore(ISecretKeyFileOperations ops) =>
        new(ops, new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false));

    private static byte[] NewKey(byte fill) =>
        Enumerable.Range(0, 32).Select(index => (byte)(fill + index)).ToArray();

    private sealed class FakeOperations : ISecretKeyFileOperations
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, UnixFileMode> _modes = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public List<string> Writes { get; } = new();
        public UnixFileMode ForceMode { get; set; } = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        public string? ReparsePointAt { get; set; }
        public UnixFileMode ModeAt { get; set; } = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        public void Seed(string path, byte[] bytes)
        {
            lock (_gate)
            {
                _files[path] = bytes;
                _modes[path] = ModeAt;
            }
        }

        public UnixFileMode ModeFor(string path)
        {
            lock (_gate) return _modes[path];
        }

        public bool Exists(string path)
        {
            lock (_gate) return _files.ContainsKey(path);
        }

        public bool FileExists(string path) => Exists(path);

        public bool IsReparsePoint(string path) => ReparsePointAt == path;

        public UnixFileMode GetUnixFileMode(string path)
        {
            lock (_gate) return _modes.TryGetValue(path, out var mode) ? mode : ForceMode;
        }

        public void SetUnixFileMode(string path, UnixFileMode mode)
        {
            lock (_gate) _modes[path] = mode;
        }

        public void CreateDirectory(string path)
        {
        }

        public Task WriteAllBytesAtomicAsync(
            string path,
            byte[] bytes,
            UnixFileMode ownerOnlyMode,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                _files[path] = bytes;
                _modes[path] = ownerOnlyMode;
            }
            Writes.Add(path);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_files[path]);
            }
        }

        public byte[] ReadAllBytes(string path)
        {
            lock (_gate) return _files[path];
        }
    }
}
