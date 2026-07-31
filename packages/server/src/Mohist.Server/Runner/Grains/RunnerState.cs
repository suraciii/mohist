namespace Mohist.Server.Runner.Grains;

[GenerateSerializer]
public sealed class RunnerState
{
    [Id(0)] public RunnerInfo? LastKnownInfo { get; set; }
    [Id(1)] public string? LastKnownActionCatalogJson { get; set; }
}

[GenerateSerializer]
public sealed class LegacyRunnerRegistrationState
{
    [Id(1)] public RunnerInfo? LastKnownInfo { get; set; }
    [Id(2)] public string? LastKnownActionCatalogJson { get; set; }
}
