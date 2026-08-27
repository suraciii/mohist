using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.GitHub.Infrastructure;

/// <summary>
/// In-process wake signal for durable GitHub command reply delivery. A lost
/// signal only adds the worker's safety-poll interval; pending rows remain
/// durable in SQLite and are recovered after a process restart.
/// </summary>
public sealed class GitHubCommandReplyDeliverySignal
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake;
    private const int Capacity = 4;

    public GitHubCommandReplyDeliverySignal()
    {
        _wake = new SemaphoreSlim(0, Capacity);
    }

    public void Wake()
    {
        lock (_gate)
        {
            if (_wake.CurrentCount < Capacity)
                _wake.Release();
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _wake.WaitAsync(timeout, ct);
}

/// <summary>
/// Reconciles and delivers one reserved command reply. The reservation is
/// persisted before the first POST; every retry lists comments by the exact
/// invisible marker so a lost POST response cannot create a duplicate.
/// </summary>
public sealed class GitHubCommandReplyDeliveryService : IScopedService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly GitHubCommandReplyStore _replies;
    private readonly GitHubConnectionStore _connections;
    private readonly IGitHubCommentPort _comments;
    private readonly GitHubCommandReplyDeliverySignal _signal;
    private readonly ILogger<GitHubCommandReplyDeliveryService> _log;

    public GitHubCommandReplyDeliveryService(
        GitHubCommandReplyStore replies,
        GitHubConnectionStore connections,
        IGitHubCommentPort comments,
        GitHubCommandReplyDeliverySignal signal,
        ILogger<GitHubCommandReplyDeliveryService> log)
    {
        _replies = replies;
        _connections = connections;
        _comments = comments;
        _signal = signal;
        _log = log;
    }

    public async Task DeliverAsync(
        GitHubCommandReply reply,
        bool force = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reply);

        GitHubCommandReply? claimed;
        try
        {
            claimed = await _replies.TryClaimAsync(reply.Id, LeaseDuration, force, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not claim GitHub command reply {ReplyId} for delivery", reply.Id);
            throw;
        }
        if (claimed is null)
            return;
        try
        {
            var connection = await _connections.GetByIdAsync(claimed.ConnectionId, ct).ConfigureAwait(false);
            if (connection is null)
                throw new InvalidOperationException($"GitHub connection '{claimed.ConnectionId}' no longer exists");
            if (connection.Status != GitHubConnectionStatus.Active)
                throw new InvalidOperationException($"GitHub connection '{connection.Id}' is disabled");

            var matches = await _comments
                .FindCommentIdsByMarkerAsync(
                    connection,
                    claimed.GithubIssueNumber,
                    claimed.Marker,
                    ct)
                .ConfigureAwait(false);
            if (matches.Count > 1)
            {
                await _replies.RecordFailureAsync(
                    claimed.Id,
                    "reply marker matched multiple GitHub comments; delivery is ambiguous",
                    terminal: true,
                    ct).ConfigureAwait(false);
                _log.LogError(
                    "GitHub command reply {ReplyId} is ambiguous: marker matched {MatchCount} comments",
                    claimed.Id,
                    matches.Count);
                return;
            }

            if (matches.Count == 1)
            {
                await _replies.MarkPostedAsync(claimed.Id, ct).ConfigureAwait(false);
                return;
            }

            await _comments.PostCommentAsync(
                connection,
                claimed.GithubIssueNumber,
                GitHubMirrorMarker.Append(claimed.Body, claimed.Marker),
                ct).ConfigureAwait(false);
            await _replies.MarkPostedAsync(claimed.Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = ex.Message.Trim();
            if (detail.Length == 0)
                detail = ex.GetType().Name;
            await _replies.RecordFailureAsync(claimed.Id, detail, ct: ct).ConfigureAwait(false);
            _signal.Wake();
            _log.LogWarning(
                ex,
                "GitHub command reply {ReplyId} delivery failed; durable retry remains pending",
                claimed.Id);
            throw;
        }
    }

    public async Task<int> ProcessPendingAsync(
        int batchSize = 100,
        CancellationToken ct = default)
    {
        var pending = await _replies.ListPendingAsync(batchSize, ct).ConfigureAwait(false);
        var delivered = 0;
        foreach (var reply in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeliverAsync(reply, ct: ct).ConfigureAwait(false);
                delivered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Pending GitHub command reply {ReplyId} could not be delivered in this pass",
                    reply.Id);
            }
        }
        return delivered;
    }
}

/// <summary>
/// Controls whether the autonomous command reply delivery loop is started.
/// Manual ProcessPendingAsync calls remain available when the loop is disabled.
/// </summary>
public sealed class GitHubCommandReplyDeliveryOptions
{
    public bool HostedWorkerEnabled { get; set; } = true;
}

/// <summary>
/// Hosted retry/reconciliation consumer for command reply reservations.
/// It is independent of webhook redelivery and also sweeps pending rows after
/// process restart.
/// </summary>
public sealed class GitHubCommandReplyDeliveryWorker : BackgroundService
{
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GitHubCommandReplyDeliverySignal _signal;
    private readonly IOptions<GitHubCommandReplyDeliveryOptions> _options;
    private readonly ILogger<GitHubCommandReplyDeliveryWorker> _log;

    public GitHubCommandReplyDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        GitHubCommandReplyDeliverySignal signal,
        IOptions<GitHubCommandReplyDeliveryOptions> options,
        ILogger<GitHubCommandReplyDeliveryWorker> log)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _options = options;
        _log = log;
    }

    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<GitHubCommandReplyDeliveryService>()
                .ProcessPendingAsync(ct: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub command reply delivery pass failed");
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Integration specs invoke ProcessPendingAsync explicitly against a
        // fake clock. Keeping the autonomous loop off in that host prevents
        // it from consuming the same due row while a spec is asserting the
        // deterministic manual pass; production keeps the default enabled.
        if (!_options.Value.HostedWorkerEnabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await _signal.WaitAsync(SafetyPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
