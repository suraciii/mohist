using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack.Ports;
using Mohist.Server.Infrastructure.Workspace;
using Microsoft.AspNetCore.TestHost;
using Orleans;
using System.Diagnostics;

namespace Mohist.Server.L1Tests.Support;

public sealed class DefaultApplicationHostOwner : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<Task<MohistIntegrationFixture>> _start;
    private readonly Func<MohistIntegrationFixture, ValueTask> _dispose;
    private Task<MohistIntegrationFixture>? _startup;
    private Task? _disposal;
    private OwnerState _state = OwnerState.Cold;

    public DefaultApplicationHostOwner()
        : this(StartDefaultHostAsync, fixture => fixture.DisposeAsync())
    {
    }

    internal DefaultApplicationHostOwner(
        Func<Task<MohistIntegrationFixture>> start,
        Func<MohistIntegrationFixture, ValueTask>? dispose = null)
    {
        _start = start;
        _dispose = dispose ?? (fixture => fixture.DisposeAsync());
    }

    internal Task<MohistIntegrationFixture> GetAsync()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case OwnerState.Cold:
                    _state = OwnerState.Demanded;
                    return _startup = StartOnceAsync();
                case OwnerState.Demanded:
                    return _startup!;
                case OwnerState.Disposed:
                    throw new ObjectDisposedException(nameof(DefaultApplicationHostOwner));
                default:
                    throw new UnreachableException();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_state == OwnerState.Disposed)
                return new ValueTask(_disposal!);

            _state = OwnerState.Disposed;
            _disposal = _startup is null
                ? Task.CompletedTask
                : DisposeStartedHostAsync(_startup);
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeStartedHostAsync(Task<MohistIntegrationFixture> startup)
    {
        MohistIntegrationFixture fixture;
        try
        {
            fixture = await startup;
        }
        catch
        {
            return;
        }

        await _dispose(fixture);
    }

    private async Task<MohistIntegrationFixture> StartOnceAsync() => await _start();

    private static Task<MohistIntegrationFixture> StartDefaultHostAsync() =>
        StartAsync(() => new MohistIntegrationFixture());

    internal static async Task<MohistIntegrationFixture> StartAsync(
        Func<MohistIntegrationFixture> createFixture)
    {
        var fixture = createFixture();
        try
        {
            await fixture.InitializeAsync();
            return fixture;
        }
        catch (Exception startupException)
        {
            try
            {
                await fixture.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "The default application host failed to start and clean up.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
    }

    private enum OwnerState
    {
        Cold,
        Demanded,
        Disposed,
    }
}

public sealed class DefaultMohistIntegrationFixture(DefaultApplicationHostOwner owner)
    : MohistIntegrationFixture
{
    private MohistIntegrationFixture? _fixture;

    private MohistIntegrationFixture Fixture =>
        _fixture ?? throw new InvalidOperationException("The default application host has not been initialized.");

    public override IGrainFactory Grains => Fixture.Grains;
    public override HttpClient Client
    {
        get => Fixture.Client;
        protected set => throw new NotSupportedException();
    }
    public override IServiceProvider Services => Fixture.Services;
    public override FakeRunnerWorkspaceClient RunnerWorkspace => Fixture.RunnerWorkspace;
    public override AgentJobDispatchProbe AgentJobDispatches => Fixture.AgentJobDispatches;
    public override AgentLaunchParticipantProbe LaunchFaults => Fixture.LaunchFaults;
    public override ReportPersistenceFailureProbe ReportPersistenceFailures => Fixture.ReportPersistenceFailures;
    public override AgentSessionPersistenceTestProbe Persistence => Fixture.Persistence;
    public override FakeTimeProvider TimeProvider => Fixture.TimeProvider;
    public override string ConnectionString
    {
        get => Fixture.ConnectionString;
        protected set => throw new NotSupportedException();
    }
    public override string RunnerRoot => Fixture.RunnerRoot;

    public override HttpClient CreateClient() => Fixture.CreateClient();
    public override WebSocketClient CreateWebSocketClient() => Fixture.CreateWebSocketClient();

    public override async ValueTask InitializeAsync() => _fixture = await owner.GetAsync();

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
