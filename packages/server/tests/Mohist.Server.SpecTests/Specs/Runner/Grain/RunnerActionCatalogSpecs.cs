using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerActionCatalogSpecs : WorkflowGrainSpecs
{
    public RunnerActionCatalogSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Register_RetainsCatalogOnRunnerInfoAndRegistry()
    {
        var runnerId = $"runner-catalog-register-{Guid.NewGuid():N}";
        var catalog = CreateCatalog("register");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            ActionCatalog: catalog));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        AssertCatalog(catalog, info!.ActionCatalog);

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var registered = Assert.Single(await registry.ListRunnersAsync(), item => item.RunnerId == runnerId);
        AssertCatalog(catalog, registered.ActionCatalog);
    }

    [Fact]
    public async Task HeartbeatRepair_ReplacesCatalogOnRunnerInfoAndRegistry()
    {
        var runnerId = $"runner-catalog-heartbeat-{Guid.NewGuid():N}";
        var initial = CreateCatalog("initial");
        var repaired = CreateCatalog("repaired");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            ActionCatalog: initial));
        await runner.HeartbeatRepairAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            ActionCatalog: repaired));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        AssertCatalog(repaired, info!.ActionCatalog);

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var registered = Assert.Single(await registry.ListRunnersAsync(), item => item.RunnerId == runnerId);
        AssertCatalog(repaired, registered.ActionCatalog);
    }

    [Fact]
    public async Task GrainReactivation_RestoresPersistedCatalog()
    {
        var runnerId = $"runner-catalog-reactivation-{Guid.NewGuid():N}";
        var catalog = CreateCatalog("reactivation");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            ActionCatalog: catalog));
        await runner.DeactivateForTestAsync();
        await Grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        var reactivated = Grains.GetGrain<IRunnerGrain>(runnerId);
        var info = await reactivated.GetInfoAsync();
        Assert.NotNull(info);
        AssertCatalog(catalog, info!.ActionCatalog);
    }

    [Fact]
    public async Task MissingCatalog_RemainsReadableUntilHeartbeatReportsOne()
    {
        var runnerId = $"runner-catalog-missing-{Guid.NewGuid():N}";
        var catalog = CreateCatalog("reported");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));
        var beforeReport = await runner.GetInfoAsync();
        Assert.NotNull(beforeReport);
        Assert.Null(beforeReport!.ActionCatalog);

        await runner.HeartbeatRepairAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            ActionCatalog: catalog));

        var afterReport = await runner.GetInfoAsync();
        Assert.NotNull(afterReport);
        AssertCatalog(catalog, afterReport!.ActionCatalog);
    }

    [Fact]
    public async Task MissingCatalogOnHeartbeatRepair_ReplacesPreviouslyReportedCatalog()
    {
        var runnerId = $"runner-catalog-cleared-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            ActionCatalog: CreateCatalog("reported")));
        await runner.HeartbeatRepairAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.ActionCatalog);

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var registered = Assert.Single(await registry.ListRunnersAsync(), item => item.RunnerId == runnerId);
        Assert.Null(registered.ActionCatalog);
    }

    [Fact]
    public async Task MissingCatalogOnReregistration_ReplacesPreviouslyReportedCatalog()
    {
        var runnerId = $"runner-catalog-reregistered-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            null,
            ActionCatalog: CreateCatalog("reported")));
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Null(info!.ActionCatalog);

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var registered = Assert.Single(await registry.ListRunnersAsync(), item => item.RunnerId == runnerId);
        Assert.Null(registered.ActionCatalog);
    }

    private static ActionCatalog CreateCatalog(string suffix)
    {
        return new ActionCatalog(
            [
                new ActionCatalogEntry(
                    $"alpha/{suffix}",
                    [
                        new ActionCatalogInput(
                            "prompt",
                            ["string", "object"],
                            true,
                            Description: "Prompt value"),
                        new ActionCatalogInput(
                            "timeout",
                            ["number"],
                            false,
                            JsonSerializer.SerializeToElement(30),
                            "Timeout in milliseconds"),
                    ],
                    [new ActionCatalogOutput("public", "Public result")],
                    [new ActionCatalogError("action-failed", "Action failed")],
                    "Catalog test Action"),
                new ActionCatalogEntry(
                    $"zeta/{suffix}",
                    [],
                    [],
                    [])
            ],
            [new ActionCatalogTombstone(
                "mohist/acp-agent",
                "Use mohist/opencode and rerun the affected stage.")]);
    }

    private static void AssertCatalog(ActionCatalog expected, ActionCatalog? actual)
    {
        Assert.NotNull(actual);
        var catalog = actual!;
        Assert.Equal(expected.Actions.Select(action => action.Name), catalog.Actions.Select(action => action.Name));
        Assert.Equal(expected.Tombstones.Select(tombstone => tombstone.Name), catalog.Tombstones.Select(tombstone => tombstone.Name));

        var expectedAction = expected.Actions[0];
        var actualAction = catalog.Actions[0];
        Assert.Equal(expectedAction.Description, actualAction.Description);
        Assert.Equal(expectedAction.Inputs.Select(input => input.Name), actualAction.Inputs.Select(input => input.Name));
        Assert.Equal(["string", "object"], actualAction.Inputs[0].Types);
        Assert.True(actualAction.Inputs[0].Required);
        Assert.Equal(JsonValueKind.Undefined, actualAction.Inputs[0].Default?.ValueKind ?? JsonValueKind.Undefined);
        Assert.Equal(["number"], actualAction.Inputs[1].Types);
        Assert.False(actualAction.Inputs[1].Required);
        Assert.Equal(JsonValueKind.Number, actualAction.Inputs[1].Default?.ValueKind);
        Assert.Equal(30, actualAction.Inputs[1].Default?.GetInt32());
        Assert.Equal(expectedAction.Outputs.Select(output => output.Name), actualAction.Outputs.Select(output => output.Name));
        Assert.Equal(expectedAction.Errors.Select(error => error.Code), actualAction.Errors.Select(error => error.Code));
        Assert.Equal(expected.Tombstones[0].Guidance, catalog.Tombstones[0].Guidance);
    }
}
