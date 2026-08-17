using System.Text;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.PublicApi;
using Xunit;

namespace Mohist.Server.UnitTests.DirectApi;

public sealed class PublicSessionEventCursorCodecTests
{
    [Fact]
    public async Task CursorRoundTripsAndBindsProjectSessionGenerationAndPosition()
    {
        var secrets = new InMemorySecretStore();
        var codec = new PublicSessionEventCursorCodec(secrets);
        var signer = await codec.OpenAsync();
        var payload = new PublicSessionEventCursorPayload(
            "project-1",
            "session-1",
            3,
            18,
            PublicSessionEventCursorCodec.CurrentVersion);

        var token = signer.Encode(payload);

        Assert.True(signer.TryDecode(token, "project-1", "session-1", out var decoded));
        Assert.Equal(payload, decoded);
        Assert.False(signer.TryDecode(token, "project-2", "session-1", out _));
        Assert.False(signer.TryDecode(token, "project-1", "session-2", out _));
    }

    [Fact]
    public async Task TamperingAndMalformedTokensAreRejected()
    {
        var secrets = new InMemorySecretStore();
        var signer = await new PublicSessionEventCursorCodec(secrets).OpenAsync();
        var token = signer.Encode(new PublicSessionEventCursorPayload(
            "project-1",
            "session-1",
            1,
            1,
            PublicSessionEventCursorCodec.CurrentVersion));
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        Assert.False(signer.TryDecode(tampered, "project-1", "session-1", out _));
        Assert.False(signer.TryDecode("not-a-cursor", "project-1", "session-1", out _));
        Assert.False(signer.TryDecode("", "project-1", "session-1", out _));
    }

    [Fact]
    public async Task PersistedKeyIsCreatedOnceAndRotationInvalidatesExistingCursor()
    {
        var secrets = new InMemorySecretStore();
        var codec = new PublicSessionEventCursorCodec(secrets);
        var signer = await codec.OpenAsync();
        var token = signer.Encode(new PublicSessionEventCursorPayload(
            "project-1",
            "session-1",
            1,
            0,
            PublicSessionEventCursorCodec.CurrentVersion));
        var originalKey = secrets.Value;

        _ = await codec.OpenAsync();
        Assert.Equal(1, secrets.StoreCount);
        Assert.NotNull(originalKey);

        secrets.Value = Encoding.UTF8.GetBytes("01234567890123456789012345678901");
        Assert.False((await codec.OpenAsync()).TryDecode(token, "project-1", "session-1", out _));
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        public byte[]? Value { get; set; }
        public int StoreCount { get; private set; }

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            Value = plaintext.ToArray();
            StoreCount++;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(Value?.ToArray());

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(Value is not null);

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
