# Runner work-result persistence recovery

## Why

The runner currently treats a local work-result journal write failure as a
permanent journal failure.  It rolls a returned `WorkItemResult` back to the
durable `started` fence, then keeps the work in flight without retaining the
result that could be persisted once local storage recovers.  A temporary
filesystem failure can therefore lose the only result available to the live
process.

## What changes

- Retain an exact returned work result in the in-memory work-result journal
  when its completion record cannot yet be written.
- Gate new work admission and result reporting while retained completions are
  not durable.
- Retry that persistence at the next successful control-plane poll boundary,
  then promote only durably recorded results to normal report-and-ack
  delivery.

## Non-goals

- Do not derive a result for a historical `started` entry from agent session
  state, runtime events, idle status, logs, or a completed physical turn.
- Do not replay, stop, or otherwise alter an unresolved work item.
- Do not make terminal task-log persistence itself authoritative for a result;
  this change applies after the host has an exact returned `WorkItemResult`.
