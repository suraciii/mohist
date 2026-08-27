using System.Text.Json;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Runner.Grains;

[GenerateSerializer]
public sealed record RuntimeTaskInput(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string? Uses = null,
    [property: Id(3)] JsonElement? With = null,
    [property: Id(4)] string? Stage = null,
    [property: Id(5)] bool InvalidateChecks = false,
    [property: Id(6)] RecoveryDefinition? Recovery = null,
    [property: Id(7)] TaskArtifactCapture? Artifacts = null,
    [property: Id(8)] Dictionary<string, string>? SetVars = null,
    [property: Id(9)] int? RecoveryRemaining = null,
    [property: Id(10)] JsonElement? Expect = null);
