using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

[Collection("IntegrationRunner")]
public class RunnerActionCatalogApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerActionCatalogApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Register_BindsTypedActionCatalogAndRetainsWireOrdering()
    {
        var runnerId = $"runner-catalog-api-{Guid.NewGuid():N}";
        var hostname = $"catalog-host-{Guid.NewGuid():N}";
        var catalog = new ActionCatalog(
            [
                new ActionCatalogEntry(
                    "alpha/catalog",
                    [
                        new ActionCatalogInput("prompt", ["string", "object"], true, Description: "Prompt value"),
                        new ActionCatalogInput(
                            "timeout",
                            ["number"],
                            false,
                            JsonSerializer.SerializeToElement(30),
                            "Timeout in milliseconds"),
                    ],
                    [new ActionCatalogOutput("public", "Public result")],
                    [new ActionCatalogError("action-failed", "Action failed")],
                    "Catalog test Action",
                    ["agent-turn"]),
                new ActionCatalogEntry("zeta/catalog", [], [], []),
            ],
            [new ActionCatalogTombstone(
                "mohist/acp-agent",
                "Use mohist/opencode and rerun the affected stage.")]);

        try
        {
            await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = new[] { "spec/*" },
                hostname,
                actionCatalog = catalog,
            });

            var info = await _fixture.Grains.GetGrain<IRunnerGrain>(runnerId).GetInfoAsync();
            Assert.NotNull(info);
            var received = info!.ActionCatalog;
            Assert.NotNull(received);
            var receivedCatalog = received!;
            Assert.Equal(["alpha/catalog", "zeta/catalog"], receivedCatalog.Actions.Select(action => action.Name));
            Assert.Equal(["mohist/acp-agent"], receivedCatalog.Tombstones.Select(tombstone => tombstone.Name));
            Assert.Equal(["string", "object"], receivedCatalog.Actions[0].Inputs[0].Types);
            Assert.True(receivedCatalog.Actions[0].Inputs[0].Required);
            Assert.Equal(30, receivedCatalog.Actions[0].Inputs[1].Default?.GetInt32());
            Assert.Equal("public", receivedCatalog.Actions[0].Outputs[0].Name);
            Assert.Equal("action-failed", receivedCatalog.Actions[0].Errors[0].Code);
            Assert.NotNull(receivedCatalog.Actions[0].Capabilities);
            Assert.Equal(["agent-turn"], receivedCatalog.Actions[0].Capabilities!);
            Assert.Equal("Use mohist/opencode and rerun the affected stage.", receivedCatalog.Tombstones[0].Guidance);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}
