using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Runner.Services;

[Trait("level", "L0")]
public sealed class RunnerActivityProbeCoordinatorTests
{
    [Fact]
    public async Task DurableSessionIsProbedAndMatchingEvidenceIsApplied()
    {
        var probe = Probe("observation-1");
        using var fixture = CreateFixture(probe);

        await fixture.Coordinator.ProbeAsync(
            probe.RunnerId,
            "process-1",
            _ => Task.FromResult(true),
            (request, _) => Task.FromResult(new RunnerSessionActivityProbeResult(
                request,
                RunnerSessionActivityObservations.Executing)),
            TestContext.Current.CancellationToken);

        Assert.Equal(probe, fixture.Session.Applied?.Probe);
        Assert.Equal(RunnerSessionActivityObservations.Executing, fixture.Session.Applied?.Observation);
        Assert.Equal([probe.SessionId], fixture.Store.EnumeratedSessionIds);
    }

    [Fact]
    public async Task ConnectionSwapAfterReplyDiscardsEvidence()
    {
        var probe = Probe("observation-stale");
        using var fixture = CreateFixture(probe);
        var checks = 0;

        await fixture.Coordinator.ProbeAsync(
            probe.RunnerId,
            "process-1",
            _ => Task.FromResult(Interlocked.Increment(ref checks) == 1),
            (request, _) => Task.FromResult(new RunnerSessionActivityProbeResult(
                request,
                RunnerSessionActivityObservations.Idle)),
            TestContext.Current.CancellationToken);

        Assert.Null(fixture.Session.Applied);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task MalformedEchoAndTransportFailureNeverApplyEvidence()
    {
        var probe = Probe("observation-malformed");
        using var fixture = CreateFixture(probe);

        await fixture.Coordinator.ProbeAsync(
            probe.RunnerId,
            "process-1",
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult(new RunnerSessionActivityProbeResult(
                probe with { RuntimeSessionId = "different" },
                RunnerSessionActivityObservations.UnknownToRunner)),
            TestContext.Current.CancellationToken);
        Assert.Null(fixture.Session.Applied);

        await fixture.Coordinator.ProbeAsync(
            probe.RunnerId,
            "process-1",
            _ => Task.FromResult(true),
            (_, _) => throw new IOException("transport failed"),
            TestContext.Current.CancellationToken);
        Assert.Null(fixture.Session.Applied);
    }

    private static Fixture CreateFixture(RunnerSessionActivityProbeRequest probe)
    {
        var store = new SessionIdStore(probe.SessionId);
        var session = DispatchProxy.Create<IAgentSessionGrain, SessionProxy>();
        ((SessionProxy)(object)session).Prepared = probe;
        var grains = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
        ((GrainFactoryProxy)(object)grains).Session = session;
        var services = new ServiceCollection()
            .AddSingleton<IAgentSessionStore>(store)
            .BuildServiceProvider();
        var coordinator = new RunnerActivityProbeCoordinator(
            services.GetRequiredService<IServiceScopeFactory>(),
            grains,
            NullLogger<RunnerActivityProbeCoordinator>.Instance);
        return new Fixture(coordinator, store, (SessionProxy)(object)session, services);
    }

    private static RunnerSessionActivityProbeRequest Probe(string observationId) => new(
        "session-durable",
        observationId,
        "runner-1",
        "pi",
        "runtime-session-1",
        "/workspace",
        3,
        2);

    private sealed record Fixture(
        RunnerActivityProbeCoordinator Coordinator,
        SessionIdStore Store,
        SessionProxy Session,
        ServiceProvider Services) : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }

    private sealed class SessionIdStore(string sessionId) : IAgentSessionStore
    {
        public IReadOnlyList<string> EnumeratedSessionIds { get; private set; } = [];

        public Task<IReadOnlyList<string>> ListSessionIdsByRunnerAsync(
            string runnerId,
            CancellationToken ct = default)
        {
            EnumeratedSessionIds = [sessionId];
            return Task.FromResult(EnumeratedSessionIds);
        }

        public Task<AgentSession?> LoadAsync(string key) => Task.FromResult<AgentSession?>(null);
        public Task<IReadOnlyList<AgentSession>> ListAsync() => Task.FromResult<IReadOnlyList<AgentSession>>([]);
        public Task SaveAsync(string key, AgentSession state) => Task.CompletedTask;
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(string runnerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentSessionReconcileBinding>>([]);
        public Task SaveAsync(string key, AgentSession state, IReadOnlyList<AgentSessionEvent> events, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private class SessionProxy : DispatchProxy
    {
        public RunnerSessionActivityProbeRequest? Prepared { get; set; }
        public RunnerSessionActivityProbeResult? Applied { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IAgentSessionGrain.PrepareActivityProbeAsync) => Task.FromResult(Prepared),
                nameof(IAgentSessionGrain.ApplyActivityProbeAsync) => Apply((RunnerSessionActivityProbeResult)args![0]!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private Task<bool> Apply(RunnerSessionActivityProbeResult result)
        {
            Applied = result;
            return Task.FromResult(true);
        }
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public IAgentSessionGrain Session { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IAgentSessionGrain))
                return Session;
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
