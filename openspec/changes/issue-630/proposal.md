# Issue 630: Durable terminal report settlement

## Why

Production evidence on current master shows that a Runner Agent turn can complete and its cleanup turn can complete while the owning Workflow task remains running until the result-settlement deadline emits `agent-result-unconfirmed`. The Runner already persists terminal results in its WorkResult journal and retries reports when the Server does not confirm durable tracking. The Server report route currently converts every Workflow acknowledgement except `missing-workflow` into `tracked=true`, including `stale`. That allows a stale report to delete the Runner's local settlement obligation even though the Workflow did not commit the result.

## What changes

- Make Workflow report acknowledgement strict: only an accepted durable Workflow transition returns `tracked=true`.
- Keep `tracked=false` for missing, stale, binding-mismatch, deadline-raced, and otherwise uncommitted reports so the Runner retains its journal entry and retries.
- Persist a terminal result fingerprint on each completed or failed task attempt.
- Accept a replay only when its complete terminal result and frozen attempt identity match the already committed attempt. A conflicting result, different artifact set, different follow-up tasks, or different execution binding remains stale.
- Preserve existing Artifact upload/bind ordering, agent binding fences, result-settlement deadlines, and independent task-log delivery.

## Non-goals

- No rewrite of the WorkResult journal or Artifact storage model.
- No changes to Runner presence, receipt retry budgets, cleanup classification, or task-log acknowledgement.
- No new message bus, polling endpoint, or cross-domain framework.
