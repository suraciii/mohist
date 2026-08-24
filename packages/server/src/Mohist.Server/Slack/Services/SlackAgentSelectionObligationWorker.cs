using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Recovers the durable selection decision after a process interruption and
/// bounds the lifetime of the chooser claim. The claim row is the authority:
/// recovery uses only its selected Project, dispatch kind, thread anchor, and
/// pre-allocated ids, never the Project or lease from the original click.
/// </summary>
public sealed class SlackAgentSelectionObligationWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ActionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private const string NonInteractiveExpiryReason = "non_interactive_expired";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<SlackProviderOptions> _options;
    private readonly ILogger<SlackAgentSelectionObligationWorker> _logger;

    public SlackAgentSelectionObligationWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<SlackProviderOptions> options,
        ILogger<SlackAgentSelectionObligationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Runs one deterministic recovery/expiry/retention pass. Tests call this
    /// method directly so they exercise the same pass as the hosted loop.
    /// </summary>
    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var prompts = scope.ServiceProvider.GetRequiredService<SlackAmbiguousPromptStore>();
            var selections = scope.ServiceProvider.GetRequiredService<SlackAgentSelectionService>();
            var outbox = scope.ServiceProvider.GetRequiredService<SlackOutboxStore>();
            var now = _timeProvider.GetUtcNow();

            foreach (var projectId in await prompts.ListProjectIdsAsync(cancellationToken))
            {
                await ExpirePendingAsync(projectId, prompts, outbox, now, cancellationToken);
                await RecoverDecidedAsync(projectId, prompts, selections, outbox, now, cancellationToken);
            }

            await EnsureRecentSettlementOutcomesAsync(prompts, outbox, now, cancellationToken);
            await prompts.DeleteFinishedBeforeAsync(
                now - _options.Value.SlackEventRetentionWindow,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process Slack Agent selection obligations");
        }
    }

    private async Task ExpirePendingAsync(
        string projectId,
        SlackAmbiguousPromptStore prompts,
        SlackOutboxStore outbox,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pending = (await prompts.ListByStateAsync(
            projectId,
            SlackSelectionStates.Pending,
            now,
            ct))
            .Where(claim => claim.PromptedAt <= now - ActionLifetime)
            .ToArray();
        foreach (var claim in pending)
        {
            try
            {
                var offeredInteractiveSelection = await OfferedInteractiveSelectionAsync(
                    claim,
                    outbox,
                    ct);
                var reason = offeredInteractiveSelection
                    ? "expired"
                    : NonInteractiveExpiryReason;
                if (await prompts.TrySettleAsync(
                        claim.Id,
                        SlackSelectionStates.Pending,
                        reason,
                        ct)
                    && offeredInteractiveSelection)
                {
                    await EnsureSettlementOutcomeAsync(claim, outbox, reason, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to expire Slack Agent selection {SelectionId}",
                    claim.Id);
            }
        }
    }

    private async Task RecoverDecidedAsync(
        string projectId,
        SlackAmbiguousPromptStore prompts,
        SlackAgentSelectionService selections,
        SlackOutboxStore outbox,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var decided = await prompts.ListByStateAsync(
            projectId,
            SlackSelectionStates.Decided,
            now - RetryInterval,
            ct);
        foreach (var claim in decided)
        {
            try
            {
                if (!await prompts.TryBeginDispatchAsync(claim.Id, now, RetryInterval, ct))
                    continue;

                var result = await selections.RecoverAsync(claim, ct);
                if (result.Completed)
                {
                    await prompts.MarkCompletedAsync(claim.Id, result.State, ct);
                    continue;
                }

                if (result.Settled)
                {
                    if (await prompts.TrySettleAsync(
                            claim.Id,
                            SlackSelectionStates.Decided,
                            result.Reason ?? "selection_unrecoverable",
                            ct))
                    {
                        await EnsureSettlementOutcomeAsync(
                            claim,
                            outbox,
                            result.Reason ?? "selection_unrecoverable",
                            ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The attempt was recorded before dispatch. An exception is
                // not evidence that the committed lineage is irrecoverable:
                // database, Orleans, adapter, and provider failures remain
                // retryable regardless of how many times they occur. Only an
                // explicit terminal result from RecoverAsync may settle the
                // durable winner.
                _logger.LogWarning(
                    ex,
                    "Failed to resume Slack Agent selection {SelectionId} (attempt {AttemptCount}); keeping it Decided for retry",
                    claim.Id,
                    claim.AttemptCount + 1);
            }
        }
    }

    private async Task EnsureRecentSettlementOutcomesAsync(
        SlackAmbiguousPromptStore prompts,
        SlackOutboxStore outbox,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // A settlement and its visible outbox row are separate durable stores.
        // Rechecking recent Settled rows closes the crash/full-outbox window
        // without retaining finished selections beyond Slack's normal event
        // reconciliation window.
        var cutoff = now - _options.Value.SlackEventRetentionWindow;
        foreach (var claim in await prompts.ListSettledSinceAsync(cutoff, ct))
        {
            if (string.Equals(
                    claim.SettleReason,
                    NonInteractiveExpiryReason,
                    StringComparison.Ordinal))
                continue;

            try
            {
                await EnsureSettlementOutcomeAsync(
                    claim,
                    outbox,
                    claim.SettleReason ?? "selection_unrecoverable",
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to ensure settlement outcome for Slack Agent selection {SelectionId}",
                    claim.Id);
            }
        }
    }

    private static async Task<bool> OfferedInteractiveSelectionAsync(
        SlackAmbiguousPromptSnapshot claim,
        SlackOutboxStore outbox,
        CancellationToken ct)
    {
        var chooser = await outbox.FindByDispatchRefAsync(
            claim.ProjectId,
            claim.WinningConnectionId,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.PromptDispatchRef(
                claim.WorkspaceTeamId,
                claim.ConversationId,
                claim.MessageTs),
            ct);
        return chooser is not null && HasSelectionAction(chooser.PayloadJson);
    }

    internal static bool HasSelectionAction(string payloadJson)
    {
        var blocks = SlackDeliveryPayload.Parse(payloadJson).Blocks;
        return blocks is { ValueKind: JsonValueKind.Array }
            && blocks.Value.EnumerateArray()
                .SelectMany(block => block.TryGetProperty("elements", out var elements)
                    && elements.ValueKind == JsonValueKind.Array
                        ? elements.EnumerateArray().Select(element => element.Clone())
                        : [])
                .Any(element => element.TryGetProperty("action_id", out var actionId)
                    && string.Equals(
                        actionId.GetString(),
                        SlackSelectionActionPayload.ActionId,
                        StringComparison.Ordinal));
    }

    private static async Task EnsureSettlementOutcomeAsync(
        SlackAmbiguousPromptSnapshot claim,
        SlackOutboxStore outbox,
        string reason,
        CancellationToken ct)
    {
        var dispatchRef = SlackAmbiguousPromptStore.SettlementDispatchRef(
            claim.WorkspaceTeamId,
            claim.ConversationId,
            claim.MessageTs);
        if (await outbox.FindByDispatchRefAsync(
                claim.ProjectId,
                claim.WinningConnectionId,
                SlackOutboxKinds.UserAction,
                dispatchRef,
                ct) is not null)
            return;

        var text = reason == "expired"
            ? "This Agent selection expired. Please re-mention a single Bot."
            : "This Agent selection could not be completed. Please re-mention a single Bot.";
        await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
            claim.ProjectId,
            claim.WinningConnectionId,
            claim.WorkspaceTeamId,
            claim.ConversationId,
            SlackOutboxKinds.UserAction,
            dispatchRef,
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                Text: text,
                FallbackText: text,
                ResponseKind: reason)),
            claim.ThreadTs), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DelayAsync(InitialDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPendingAsync(stoppingToken);
            await DelayAsync(Interval, stoppingToken);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
