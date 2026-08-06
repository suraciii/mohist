using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public sealed partial class AgentConnectionStoreSpecs
{
    [Fact]
    public async Task ConcurrentStagedBindingsPersistExactlyOneCompleteIdentity()
    {
        await SeedAgentAsync("proj-staged-race", "agent-staged-race", AgentStatus.Active);
        var created = await _store.CreateStagedAsync(new AgentConnection
        {
            Id = "conn-staged-race",
            ProjectId = "proj-staged-race",
            AgentId = "agent-staged-race",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "team-staged-race",
        });

        var attempts = await Task.WhenAll(
            AttemptBindingAsync(created.Id, "app-race-a", "bot-race-a"),
            AttemptBindingAsync(created.Id, "app-race-b", "bot-race-b"));

        Assert.Single(attempts, attempt => attempt.Error is null);
        Assert.Single(attempts, attempt => attempt.Error is AgentConnectionValidationException { Code: "immutable_binding" });
        var persisted = await _store.GetAsync("proj-staged-race", created.Id);
        Assert.NotNull(persisted);
        (string AppId, string BotUserId)[] expectedIdentities =
        [
            ("app-race-a", "bot-race-a"),
            ("app-race-b", "bot-race-b"),
        ];
        Assert.Contains((persisted.AppId, persisted.BotUserId), expectedIdentities);
        Assert.False(string.IsNullOrWhiteSpace(persisted.AppId));
        Assert.False(string.IsNullOrWhiteSpace(persisted.BotUserId));
    }

    private async Task<BindingAttempt> AttemptBindingAsync(string connectionId, string appId, string botUserId)
    {
        try
        {
            await _store.BindSlackIdentityAsync(
                "proj-staged-race",
                connectionId,
                "team-staged-race",
                appId,
                botUserId,
                "Mohist");
            return new(null);
        }
        catch (Exception exception)
        {
            return new(exception);
        }
    }

    private sealed record BindingAttempt(Exception? Error);
}
