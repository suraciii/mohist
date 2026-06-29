## Why

RunnerGrain re-registers the `work-timeout` reminder on every work assignment via `RegisterOrUpdateReminder`, which resets the reminder's due-time to the full period. Because the scan fires on a single runner-level reminder, assigning a new work item pushes the next timeout check later for *all* outstanding work, including older work closer to its deadline. Under sustained dispatch a work item can stay "running" well past the configured `WorkCompletionTimeout`, delaying failure synthesis, retry, and workflow convergence — and breaking the invariant that each work's deadline is derived from its own taken/created timestamp.

## What Changes

- RunnerGrain SHALL register the `work-timeout` reminder only when outstanding work transitions from zero to non-zero, and SHALL NOT re-register (reset its due-time) when additional work is assigned while the reminder already exists.
- The reminder SHALL keep a stable runner-level cadence while any work is pending or running; each work item's deadline continues to be derived from its own taken/created `TakenAt` timestamp, not from the latest registration time.
- RunnerGrain SHALL continue to unregister/stop the reminder when a scan observes no pending or running work (existing drain behavior preserved).
- Synthesis behavior is unchanged: work whose `now - TakenAt > WorkCompletionTimeout` is synthesized as `WorkResult(status="failed", reason="timeout")` via the existing `ReportWorkflowResultAsync` channel on the next stable scan tick.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `workflow-supervision`: The "Work 完成超时检测由持久 reminder 驱动" requirement gains a cadence-stability requirement: the reminder is registered once when outstanding work appears and is not re-registered on subsequent assignments, so additional workflow work or agent jobs do not postpone the next scan tick for previously outstanding work. The reminder is released only when outstanding work drains to zero. The per-work deadline basis (own `TakenAt`) and the synthesis path (`failed`/`reason=timeout` via `ReportWorkflowResultAsync`) are unchanged.

## Impact

- **Server** (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`): `EnsureWorkTimeoutReminderAsync` becomes a register-if-absent operation (check `GetReminder` before `RegisterOrUpdateReminder`); the due-time is no longer reset per assignment at `PollOneWorkflowAsync`/`AssignAgentJobAsync`. Drain-side unregister in `CheckWorkTimeoutsAsync` is preserved.
- **Server tests** (`packages/server/tests/.../Runner/Grain/RunnerWorkLedgerSpecs.cs`, `RunnerFailureSpecs.cs`): add coverage for (a) an older outstanding work item timing out even when newer work is assigned before the deadline, (b) reminder lifecycle when work drains to zero and later reappears, and (c) reminder/timer scheduling behavior (register-once vs. re-register) rather than only calling `CheckWorkTimeoutsAsync` directly.
- **No schema, config, or API changes**: `WorkCompletionTimeout` default (30 min), the `RunnerWorks` ledger, and the reminder name/period are unchanged. No new work-result states.
