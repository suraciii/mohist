namespace Mohist.Server.Variables.Grains;

public interface IVariableScopeGrain : IGrainWithStringKey
{
    Task SetContextAsync(string name, string json);
    Task<string> SnapshotAsync(VariableSnapshotRequest request);
    Task ClearAsync();
}

[GenerateSerializer]
public sealed record VariableScopeState(
    [property: Id(0)] Dictionary<string, string> Contexts);

[GenerateSerializer]
public sealed record VariableSnapshotRequest(
    string WorkflowRunId,
    string WorkId,
    string WorkType,
    string? Stage = null,
    string? Title = null);
