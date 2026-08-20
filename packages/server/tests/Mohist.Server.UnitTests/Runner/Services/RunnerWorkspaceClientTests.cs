using System.Reflection;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Services;

public sealed class RunnerWorkspaceClientTests
{
    [Fact]
    public async Task DisconnectedAssignmentFallsBackToConnectedEligibleRunner()
    {
        var transport = new RecordingTransport("runner-3");
        var client = CreateClient("runner-1", [Runner("runner-1"), Runner("runner-2"), Runner("runner-3")], transport);

        var result = await client.GetDiffAsync(
            "project-1", "run-1", 1, new("repo", "https://example.test/repo.git", "main"), new("/workspace"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("runner-3", transport.RequestedRunnerId);
    }

    [Fact]
    public async Task ConnectedAssignmentRoutesToItsExactRunner()
    {
        var transport = new RecordingTransport("runner-1", "runner-2");
        var client = CreateClient("runner-1", [Runner("runner-2")], transport);

        var result = await client.GetDiffAsync(
            "project-1", "run-1", 1, new("repo", "https://example.test/repo.git", "main"), new("/workspace"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("runner-1", transport.RequestedRunnerId);
    }

    private static RunnerWorkspaceClient CreateClient(
        string? assignedRunnerId,
        IReadOnlyList<RunnerInfo> eligible,
        IRunnerControlTransport transport)
    {
        var workflow = DispatchProxy.Create<IWorkflowGrain, WorkflowProxy>();
        ((WorkflowProxy)(object)workflow).AssignedRunnerId = assignedRunnerId;
        var registry = DispatchProxy.Create<IRunnerRegistryGrain, RegistryProxy>();
        ((RegistryProxy)(object)registry).Eligible = eligible;
        var grains = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grains).Workflow = workflow;
        ((GrainFactoryProxy)(object)grains).Registry = registry;
        return new(transport, grains);
    }

    private static RunnerInfo Runner(string runnerId) => new(runnerId, [], runnerId, null);

    private sealed class RecordingTransport(params string[] connectedRunnerIds) : IRunnerControlTransport
    {
        private readonly HashSet<string> _connected = new(connectedRunnerIds, StringComparer.Ordinal);

        public string? RequestedRunnerId { get; private set; }
        public bool IsConnected(string runnerId) => _connected.Contains(runnerId);

        public Task<TResult> SendRequestAsync<TParams, TResult>(
            string runnerId,
            string method,
            TParams parameters,
            Action? requestEnqueued = null,
            CancellationToken ct = default)
        {
            RequestedRunnerId = runnerId;
            return Task.FromResult((TResult)(object)new RunnerWorkspaceDiffResult(
                "base", "head", "merge-base", 1, 0, 1, 1, 0, []));
        }

        public Task SendNotificationAsync<TParams>(
            string runnerId,
            string method,
            TParams parameters,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private class WorkflowProxy : DispatchProxy
    {
        public string? AssignedRunnerId { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IWorkflowGrain.GetAssignedWorkerIdAsync)
                ? Task.FromResult(AssignedRunnerId)
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private class RegistryProxy : DispatchProxy
    {
        public IReadOnlyList<RunnerInfo> Eligible { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IRunnerRegistryGrain.ListEligibleRunnersAsync)
                ? Task.FromResult(Eligible)
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public IWorkflowGrain Workflow { get; set; } = null!;
        public IRunnerRegistryGrain Registry { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IGrainFactory.GetGrain) || !targetMethod.IsGenericMethod)
                throw new NotSupportedException(targetMethod?.Name);
            var grainType = targetMethod.GetGenericArguments()[0];
            if (grainType == typeof(IWorkflowGrain)) return Workflow;
            if (grainType == typeof(IRunnerRegistryGrain)) return Registry;
            throw new NotSupportedException(grainType.Name);
        }
    }
}
