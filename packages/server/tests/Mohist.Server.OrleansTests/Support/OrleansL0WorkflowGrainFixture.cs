using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.OrleansTests.Support;

public sealed class OrleansL0WorkflowGrainFixture : WorkflowGrainFixture
{
    public const string WarmupRunnerId = "orleans-l0-fixture-warmup";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Pay Orleans serializer, first activation, and reset cost during
        // fixture setup so the first business Spec measures its own claim.
        var runner = Grains.GetGrain<IRunnerGrain>(WarmupRunnerId);
        await runner.RegisterAsync(new RunnerInfo(
            WarmupRunnerId,
            ["spec/*"],
            "test-host",
            null));
        _ = await runner.GetInfoAsync();
        await runner.UnregisterAsync();
    }
}
