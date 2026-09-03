using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Xunit;

namespace Mohist.Server.Tests.Agent.Services;

public partial class AgentGrainSpecs
{
    [Fact]
    public async Task Update_WithExplicitNullOptionalFields_ClearsThoseFieldsWithoutChangingIdentity()
    {
        await using var database = CreateModelSchemaDatabase();
        await using var context = database.CreateDbContext();
        var factory = database.Factory;
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        var grain = CreateGrain(factory, timeProvider, "project_optional", "agent_optional");

        var created = await grain.CreateAsync(new AgentCreateData(
            "project_optional",
            "optional-agent",
            "description",
            "instructions",
            JsonDocument.Parse("{\"model\":\"provider/model\"}").RootElement.Clone(),
            ["coding"],
            2,
            Avatar: "avatar",
            Purpose: "purpose",
            Permissions: ["repo:read"]));

        var updated = await grain.UpdateAsync(new AgentUpdateData(
            Name: null,
            Description: null,
            Instructions: null,
            AgentConfig: null,
            Skills: null,
            MaxConcurrentRuns: null,
            Fields: new HashSet<string>
            {
                nameof(AgentUpdateData.Description),
                nameof(AgentUpdateData.Purpose),
                nameof(AgentUpdateData.AgentConfig),
                nameof(AgentUpdateData.Skills),
                nameof(AgentUpdateData.MaxConcurrentRuns),
                nameof(AgentUpdateData.Avatar),
                nameof(AgentUpdateData.Permissions),
            },
            Avatar: null,
            Purpose: null,
            Permissions: null));

        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated!.Id);
        Assert.Equal(created.Name, updated.Name);
        Assert.Equal(string.Empty, updated.Description);
        Assert.Null(updated.Purpose);
        Assert.Null(updated.AgentConfig);
        Assert.Empty(updated.Skills);
        Assert.Null(updated.MaxConcurrentRuns);
        Assert.Null(updated.Avatar);
        Assert.Empty(updated.Permissions!);
    }
}
