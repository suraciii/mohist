using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed partial class SlackManagerCorrectnessKernelSpecs
{
    [Fact]
    public async Task OAuth_secret_store_failure_leaves_recovery_attempt_and_retries_without_reconsuming_state()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_SECRET_RETRY");
        var states = new SlackOAuthStateService(_factory, _time);
        var issued = await states.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId);
        var sink = new ControlledSlackOAuthCredentialSink(_factory, child.Id) { FailNextStore = true };
        var authorization = new SlackOAuthAuthorizationService(_factory, states, sink, _time);

        var first = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_SECRET_RETRY", "xoxb-secret-retry");
        Assert.Equal(SlackOAuthAuthorizationStatus.RecoveryRequired, first.Status);
        await using (var failed = _factory.CreateDbContext())
        {
            var attempt = await failed.SlackOAuthAttempts.SingleAsync();
            Assert.Equal(SlackOAuthAttemptStatus.RecoveryRequired, attempt.Status);
            Assert.Null(attempt.BotTokenRef);
        }

        var retry = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_SECRET_RETRY", "xoxb-secret-retry");
        Assert.Equal(SlackOAuthAuthorizationStatus.Accepted, retry.Status);
        Assert.Equal(2, sink.StoreCalls);
        Assert.Single(sink.Tokens);
        await using var recovered = _factory.CreateDbContext();
        var recoveredAttempt = await recovered.SlackOAuthAttempts.SingleAsync();
        Assert.Equal(SlackOAuthAttemptStatus.Applied, recoveredAttempt.Status);
        Assert.StartsWith("slack-oauth-attempt:", recoveredAttempt.BotTokenRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuth_secret_reference_persist_failure_retries_same_external_secret_without_orphaning()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_SECRET_REFERENCE_RETRY");
        var states = new SlackOAuthStateService(_factory, _time);
        var issued = await states.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId);
        var sink = new ControlledSlackOAuthCredentialSink(_factory, child.Id);
        var faultingFactory = new FailNextSecretReferencePersistDbContextFactory(_database.ConnectionString);
        var authorization = new SlackOAuthAuthorizationService(faultingFactory, states, sink, _time);

        var first = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_SECRET_REFERENCE_RETRY", "xoxb-secret-reference-retry");
        Assert.Equal(SlackOAuthAuthorizationStatus.RecoveryRequired, first.Status);
        Assert.Equal(1, sink.ExternalWriteCount);
        await using (var failed = faultingFactory.CreateDbContext())
        {
            var attempt = await failed.SlackOAuthAttempts.SingleAsync();
            Assert.Equal(SlackOAuthAttemptStatus.RecoveryRequired, attempt.Status);
            Assert.Null(attempt.BotTokenRef);
        }

        var retry = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_SECRET_REFERENCE_RETRY", "xoxb-secret-reference-retry");
        Assert.Equal(SlackOAuthAuthorizationStatus.Accepted, retry.Status);
        Assert.Equal(2, sink.StoreCalls);
        Assert.Equal(1, sink.ExternalWriteCount);
        Assert.Single(sink.Tokens);
        await using var recovered = _factory.CreateDbContext();
        var recoveredAttempt = await recovered.SlackOAuthAttempts.SingleAsync();
        Assert.Equal(SlackOAuthAttemptStatus.Applied, recoveredAttempt.Status);
        Assert.Equal("slack-oauth-attempt:" + recoveredAttempt.Id + ":bot-token", recoveredAttempt.BotTokenRef);
    }

    [Theory]
    [InlineData(SlackChildAppBindingState.Conflict, SlackChildAppBindingObligationStatus.Conflict)]
    [InlineData(SlackChildAppBindingState.ConnectionDeleted, SlackChildAppBindingObligationStatus.ConnectionDeleted)]
    public async Task Binding_diagnostic_retry_resets_both_states_and_converges_to_bound(string bindingState, string obligationStatus)
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND_RETRY",
            botUserId: "U_BIND_RETRY",
            bindingState: bindingState);
        await SeedBindingObligationAsync(child, obligationStatus);
        var connections = new AgentConnectionStore(
            _factory,
            new AgentQuerier(_factory),
            new FakeSecretStore(),
            Array.Empty<IAgentConnectionProviderCleanup>(),
            _time);
        var binding = new SlackChildAppBindingService(_factory, connections, _time);

        var result = await binding.ReconcileAsync(child.Id);

        Assert.Equal(SlackChildAppBindingStatus.Bound, result.Status);
        Assert.Equal(SlackChildAppBindingState.Bound, await BindingStateAsync(child.Id));
        await using var db = _factory.CreateDbContext();
        var obligation = await db.SlackChildAppBindingObligations.SingleAsync(item => item.ChildAppId == child.Id);
        Assert.Equal(SlackChildAppBindingObligationStatus.Bound, obligation.Status);
        Assert.Equal(("A_BIND_RETRY", "U_BIND_RETRY"), await ConnectionIdentityAsync(child.AgentConnectionId));
    }

    [Fact]
    public async Task Expired_worker_failure_cannot_overwrite_newer_success()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND_LEASE_FAILURE",
            botUserId: "U_BIND_LEASE_FAILURE");
        var actual = CreateBindingPortStore();
        var port = new InterleavingBindingPort(actual)
        {
            PauseFirstCall = true,
            FailFirstCall = true,
        };
        var first = new SlackChildAppBindingService(_factory, port, _time);
        var second = new SlackChildAppBindingService(_factory, port, _time);

        var staleFailure = first.ReconcileAsync(child.Id);
        await port.FirstCallStarted;
        _time.Advance(TimeSpan.FromMinutes(5));

        var newerSuccess = await second.ReconcileAsync(child.Id);
        Assert.Equal(SlackChildAppBindingStatus.Bound, newerSuccess.Status);

        port.ReleaseFirstCall();
        var staleResult = await staleFailure;
        Assert.Equal(SlackChildAppBindingStatus.Bound, staleResult.Status);
        await AssertBindingConvergedAsync(child, SlackChildAppBindingState.Bound);
    }

    [Fact]
    public async Task Expired_worker_success_cannot_overwrite_newer_failure()
    {
        var child = await SeedChildAsync(
            lifecycle: SlackAppLifecycle.Created,
            authorization: SlackAuthorizationState.Authorized,
            appId: "A_BIND_LEASE_SUCCESS",
            botUserId: "U_BIND_LEASE_SUCCESS");
        var actual = CreateBindingPortStore();
        var port = new InterleavingBindingPort(actual)
        {
            PauseFirstCall = true,
            FailSecondCall = true,
        };
        var staleWorker = new SlackChildAppBindingService(_factory, port, _time);
        var newerWorker = new SlackChildAppBindingService(_factory, port, _time);

        var staleSuccess = staleWorker.ReconcileAsync(child.Id);
        await port.FirstCallStarted;
        _time.Advance(TimeSpan.FromMinutes(5));

        var newerFailure = await newerWorker.ReconcileAsync(child.Id);
        Assert.Equal(SlackChildAppBindingStatus.Conflict, newerFailure.Status);

        port.ReleaseFirstCall();
        var staleResult = await staleSuccess;
        Assert.Equal(SlackChildAppBindingStatus.Conflict, staleResult.Status);
        await AssertBindingConvergedAsync(child, SlackChildAppBindingState.Conflict);
        Assert.Equal((string.Empty, string.Empty), await ConnectionIdentityAsync(child.AgentConnectionId));
    }

    private AgentConnectionStore CreateBindingPortStore() => new(
        _factory,
        new AgentQuerier(_factory),
        new FakeSecretStore(),
        Array.Empty<IAgentConnectionProviderCleanup>(),
        _time);

    private async Task AssertBindingConvergedAsync(ManagedSlackChildAppRow child, string state)
    {
        Assert.Equal(state, await BindingStateAsync(child.Id));
        await using var db = _factory.CreateDbContext();
        Assert.Equal(state, await db.SlackChildAppBindingObligations
            .Where(item => item.ChildAppId == child.Id)
            .Select(item => item.Status)
            .SingleAsync());
    }

    private sealed class InterleavingBindingPort(AgentConnectionStore inner) : ISlackChildAppBindingPort
    {
        private readonly TaskCompletionSource<bool> _firstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public bool PauseFirstCall { get; init; }
        public bool FailFirstCall { get; init; }
        public bool FailSecondCall { get; init; }
        public Task FirstCallStarted => _firstCallStarted.Task;

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult(true);

        public async Task<AgentConnection?> BindSlackIdentityAsync(
            string projectId,
            string id,
            string workspaceTeamId,
            string appId,
            string botUserId,
            string? botName,
            CancellationToken ct = default,
            string? claimToken = null)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _firstCallStarted.TrySetResult(true);
                if (PauseFirstCall)
                    await _releaseFirstCall.Task.WaitAsync(ct);
                if (FailFirstCall)
                    throw new AgentConnectionDuplicateException(projectId, "agent", workspaceTeamId);
            }
            else if (call == 2 && FailSecondCall)
            {
                throw new AgentConnectionDuplicateException(projectId, "agent", workspaceTeamId);
            }

            return await inner.BindSlackIdentityAsync(projectId, id, workspaceTeamId, appId, botUserId, botName, ct, claimToken);
        }
    }

    [Fact]
    public async Task OAuth_apply_failure_keeps_secret_reference_and_replay_resumes_apply_without_orphaning_token()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_APPLY_RETRY");
        var states = new SlackOAuthStateService(_factory, _time);
        var issued = await states.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId);
        var sink = new ControlledSlackOAuthCredentialSink(_factory, child.Id) { MutateChildAppIdBeforeReturn = "A_CHANGED" };
        var authorization = new SlackOAuthAuthorizationService(_factory, states, sink, _time);

        var first = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_APPLY_RETRY", "xoxb-apply-retry");
        Assert.Equal(SlackOAuthAuthorizationStatus.RecoveryRequired, first.Status);
        await using (var failed = _factory.CreateDbContext())
        {
            var attempt = await failed.SlackOAuthAttempts.SingleAsync();
            Assert.Equal(SlackOAuthAttemptStatus.SecretStored, attempt.Status);
            Assert.StartsWith("slack-oauth-attempt:", attempt.BotTokenRef, StringComparison.Ordinal);
            Assert.Equal("A_CHANGED", await failed.ManagedSlackChildApps.Where(item => item.Id == child.Id).Select(item => item.AppId).SingleAsync());
        }

        await using (var restore = _factory.CreateDbContext())
        {
            var row = await restore.ManagedSlackChildApps.SingleAsync(item => item.Id == child.Id);
            row.AppId = "A_APPLY_RETRY";
            await restore.SaveChangesAsync();
        }
        sink.MutateChildAppIdBeforeReturn = null;
        var retry = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, "A_APPLY_RETRY", "U_APPLY_RETRY", "xoxb-apply-retry");
        Assert.Equal(SlackOAuthAuthorizationStatus.Accepted, retry.Status);
        Assert.Equal(1, sink.StoreCalls);
        await using var recovered = _factory.CreateDbContext();
        Assert.Equal(SlackOAuthAttemptStatus.Applied, await recovered.SlackOAuthAttempts.Select(item => item.Status).SingleAsync());
        Assert.Equal(SlackAuthorizationState.Authorized, await recovered.ManagedSlackChildApps.Select(item => item.Authorization).SingleAsync());
    }

    [Fact]
    public void Domain_transitions_and_derivation_reject_unknown_or_illegal_states()
    {
        var child = new ManagedSlackChildApp
        {
            AppLifecycle = SlackAppLifecycle.Created,
            Authorization = SlackAuthorizationState.Authorized,
            TransportKind = SlackTransportKind.Socket,
            BindingState = SlackChildAppBindingState.Bound,
        };
        Assert.Throws<InvalidOperationException>(() => child.TransitionAppLifecycle(SlackAppLifecycle.NotCreated));
        Assert.Throws<ArgumentException>(() => child.SetTransportKind("invalid"));
        child.AppLifecycle = "invalid";
        Assert.Throws<ArgumentException>(() => ManagedSlackChildAppStatusDeriver.Derive(child));

        var enrollment = new SlackWorkspaceEnrollment { Lifecycle = SlackEnrollmentLifecycle.Active };
        Assert.Throws<ArgumentOutOfRangeException>(() => enrollment.UpdatePlan("pro", -1));
    }

    [Fact]
    public async Task Database_constraints_reject_invalid_child_app_lifecycle_and_manifest_pair()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_CHECKS", botUserId: "U_CHECKS");
        await using var invalidLifecycle = _factory.CreateDbContext();
        var row = await invalidLifecycle.ManagedSlackChildApps.SingleAsync(item => item.Id == child.Id);
        row.AppLifecycle = "invalid";
        await Assert.ThrowsAsync<DbUpdateException>(() => invalidLifecycle.SaveChangesAsync());

        await using var invalidManifest = _factory.CreateDbContext();
        var manifestRow = await invalidManifest.ManagedSlackChildApps.SingleAsync(item => item.Id == child.Id);
        manifestRow.AppLifecycle = SlackAppLifecycle.Created;
        manifestRow.DesiredManifestVersion = 0;
        await Assert.ThrowsAsync<DbUpdateException>(() => invalidManifest.SaveChangesAsync());
    }

    [Fact]
    public async Task OAuth_state_without_attempt_reference_is_recovery_required_not_accepted()
    {
        var child = await SeedChildAsync(lifecycle: SlackAppLifecycle.Created, appId: "A_MISSING_ATTEMPT");
        var states = new SlackOAuthStateService(_factory, _time);
        var issued = await states.IssueAsync(child.Id, child.WorkspaceTeamId, child.AppId);
        await using (var db = _factory.CreateDbContext())
        {
            var state = await db.SlackOAuthStates.SingleAsync();
            state.AuthorizationAttemptId = null;
            await db.SaveChangesAsync();
        }
        var authorization = new SlackOAuthAuthorizationService(_factory, states, new FakeSlackOAuthCredentialSink(), _time);
        var result = await authorization.AuthorizeAsync(
            issued.State, child.Id, child.WorkspaceTeamId, child.AppId, "U_MISSING_ATTEMPT", "xoxb-missing-attempt");
        Assert.Equal(SlackOAuthAuthorizationStatus.RecoveryRequired, result.Status);
    }

    private sealed class FailNextSecretReferencePersistDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly string _connectionString;
        private bool _failed;

        public FailNextSecretReferencePersistDbContextFactory(string connectionString) => _connectionString = connectionString;

        public MohistDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_connectionString)
                .AddInterceptors(new FailSecretReferencePersistInterceptor(this))
                .Options;
            return new MohistDbContext(options);
        }

        public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());

        private sealed class FailSecretReferencePersistInterceptor(FailNextSecretReferencePersistDbContextFactory owner) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
                System.Data.Common.DbCommand command,
                CommandEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                if (!owner._failed
                    && command.CommandText.Contains("UPDATE \"SlackOAuthAttempts\"", StringComparison.Ordinal)
                    && command.CommandText.Contains("\"BotTokenRef\"", StringComparison.Ordinal))
                {
                    owner._failed = true;
                    throw new InvalidOperationException("secret_reference_persist_failure");
                }

                return ValueTask.FromResult(result);
            }
        }
    }

    private sealed class ControlledSlackOAuthCredentialSink : ISlackOAuthCredentialSink
    {
        private readonly IDbContextFactory<MohistDbContext> _factory;
        private readonly string _childAppId;
        private readonly Dictionary<string, string> _referencesByAttempt = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tokensByReference = new(StringComparer.Ordinal);

        public ControlledSlackOAuthCredentialSink(IDbContextFactory<MohistDbContext> factory, string childAppId)
        {
            _factory = factory;
            _childAppId = childAppId;
        }

        public bool FailNextStore { get; set; }
        public string? MutateChildAppIdBeforeReturn { get; set; }
        public int StoreCalls { get; private set; }
        public int ExternalWriteCount { get; private set; }
        public IReadOnlyDictionary<string, string> Tokens => _tokensByReference;

        public async Task<string> GetOrStoreBotTokenAsync(
            string childAppId,
            string authorizationAttemptId,
            string botToken,
            CancellationToken ct = default)
        {
            StoreCalls++;
            if (FailNextStore)
            {
                FailNextStore = false;
                throw new InvalidOperationException("secret_store_unavailable");
            }

            if (_referencesByAttempt.TryGetValue(authorizationAttemptId, out var existingReference))
                return existingReference;

            ExternalWriteCount++;
            var reference = $"slack-oauth-attempt:{authorizationAttemptId}:bot-token";
            _referencesByAttempt.Add(authorizationAttemptId, reference);
            _tokensByReference.Add(reference, botToken);
            if (MutateChildAppIdBeforeReturn is not null)
            {
                await using var db = _factory.CreateDbContext();
                var child = await db.ManagedSlackChildApps.SingleAsync(item => item.Id == _childAppId, ct);
                child.AppId = MutateChildAppIdBeforeReturn;
                await db.SaveChangesAsync(ct);
            }
            return reference;
        }
    }

}
