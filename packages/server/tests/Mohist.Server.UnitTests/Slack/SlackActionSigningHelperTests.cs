using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackActionSigningHelperTests
{
    [Fact]
    public async Task Signs_and_verifies_only_the_exact_canonical_value()
    {
        var secrets = new FakeSecretStore();
        var connection = new AgentConnection { ProjectId = "p1", Id = "c1" };
        secrets.Set(new SecretStoreAddress("p1", "c1", SecretKind.BotToken), "xoxb-test"u8.ToArray());
        var helper = new SlackActionSigningHelper(secrets);

        var signature = await helper.TrySignAsync(connection, "v1\nretry\nc1\ns1");

        Assert.NotNull(signature);
        Assert.True(await helper.VerifyAsync(connection, "v1\nretry\nc1\ns1", signature));
        Assert.False(await helper.VerifyAsync(connection, "v1\nretry\nc1\ns2", signature));
    }

    [Fact]
    public async Task Retry_payload_rejects_tampering_and_missing_required_fields()
    {
        var secrets = new FakeSecretStore();
        var connection = new AgentConnection { ProjectId = "p1", Id = "c1" };
        secrets.Set(new SecretStoreAddress("p1", "c1", SecretKind.BotToken), "xoxb-test"u8.ToArray());
        var signing = new SlackActionSigningHelper(secrets);
        var payload = new SlackRetryActionPayload(
            "v1", "retry", "c1", "s1", "t1", "D1", "100.001", "100.000",
            "U1", "U1", "nonce", DateTimeOffset.Parse("2026-01-01T00:05:00Z"), null);
        var signature = await signing.TrySignAsync(connection, SlackRetryActionService.Canonical(payload));
        var service = new SlackRetryActionService(signing, null!, new FakeTimeProvider());
        var signed = Mohist.Server.Infrastructure.JSON.Serialize(payload with { Signature = signature });

        Assert.NotNull(await service.VerifyAsync(connection, signed));
        Assert.Null(await service.VerifyAsync(connection, signed.Replace("100.001", "100.002", StringComparison.Ordinal)));
        Assert.Null(await service.VerifyAsync(connection, Mohist.Server.Infrastructure.JSON.Serialize(payload with { Signature = null })));
        Assert.Null(await service.VerifyAsync(connection, Mohist.Server.Infrastructure.JSON.Serialize(payload with { Nonce = "", Signature = signature })));
        Assert.Null(await service.VerifyAsync(connection, Mohist.Server.Infrastructure.JSON.Serialize(payload with { Version = "", Signature = signature })));
        Assert.Null(await service.VerifyAsync(connection, Mohist.Server.Infrastructure.JSON.Serialize(payload with { Action = "", Signature = signature })));
    }

    [Fact]
    public async Task Missing_signing_material_suppresses_signatures()
    {
        var helper = new SlackActionSigningHelper(new FakeSecretStore());
        var connection = new AgentConnection { ProjectId = "p1", Id = "c1" };

        Assert.Null(await helper.TrySignAsync(connection, "canonical"));
        Assert.False(await helper.VerifyAsync(connection, "canonical", "signature"));
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _secrets = [];

        public void Set(SecretStoreAddress address, byte[] value) => _secrets[address] = value;
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _secrets[address] = plaintext;
            return Task.CompletedTask;
        }
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.TryGetValue(address, out var value) ? value : null);
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_secrets.Remove(address));
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
