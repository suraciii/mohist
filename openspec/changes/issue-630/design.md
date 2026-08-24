# Design: terminal report settlement

## Current boundary

`RunnerRoutes` receives a terminal report and delegates Workflow validation and mutation to `WorkflowReportService`, which calls `IWorkflowGrain.ReceiveTaskReportAsync`. The Runner removes a journal entry only after the response says `tracked=true`. A `ReportAck.Stale` means that the owning aggregate rejected the attempt; it is not a durable acknowledgement.

## Chosen design

1. `RunnerRoutes` maps Workflow `accepted` to `tracked=true` and every other Workflow acknowledgement to `tracked=false`.
2. The report service computes the existing canonical `RuntimeRecoveryReceiptFingerprint` from the original `WorkResult` and passes it to the Workflow grain. This reuses the already-defined cross-language result identity, including Artifact upload IDs and follow-up tasks.
3. `TaskRun` stores the fingerprint alongside its existing terminal state. It is an execution fact, not a new authority or a second result model.
4. `ReceiveTaskReportAsync` first validates the normal active-attempt path. If the attempt is already terminal, it validates the same worker/agent execution binding and returns `Accepted` only when the incoming fingerprint equals the stored fingerprint. It performs no mutation, Artifact binding, follow-up projection, or event append on replay.
5. A terminal attempt with no fingerprint, a mismatched fingerprint, a different worker, a different Agent binding, or a missing attempt remains `Stale`.

## Why this is smallest

The existing Workflow aggregate already retains task identity, worker identity, Agent settlement binding, terminal status, and Artifact outcome. One persisted fingerprint closes the response-loss replay window without introducing a receipt ledger or changing the authoritative task state machine. Strict API mapping preserves the Runner's existing durable retry behavior.

## Failure behavior

- `accepted`: owning Workflow committed the transition; Runner may acknowledge its journal.
- `stale`: no durable mutation was committed for this report; Runner must retain and retry its journal entry. If the attempt is permanently stale, existing reconciliation/stop paths remain authoritative.
- Identical terminal replay: `accepted`, with no duplicate events or Artifact binding.
- Conflicting terminal replay: `stale`, with no side effects.

## Verification

Use deterministic Server specs for accepted reports, strict stale HTTP acknowledgement, identical response-loss replay, mismatched replay, and the absence of an `agent-result-unconfirmed` event after an accepted transition. Existing Runner tests continue to prove that `tracked=false` retains `awaitingAck`.
