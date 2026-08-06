using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Security.Secrets;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Services;

public sealed partial class AgentConnectionStoreSpecs
{
    [Fact]
    public async Task DeleteDoesNotRemoveManagedAgentAppCredentials()
    {
        await SeedAgentAsync("proj-app-owner", "agent-app-owner", AgentStatus.Active);
        var connection = await _store.CreateStagedAsync(NewConnection("proj-app-owner", "agent-app-owner", "team-app-owner"));
        var appToken = SecretStoreAddress.ForManagedSlackAgentApp("app-owner-1", SecretKind.AppToken);
        var botToken = SecretStoreAddress.ForManagedSlackAgentApp("app-owner-1", SecretKind.BotToken);
        await _secretStore.StoreAsync(appToken, [1]);
        await _secretStore.StoreAsync(botToken, [2]);

        await _store.DeleteAsync(connection.ProjectId, connection.Id);

        Assert.Equal([1], await _secretStore.LoadAsync(appToken));
        Assert.Equal([2], await _secretStore.LoadAsync(botToken));
    }
}
